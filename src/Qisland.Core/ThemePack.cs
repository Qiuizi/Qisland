using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>不依赖 WPF 的颜色（A、R、G、B）。</summary>
public readonly record struct Argb(byte A, byte R, byte G, byte B)
{
    /// <summary>认 #RGB、#RRGGBB、#RRGGBBAA（CSS 写法，透明度在最后）。别的写法一律不认。</summary>
    public static Argb? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (!s.StartsWith('#')) return null;
        var h = s[1..];
        if (h.Any(c => !Uri.IsHexDigit(c))) return null;
        byte X(string v) => byte.Parse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return h.Length switch
        {
            3 => new Argb(0xFF, X(new string(h[0], 2)), X(new string(h[1], 2)), X(new string(h[2], 2))),
            6 => new Argb(0xFF, X(h[..2]), X(h[2..4]), X(h[4..6])),
            8 => new Argb(X(h[6..8]), X(h[..2]), X(h[2..4]), X(h[4..6])),
            _ => null,
        };
    }

    public override string ToString() => A == 0xFF ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public Argb WithAlpha(byte a) => this with { A = a };

    /// <summary>叠在 <paramref name="under"/>（不透明）上面之后的颜色。</summary>
    public Argb Over(Argb under)
    {
        var a = A / 255.0;
        byte M(byte top, byte bottom) => (byte)Math.Round(top * a + bottom * (1 - a));
        return new Argb(0xFF, M(R, under.R), M(G, under.G), M(B, under.B));
    }

    /// <summary>WCAG 相对亮度（0 黑 – 1 白）。</summary>
    public double Luminance
    {
        get
        {
            static double C(byte v) { var c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            return 0.2126 * C(R) + 0.7152 * C(G) + 0.0722 * C(B);
        }
    }

    /// <summary>WCAG 对比度（1–21）。两边都按不透明算。</summary>
    public static double Contrast(Argb a, Argb b)
    {
        var (l1, l2) = (a.Luminance, b.Luminance);
        if (l1 < l2) (l1, l2) = (l2, l1);
        return (l1 + 0.05) / (l2 + 0.05);
    }
}

/// <summary>主题包里的材质：纯色、磨砂玻璃、液态玻璃。</summary>
public enum ThemeMaterial { Solid, Frosted, Liquid }

/// <summary>
/// 5.0 主题包（theme.json）。只写和基础外观不同的字段，没写的沿用当前外观。
/// 导入时逐项校验、钳位：颜色认不出就丢掉，数值夹到安全范围，文字和底色对比度不够就退回默认文字色，
/// 底色透明度有下限——坏文件 / 恶意文件最多是「不好看」，不会把岛变成看不见、点不着或者崩掉。
/// </summary>
public sealed record ThemePack
{
    public const int MaxBytes = 32 * 1024;
    public const string Format = "qisland-theme/1";
    public const double MinRadius = 14, MaxRadius = 34, MinOpacity = 0.6, MaxOpacity = 1;

    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public ThemeMaterial? Material { get; init; }
    /// <summary>胶囊底色（渐变上端）。</summary>
    public Argb? Background { get; init; }
    /// <summary>胶囊底色渐变下端；没写就跟上端一样。</summary>
    public Argb? BackgroundEnd { get; init; }
    public Argb? Card { get; init; }
    public Argb? Accent { get; init; }
    public Argb? Text { get; init; }
    public Argb? Subtext { get; init; }
    /// <summary>展开后岛的圆角（卡片圆角按比例跟着变）。</summary>
    public double? Radius { get; init; }
    /// <summary>整座岛的不透明度。</summary>
    public double? Opacity { get; init; }
    /// <summary>模糊强度 0–1（磨砂 / 液态玻璃的截屏模糊）。</summary>
    public double? Blur { get; init; }
    public string? Font { get; init; }

    /// <summary>纯色主题按底色深浅决定走深色还是白色外观。</summary>
    public bool DarkBase => Background is not { } bg || bg.WithAlpha(0xFF).Luminance < 0.4;

    private static readonly Regex FontOk = new(@"^[\p{L}\p{N} ,._\-]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Known = ["format", "qisland", "$schema", "name", "author", "version", "description", "material",
        "background", "backgroundEnd", "card", "accent", "text", "subtext", "radius", "opacity", "blur", "font"];

