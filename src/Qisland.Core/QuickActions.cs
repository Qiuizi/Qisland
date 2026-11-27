using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qisland.Core;

/// <summary>快捷按钮能做的四件事。</summary>
public enum QuickKind
{
    /// <summary>用默认浏览器打开 http / https 网页。</summary>
    Url,
    /// <summary>打开本机的程序、文件或文件夹（只认已存在的完整路径，不经过命令行）。</summary>
    Open,
    /// <summary>在岛上弹一条自己的提醒（不出网、不碰别的软件）。</summary>
    Notify,
    /// <summary>向本机回环地址（127.0.0.1 / localhost）发一个 HTTP 请求，例如 Qisland 自己的推送 API 或你本机的脚本服务。</summary>
    Local,
}

/// <summary>一个零代码快捷按钮：图标（emoji / 符号）+ 简短标题 + 动作。</summary>
public sealed record QuickAction(string Icon, string Title, QuickKind Kind, string Target, string Body = "");

/// <summary>
/// 5.0 自定义快捷按钮：存成 quick-actions.json。所有入口（设置页保存、读文件、点按钮执行前）都过同一套校验。
/// </summary>
public static class QuickActions
{
    public const int Max = 12, MaxTitle = 8, MaxBody = 4096;

    private sealed class Dto
    {
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
    }

    public static string KindKey(QuickKind k) => k switch { QuickKind.Open => "open", QuickKind.Notify => "notify", QuickKind.Local => "local", _ => "url" };

    public static string KindName(QuickKind k) => k switch { QuickKind.Open => "程序 / 文件", QuickKind.Notify => "岛上提醒", QuickKind.Local => "本机接口", _ => "网页" };

    /// <summary>读 quick-actions.json：坏文件返回空列表；单个按钮校验不过（格式层面）就跳过，最多 12 个。路径是否存在留到点的时候再查。</summary>
    public static List<QuickAction> Parse(string? json)
    {
        var list = new List<QuickAction>();
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024) return list;
        List<Dto>? raw;
        try { raw = JsonSerializer.Deserialize<List<Dto>>(json, new JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return list; }
        foreach (var d in raw ?? [])
        {
            if (list.Count >= Max) break;
            if (d == null) continue;
            QuickKind? kind = (d.Kind ?? "").Trim().ToLowerInvariant() switch { "url" => QuickKind.Url, "open" => QuickKind.Open, "notify" => QuickKind.Notify, "local" => QuickKind.Local, _ => null };
            if (kind is not { } k) continue;
            var a = new QuickAction(CleanIcon(d.Icon), CleanTitle(d.Title), k, (d.Target ?? "").Trim(), d.Body ?? "");
            if (Validate(a, null) == null) list.Add(a);
        }
        return list;
    }

    public static string Serialize(IEnumerable<QuickAction> list) =>
        JsonSerializer.Serialize(list.Take(Max).Select(a => new Dto { Icon = a.Icon, Title = a.Title, Kind = KindKey(a.Kind), Target = a.Target, Body = a.Body.Length > 0 ? a.Body : null }),
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }) + "\n";

    /// <summary>图标：最多 2 个字符元素（一个 emoji 或一两个符号）。空的用 ⚡。</summary>
    public static string CleanIcon(string? s)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) return "⚡";
        var e = StringInfo.GetTextElementEnumerator(s);
        var parts = new List<string>();
        while (e.MoveNext() && parts.Count < 2) parts.Add((string)e.Current);
        return string.Concat(parts);
    }

    /// <summary>标题：去控制字符、最多 8 个字（卡片里一行放得下）。</summary>
    public static string CleanTitle(string? s)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        var si = new StringInfo(s);
        return si.LengthInTextElements > MaxTitle ? si.SubstringByTextElements(0, MaxTitle) : s;
    }

    /// <summary>
    /// 校验一个按钮。返回 null = 可以用；否则是给人看的原因。
    /// <paramref name="exists"/> 传进来时还会检查程序 / 文件 / 文件夹是否真的存在（保存前、执行前都查）。
    /// </summary>
    public static string? Validate(QuickAction a, Func<string, bool>? exists)
    {
        if (a.Title.Length == 0) return "给按钮起个名字";
        return a.Kind switch
        {
            QuickKind.Url => SafeWebUrl(a.Target) == null ? "网址要以 http:// 或 https:// 开头，而且是完整的网址" : null,
            QuickKind.Open => ValidatePath(a.Target, exists),
            QuickKind.Notify => a.Target.Length == 0 ? "写一句要在岛上显示的话" : a.Target.Length > 120 ? "提醒最多 120 个字" : a.Target.Any(char.IsControl) ? "提醒里不能有控制字符" : null,
            QuickKind.Local => SafeLoopbackUrl(a.Target) == null ? "只能发到本机：http://127.0.0.1:端口/… 或 http://localhost:端口/…" : a.Body.Length > MaxBody ? "请求内容最多 4096 个字" : null,
            _ => "不认识的动作",
        };
    }

    /// <summary>只放行完整的 http / https 网址（不允许 javascript:、file:、带空白或控制字符的）。</summary>
    public static Uri? SafeWebUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim(' ');
        if (s.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || s.Length > 2048) return null;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(u.Host) || u.UserInfo.Length > 0) return null;
        return u;
    }

    /// <summary>只放行发往本机回环地址的 http(s) 请求，且必须写明端口。</summary>
    public static Uri? SafeLoopbackUrl(string? s)
    {
        var u = SafeWebUrl(s);
        if (u == null) return null;
        var host = u.Host.Trim('[', ']').ToLowerInvariant();
        if (host is not ("127.0.0.1" or "localhost" or "::1")) return null;
        if (u.IsDefaultPort && !(s ?? "").Contains(":" + u.Port, StringComparison.Ordinal)) return null;
        return u;
    }

    /// <summary>
    /// 程序 / 文件 / 文件夹路径：必须是完整的本机路径（C:\…），不能带引号、管道、重定向这类命令行字符，
    /// 也不收网络共享路径（\\server\share），执行时直接交给系统打开，不拼命令行。
    /// </summary>
    public static string? ValidatePath(string? path, Func<string, bool>? exists)
    {
        if (string.IsNullOrWhiteSpace(path)) return "选一个程序、文件或文件夹";
        path = path.Trim();
        if (path.Length > 260) return "路径太长";
        if (path.Any(c => char.IsControl(c) || c is '"' or '|' or '<' or '>' or '*' or '?' or '%' or '^' or '&')) return "路径里不能有引号、| < > * ? % ^ & 这类字符";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "不支持网络共享路径";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return "要写完整路径，比如 C:\\Program Files\\…";
        if (path.Contains("..", StringComparison.Ordinal)) return "路径里不能有 ..";
        if (exists != null && !exists(path)) return "这个路径不存在";
        return null;
    }
}