    /// <summary>
    /// 解析并校验。返回 null 表示整份不能用（不是 JSON 对象、太大、没名字），原因在 <paramref name="error"/>；
    /// 能用但有字段被丢掉 / 钳位时，说明写进 <paramref name="warnings"/>。
    /// </summary>
    public static ThemePack? Parse(string? json, out string? error, out List<string> warnings)
    {
        var w = new List<string>();
        warnings = w;
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "文件是空的"; return null; }
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) { error = "文件超过 32 KB，不像主题包"; return null; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 8 }); }
        catch (JsonException ex) { error = "不是合法的 JSON：" + ex.Message.Split('.')[0]; return null; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { error = "最外层要是一个 { } 对象"; return null; }
            string? Str(string key, int max)
            {
                if (!r.TryGetProperty(key, out var v)) return null;
                if (v.ValueKind != JsonValueKind.String) { w.Add($"{key} 不是文字，已忽略"); return null; }
                var s = Clean(v.GetString());
                if (s.Length > max) { w.Add($"{key} 太长，已截到 {max} 字"); s = s[..max]; }
                return s;
            }
            Argb? Color(string key)
            {
                var s = Str(key, 16);
                if (s == null) return null;
                var c = Argb.Parse(s);
                if (c == null) w.Add($"{key}「{s}」不是 #RRGGBB / #RRGGBBAA 颜色，已忽略");
                return c;
            }
            double? Num(string key, double min, double max)
            {
                if (!r.TryGetProperty(key, out var v)) return null;
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d)) { w.Add($"{key} 不是数字，已忽略"); return null; }
                var c = Math.Clamp(d, min, max);
                if (Math.Abs(c - d) > 1e-9) w.Add($"{key} = {d.ToString(CultureInfo.InvariantCulture)} 超出范围，已夹到 {c.ToString(CultureInfo.InvariantCulture)}");
                return c;
            }
            foreach (var p in r.EnumerateObject())
                if (!Known.Contains(p.Name)) w.Add($"不认识的字段 {p.Name}，已忽略");

            var name = Str("name", 24);
            if (string.IsNullOrWhiteSpace(name)) { error = "缺少主题名（name）"; return null; }
            ThemeMaterial? mat = null;
            if (Str("material", 16) is { } m)
            {
                mat = m.ToLowerInvariant() switch { "solid" => ThemeMaterial.Solid, "frosted" or "glass" => ThemeMaterial.Frosted, "liquid" => ThemeMaterial.Liquid, _ => null };
                if (mat == null) w.Add($"material「{m}」只能是 solid / frosted / liquid，已忽略");
            }
            string? font = null;
            if (Str("font", 80) is { Length: > 0 } f)
            {
                if (FontOk.IsMatch(f)) font = f;
                else w.Add("font 里有不允许的字符，已忽略");
            }
            var pack = new ThemePack
            {
                Name = name,
                Author = Str("author", 32) ?? "",
                Version = Str("version", 16) ?? "",
                Description = Str("description", 80) ?? "",
                Material = mat,
                Background = Color("background"),
                BackgroundEnd = Color("backgroundEnd"),
                Card = Color("card"),
                Accent = Color("accent"),
                Text = Color("text"),
                Subtext = Color("subtext"),
                Radius = Num("radius", MinRadius, MaxRadius),
                Opacity = Num("opacity", MinOpacity, MaxOpacity),
                Blur = Num("blur", 0, 1),
                Font = font,
            };
            return pack.Sanitized(w);
        }
    }

    /// <summary>控制字符、换行全部去掉（名字会显示在设置页和提示里）。</summary>
    private static string Clean(string? s) => new string((s ?? "").Where(c => !char.IsControl(c) && c is not '\u202E' and not '\u202D' and not '\u200F' and not '\u200E').ToArray()).Trim();

    /// <summary>
    /// 安全钳位：底色透明度下限（纯色 0.9、玻璃 0.15）、卡片透明度上限、文字对比度。
    /// 对比度按「底色叠在假想背景上」算：纯色叠黑、玻璃叠中灰（玻璃后面是什么都可能）。
    /// </summary>
    public ThemePack Sanitized(List<string>? warnings = null, Argb? fallbackBackground = null)
    {
        var p = this;
        var solid = (Material ?? ThemeMaterial.Solid) == ThemeMaterial.Solid;
        byte minA = solid ? (byte)0xE6 : (byte)0x26, maxA = solid ? (byte)0xFF : (byte)0xF2;
        Argb? ClampA(Argb? c, byte lo, byte hi, string key)
        {
            if (c is not { } v) return null;
            var a = Math.Clamp(v.A, lo, hi);
            if (a != v.A) warnings?.Add($"{key} 透明度超出安全范围，已调整");
            return v.WithAlpha(a);
        }
        p = p with
        {
            Background = ClampA(p.Background, minA, maxA, "background"),
            BackgroundEnd = ClampA(p.BackgroundEnd, minA, maxA, "backgroundEnd"),
            Card = ClampA(p.Card, 0x08, 0xC8, "card"),
            Accent = p.Accent?.WithAlpha(0xFF),
        };
        if ((p.Background ?? fallbackBackground) is { } bg)
        {
            var backdrop = solid ? new Argb(0xFF, 0, 0, 0) : new Argb(0xFF, 0x80, 0x80, 0x80);
            var under = bg.Over(backdrop);
            var end = (p.BackgroundEnd ?? bg).Over(backdrop);
            double Worst(Argb fg) => Math.Min(Argb.Contrast(fg.WithAlpha(0xFF).Over(under), under), Argb.Contrast(fg.WithAlpha(0xFF).Over(end), end));
            if (p.Text is { } t && Worst(t) < (solid ? 4.5 : 3.0)) { warnings?.Add("text 和底色对比度太低，已改用默认文字色"); p = p with { Text = null }; }
            if (p.Subtext is { } st && Worst(st) < (solid ? 2.6 : 2.0)) { warnings?.Add("subtext 和底色对比度太低，已改用默认副文字色"); p = p with { Subtext = null }; }
        }
        if (p.Text is { } tx) p = p with { Text = tx.WithAlpha(Math.Max(tx.A, (byte)0xD0)) };
        if (p.Subtext is { } sx) p = p with { Subtext = sx.WithAlpha(Math.Max(sx.A, (byte)0x80)) };
        return p;
    }

    /// <summary>差异合并：<paramref name="over"/> 里写了的字段盖掉 this 的。</summary>
    public ThemePack Merge(ThemePack over) => new()
    {
        Name = over.Name.Length > 0 ? over.Name : Name,
        Author = over.Author.Length > 0 ? over.Author : Author,
        Version = over.Version.Length > 0 ? over.Version : Version,
        Description = over.Description.Length > 0 ? over.Description : Description,
        Material = over.Material ?? Material,
        Background = over.Background ?? Background,
        BackgroundEnd = over.BackgroundEnd ?? (over.Background != null ? null : BackgroundEnd),
        Card = over.Card ?? Card,
        Accent = over.Accent ?? Accent,
        Text = over.Text ?? Text,
        Subtext = over.Subtext ?? Subtext,
        Radius = over.Radius ?? Radius,
        Opacity = over.Opacity ?? Opacity,
        Blur = over.Blur ?? Blur,
        Font = over.Font ?? Font,
    };

    /// <summary>导出成 theme.json（只写有值的字段，缩进好看，中文不转义）。</summary>
    public string ToJson()
    {
        var o = new Dictionary<string, object?> { ["format"] = Format, ["name"] = Name };
        if (Author.Length > 0) o["author"] = Author;
        if (Version.Length > 0) o["version"] = Version;
        if (Description.Length > 0) o["description"] = Description;
        if (Material is { } m) o["material"] = m switch { ThemeMaterial.Frosted => "frosted", ThemeMaterial.Liquid => "liquid", _ => "solid" };
        void C(string k, Argb? c) { if (c is { } v) o[k] = v.ToString(); }
        C("background", Background); C("backgroundEnd", BackgroundEnd); C("card", Card); C("accent", Accent); C("text", Text); C("subtext", Subtext);
        if (Radius is { } r) o["radius"] = Math.Round(r, 1);
        if (Opacity is { } op) o["opacity"] = Math.Round(op, 2);
        if (Blur is { } b) o["blur"] = Math.Round(b, 2);
        if (Font is { } f) o["font"] = f;
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>导入后存盘用的文件名：只留字母数字和中文，最长 32。</summary>
    public static string SafeFileName(string name)
    {
        var s = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (s.Length == 0) s = "theme";
        return (s.Length > 32 ? s[..32] : s) + ".json";
    }
}
