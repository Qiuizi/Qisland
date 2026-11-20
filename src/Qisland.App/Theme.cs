using System.Windows;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

internal static class Theme
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    /// <summary>Glass = 磨砂玻璃（存档里沿用「Glass」）；Liquid = 液态玻璃（通透、少磨砂、边缘折射带 + 镜面高光）。</summary>
    public enum Look { Dark, Light, Glass, Liquid }
    public static Look Current { get; private set; } = Look.Dark;
    /// <summary>背后要垫模糊底（系统合成或截屏）的外观。</summary>
    public static bool IsGlass => Current is Look.Glass or Look.Liquid;
    public static bool IsLiquid => Current == Look.Liquid;
    /// <summary>液态玻璃跟随系统深浅色：深色时文字变亮、底色偏暗。</summary>
    public static bool LiquidDark { get; private set; }
    /// <summary>液态玻璃的内圈亮边（离外沿 2–3 DIP 的一圈斜面反光）。</summary>
    public static Brush? InnerRim { get; private set; }
    /// <summary>液态玻璃左上角的一团镜面高光（动画时随弹簧晃）。</summary>
    public static Brush? Specular { get; private set; }
    /// <summary>液态玻璃里小按钮 / 胶囊标签的细亮边（其它外观为 null，不加边）。</summary>
    public static Brush? ChipRim { get; private set; }
    /// <summary>液态玻璃里卡片内圈的斜面亮边（贴在卡片内容层外沿；其它外观为 null）。</summary>
    public static Brush? CardInner { get; private set; }

    /// <summary>
    /// 统一的「表面」样式：卡片 / 列表容器 / 输入框都走这里，按当前外观给底色、外沿亮边和（液态玻璃的）内圈亮边。
    /// 静态画刷，建好就不再改，不加任何逐帧开销。
    /// </summary>
    public static void Surface(System.Windows.Controls.Border b)
    {
        b.Background = Card;
        b.BorderBrush = CardRim;
        b.BorderThickness = new Thickness(CardRim == null ? 0 : IsLiquid ? 1.2 : 1);
    }

    /// <summary>小按钮 / 标签：液态玻璃加一圈细亮边。</summary>
    public static void ChipSurface(System.Windows.Controls.Border b)
    {
        if (ChipRim == null) return;
        b.BorderBrush = ChipRim;
        b.BorderThickness = new Thickness(0.8);
    }

    public static Brush Primary { get; private set; } = null!;
    public static Brush Secondary { get; private set; } = null!;
    public static Brush Tertiary { get; private set; } = null!;
    public static Brush Fill { get; private set; } = null!;
    public static Brush Hover { get; private set; } = null!;
    public static Brush Press { get; private set; } = null!;
    /// <summary>胶囊底色。深色：近黑渐变；白色：近白渐变；液态玻璃：半透明白，背后是系统亚克力模糊。</summary>
    public static Brush Pill { get; private set; } = null!;
    /// <summary>玻璃的高光描边：上沿亮、下沿几乎透明，模拟液体表面的反光。</summary>
    public static Brush? Rim { get; private set; }
    /// <summary>玻璃内部从上往下的一层柔光。</summary>
    public static Brush? Sheen { get; private set; }
    /// <summary>卡片底：深色/白色主题就是 Fill；玻璃主题是一层半透明白，像 iOS 小组件叠在毛玻璃上。</summary>
    public static Brush Card { get; private set; } = null!;
    public static Brush CardHover { get; private set; } = null!;
    /// <summary>卡片 1px 高光边（只玻璃主题有）。</summary>
    public static Brush? CardRim { get; private set; }
    /// <summary>分段控件里选中那一格。</summary>
    public static Brush SegOn { get; private set; } = null!;
    public static byte ShadowAlpha { get; private set; } = 0x22;
    public static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    public static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    public static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    public static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    static Theme() => Apply(Look.Dark);

    private static SolidColorBrush S(byte a, byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    public static void Apply(Look look)
    {
        Current = look;
        Rim = null; Sheen = null; CardRim = null; InnerRim = null; Specular = null; ChipRim = null; CardInner = null; LiquidDark = false;
        switch (look)
        {
            case Look.Light:
                Primary = S(0xFF, 0x1D, 0x1D, 0x1F);
                Secondary = S(0xA6, 0x3C, 0x3C, 0x43);
                Tertiary = S(0x73, 0x3C, 0x3C, 0x43);
                Fill = S(0x17, 0x00, 0x00, 0x00);
                Hover = S(0x0D, 0x00, 0x00, 0x00);
                Press = S(0x1A, 0x00, 0x00, 0x00);
                Pill = Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF2, 0xF2, 0xF5), 90));
                Rim = Freeze(new LinearGradientBrush(Color.FromArgb(0x14, 0, 0, 0), Color.FromArgb(0x1F, 0, 0, 0), 90));
                ShadowAlpha = 0x26;
                Card = Fill; CardHover = S(0x22, 0x00, 0x00, 0x00); SegOn = S(0xFF, 0xFF, 0xFF, 0xFF);
                break;
            case Look.Glass:
                Primary = S(0xFF, 0x12, 0x12, 0x14);
                Secondary = S(0xB3, 0x2A, 0x2A, 0x30);
                Tertiary = S(0x80, 0x2A, 0x2A, 0x30);
                Fill = S(0x1F, 0x00, 0x00, 0x00);
                Hover = S(0x2E, 0xFF, 0xFF, 0xFF);
                Press = S(0x14, 0x00, 0x00, 0x00);
                // 背后是真实模糊（GlassBackdrop），这里只罩一层薄薄的白：通透来自少盖，可读来自模糊后的低对比
                Pill = Freeze(new LinearGradientBrush(Color.FromArgb(0x8C, 0xFC, 0xFC, 0xFE), Color.FromArgb(0x66, 0xF2, 0xF4, 0xF8), 90));
                var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF), 0));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), 0.35));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.75));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 1));
                Rim = Freeze(rim);
                var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 0));
                sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45));
                Sheen = Freeze(sheen);
                ShadowAlpha = 0x18;
                Card = S(0x48, 0xFF, 0xFF, 0xFF); CardHover = S(0x70, 0xFF, 0xFF, 0xFF); SegOn = S(0xE0, 0xFF, 0xFF, 0xFF);
                var cr = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0.3, 1) };
                cr.GradientStops.Add(new GradientStop(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 0));
                cr.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), 0.5));
                cr.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1));
                CardRim = Freeze(cr);
                break;
            case Look.Liquid:
                ApplyLiquid(!SystemLook.AppsLight);
                break;
            default:
                Primary = S(0xFF, 0xF5, 0xF5, 0xF7);
                Secondary = S(0x9E, 0xEB, 0xEB, 0xF5);
                Tertiary = S(0x66, 0xEB, 0xEB, 0xF5);
                Fill = S(0x1F, 0xFF, 0xFF, 0xFF);
                Hover = S(0x14, 0xFF, 0xFF, 0xFF);
                Press = S(0x24, 0xFF, 0xFF, 0xFF);
                Pill = Freeze(new LinearGradientBrush(Color.FromRgb(0x12, 0x12, 0x14), Color.FromRgb(0x02, 0x02, 0x03), 90));
                ShadowAlpha = 0x22;
                Card = Fill; CardHover = Hover; SegOn = S(0x38, 0xFF, 0xFF, 0xFF);
                break;
        }
    }

    public static string LookName(Look l) => l switch { Look.Light => "白色", Look.Glass => "磨砂玻璃", Look.Liquid => "液态玻璃", _ => "深色" };

    /// <summary>
    /// 液态玻璃（参考 Apple iOS 26 Liquid Glass 的观感，和 vitrio / liquid-glass 等网页实现「合成器近似」那一档的做法）：
    /// 胶囊几乎不盖色、中间只有一点点磨砂（底板模糊只给三成），模糊集中在边缘一圈「折射带」；
    /// 外沿一道锐利的镜面描边（左上最亮、右下次亮，两端带一丝冷暖色差），内圈 2–3 DIP 一道斜面亮边，
    /// 左上一团柔和高光随弹簧晃动。深浅色跟随系统：浅色压一层极淡的白、深色压一层极淡的黑。
    /// </summary>
    private static void ApplyLiquid(bool dark)
    {
        LiquidDark = dark;
        if (dark)
        {
            Primary = S(0xFF, 0xF7, 0xF7, 0xFA);
            Secondary = S(0xC0, 0xEE, 0xEE, 0xF4);
            Tertiary = S(0x8C, 0xEE, 0xEE, 0xF4);
            Fill = S(0x24, 0xFF, 0xFF, 0xFF);
            Hover = S(0x1C, 0xFF, 0xFF, 0xFF);
            Press = S(0x2A, 0xFF, 0xFF, 0xFF);
            Pill = Freeze(new LinearGradientBrush(Color.FromArgb(0x52, 0x1A, 0x1C, 0x22), Color.FromArgb(0x6A, 0x0C, 0x0D, 0x10), 90));
            Card = S(0x22, 0xFF, 0xFF, 0xFF); CardHover = S(0x34, 0xFF, 0xFF, 0xFF); SegOn = S(0x48, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            Primary = S(0xFF, 0x0E, 0x0E, 0x10);
            Secondary = S(0xC8, 0x22, 0x22, 0x28);
            Tertiary = S(0x96, 0x22, 0x22, 0x28);
            Fill = S(0x1A, 0x00, 0x00, 0x00);
            Hover = S(0x30, 0xFF, 0xFF, 0xFF);
            Press = S(0x14, 0x00, 0x00, 0x00);
            Pill = Freeze(new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x2C, 0xF4, 0xF6, 0xFA), 90));
            Card = S(0x38, 0xFF, 0xFF, 0xFF); CardHover = S(0x5C, 0xFF, 0xFF, 0xFF); SegOn = S(0xD0, 0xFF, 0xFF, 0xFF);
        }
        // 外沿镜面描边：对角渐变，左上亮白 → 中段几乎透明 → 右下次亮；两端一丝冷 / 暖色差
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0xF4, 0xFB, 0xFF), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 0.18));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF), 0.85));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xC8, 0xFF, 0xF2, 0xF8), 1));
        Rim = Freeze(rim);
        // 内圈斜面亮边：上沿和下沿亮（光从顶部进来、在底部内壁折回），侧面淡
        var inner = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        inner.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x60 : (byte)0x90, 0xFF, 0xFF, 0xFF), 0));
        inner.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.3));
        inner.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.7));
        inner.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x38 : (byte)0x60, 0xFF, 0xFF, 0xFF), 1));
        InnerRim = Freeze(inner);
        // 顶部一层很薄的反光（比磨砂玻璃的柔光短得多，只在最上沿）
        var sheen = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x22 : (byte)0x38, 0xFF, 0xFF, 0xFF), 0));
        sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.22));
        Sheen = Freeze(sheen);
        var spec = new RadialGradientBrush { Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        spec.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x40 : (byte)0x70, 0xFF, 0xFF, 0xFF), 0));
        spec.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        Specular = Freeze(spec);
        ShadowAlpha = 0x14;
        // 里面的卡片 / 列表 / 按钮：同一套液态语言——几乎不盖色、上下沿一道淡淡的磨砂边（画在底色渐变里）、
        // 外沿对角镜面亮边（左上最亮）、内容层外沿一圈内斜面亮边。深色时底色压一点黑、提高文字对比
        // 中间留一层很薄的衬底（亮 23% / 暗 16%），岛压在杂乱背景上字也读得清；上下沿更亮一点就是那道磨砂边
        var cardTop = dark ? (byte)0x3A : (byte)0x74; var cardMid = dark ? (byte)0x28 : (byte)0x3A; var cardBot = dark ? (byte)0x30 : (byte)0x5C;
        var cardRgb = dark ? (byte)0x1C : (byte)0xFF;
        var card = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        card.GradientStops.Add(new GradientStop(Color.FromArgb(cardTop, cardRgb, cardRgb, cardRgb), 0));
        card.GradientStops.Add(new GradientStop(Color.FromArgb(cardMid, cardRgb, cardRgb, cardRgb), 0.12));
        card.GradientStops.Add(new GradientStop(Color.FromArgb(cardMid, cardRgb, cardRgb, cardRgb), 0.88));
        card.GradientStops.Add(new GradientStop(Color.FromArgb(cardBot, cardRgb, cardRgb, cardRgb), 1));
        Card = Freeze(card);
        CardHover = S(dark ? (byte)0x30 : (byte)0x50, 0xFF, 0xFF, 0xFF);
        var cr = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        cr.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, 0xF4, 0xFB, 0xFF), 0));
        cr.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 0.25));
        cr.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.55));
        cr.GradientStops.Add(new GradientStop(Color.FromArgb(0x70, 0xFF, 0xF4, 0xF8), 1));
        CardRim = Freeze(cr);
        var ci = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        ci.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x30 : (byte)0x58, 0xFF, 0xFF, 0xFF), 0));
        ci.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.25));
        ci.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.8));
        ci.GradientStops.Add(new GradientStop(Color.FromArgb(dark ? (byte)0x1C : (byte)0x34, 0xFF, 0xFF, 0xFF), 1));
        CardInner = Freeze(ci);
        var chip = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        chip.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 0));
        chip.GradientStops.Add(new GradientStop(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF), 0.6));
        chip.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), 1));
        ChipRim = Freeze(chip);
        Fill = S(dark ? (byte)0x22 : (byte)0x2C, 0xFF, 0xFF, 0xFF);
    }

    public static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    public static (Color tile, string glyph, string? text) Mark(string source) => source switch
    {
        "claude-code" or "claude" => (Color.FromRgb(0xD9, 0x77, 0x57), "", "✻"),
        "codex" => (Color.FromRgb(0x2B, 0x2B, 0x2E), "\uE756", null),
        "deepseek-harness" or "deepseek" => (Color.FromRgb(0x4D, 0x6B, 0xFE), "\uE756", null),
        "doubao" => (Color.FromRgb(0x2F, 0x6B, 0xFF), "\uE756", null),
        "trae" => (Color.FromRgb(0x32, 0xD7, 0x6B), "\uE756", null),
        "workbuddy" => (Color.FromRgb(0x00, 0x82, 0xFF), "\uE756", null),
        "agents" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE756", null),
        "info-station" => (Color.FromRgb(0x63, 0x66, 0xF1), "\uE8A5", null),
        "info-deadline" => (Color.FromRgb(0x63, 0x66, 0xF1), "\uE787", null),
        "coursedesk" or "course" => (Color.FromRgb(0x0B, 0x8F, 0x66), "\uE7BE", null),
        "class" => (Color.FromRgb(0x34, 0x7A, 0xF0), "\uE7BE", null),
        "homework" => (Color.FromRgb(0xAF, 0x52, 0xDE), "\uE70F", null),
        "jarvis" => (Color.FromRgb(0x25, 0x63, 0xEB), "\uE8F1", null),
        "wechat" => (Color.FromRgb(0x07, 0xC1, 0x60), "\uE8BD", null),
        "qq" => (Color.FromRgb(0x12, 0xB7, 0xF5), "\uE8BD", null),
        "timer" => (Color.FromRgb(0xFF, 0x9F, 0x0A), "\uE823", null),
        "battery" => (Color.FromRgb(0x30, 0xD1, 0x58), "\uEA93", null),
        "battery-low" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE850", null),
        "audio" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE7F6", null),
        "mic" => (Color.FromRgb(0xFF, 0x9F, 0x0A), "\uE720", null),
        "camera" => (Color.FromRgb(0x30, 0xD1, 0x58), "\uE714", null),
        "network" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE701", null),
        "network-off" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uEB55", null),
        "proxy" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE774", null),
        "call" => (Color.FromRgb(0x30, 0xD1, 0x58), "\uE717", null),
        "meeting" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE8AA", null),
        "calendar" or "agenda" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE787", null),
        "bluetooth" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE702", null),
        "bluetooth-low" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE702", null),
        "filecopy" => (Color.FromRgb(0xFF, 0xB3, 0x40), "\uE8C8", null),
        "download" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE896", null),
        "update" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE895", null),
        "todo" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE73E", null),
        "music" => (Color.FromRgb(0xE6, 0x00, 0x26), "\uE8D6", null),
        "qqmusic" => (Color.FromRgb(0x31, 0xC2, 0x7C), "\uE8D6", null),
        "spotify" => (Color.FromRgb(0x1D, 0xB9, 0x54), "\uE8D6", null),
        "kugou" => (Color.FromRgb(0x20, 0x8B, 0xF5), "\uE8D6", null),
        "kuwo" => (Color.FromRgb(0xFF, 0xB8, 0x00), "\uE8D6", null),
        "applemusic" => (Color.FromRgb(0xFA, 0x2D, 0x48), "\uE8D6", null),
        "player" => (Color.FromRgb(0xFA, 0x2D, 0x48), "\uE768", null),
        "foobar" => (Color.FromRgb(0x4A, 0x4A, 0x52), "\uE8D6", null),
        _ when source.StartsWith("exe:") => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE8D6", null),
        "potplayer" => (Color.FromRgb(0xF5, 0xC5, 0x18), "\uE714", null),
        "bilibili" => (Color.FromRgb(0xFB, 0x72, 0x99), "\uE714", null),
        "chrome" => (Color.FromRgb(0x42, 0x85, 0xF4), "\uE774", null),
        "edge" => (Color.FromRgb(0x0C, 0x8C, 0xE9), "\uE774", null),
        "firefox" => (Color.FromRgb(0xFF, 0x71, 0x39), "\uE774", null),
        "browser" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE774", null),
        _ => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE946", null),
    };

    public static string SourceName(string source) => source switch
    {
        "claude-code" or "claude" => "Claude Code",
        "codex" => "Codex",
        "deepseek-harness" or "deepseek" => "DeepSeek",
        "doubao" => "豆包",
        "trae" => "Trae",
        "workbuddy" => "WorkBuddy",
        "info-station" => "信息站",
        "info-deadline" => "截止",
        "coursedesk" or "course" => "课程",
        "class" => "课表",
        "homework" => "作业",
        "jarvis" => "JARVIS",
        "wechat" => "微信",
        "qq" => "QQ",
        "timer" => "计时",
        "battery" or "battery-low" => "电池",
        "audio" => "声音",
        "mic" => "麦克风",
        "camera" => "摄像头",
        "network" or "network-off" => "网络",
        "proxy" => "代理",
        "call" => "通话",
        "meeting" => "会议",
        "calendar" or "agenda" => "日历",
        "bluetooth" or "bluetooth-low" => "蓝牙",
        "filecopy" => "文件",
        "download" => "下载",
        "update" => "更新",
        "todo" => "待办",
        "music" => "网易云音乐",
        "qqmusic" => "QQ 音乐",
        "spotify" => "Spotify",
        "kugou" => "酷狗音乐",
        "kuwo" => "酷我音乐",
        "potplayer" => "PotPlayer",
        "applemusic" => "Apple Music",
        "foobar" => "foobar2000",
        "chrome" => "Chrome",
        "edge" => "Edge",
        "firefox" => "Firefox",
        _ => "通知",
    };

    public static string KindWord(ActivityKind k) => k switch
    {
        ActivityKind.Running => "进行中",
        ActivityKind.Done => "完成",
        ActivityKind.WaitingApproval => "待确认",
        ActivityKind.Error => "出错",
        ActivityKind.Warning => "提醒",
        _ => "",
    };

    public static readonly FontFamily Symbols = new("Segoe UI Symbol");

    /// <summary>Fluent 图标字体里没有天气图标，用 Segoe UI Symbol 的单色天气符号（WPF 不渲染彩色 emoji，单色正好统一）。</summary>
    public static string WeatherGlyph(int code, bool night = false) => code switch
    {
        0 or 1 => night ? "\u263E" : "\u2600",
        2 => night ? "\u2601" : "\u26C5",
        3 => "\u2601",
        45 or 48 => "\u2601",
        >= 51 and <= 67 or >= 80 and <= 82 => "\u2614",
        >= 71 and <= 77 or 85 or 86 => "\u2744",
        >= 95 => "\u26C8",
        _ => "\u2601",
    };

    public static bool IsNight(DateTime t) => t.Hour < 7 || t.Hour >= 19;

    public static string Ago(DateTime t)
    {
        var s = (DateTime.Now - t).TotalSeconds;
        if (s < 50) return "现在";
        if (s < 3600) return $"{(int)(s / 60)} 分钟";
        if (s < 86400) return $"{(int)(s / 3600)} 小时";
        return t.ToString("M/d");
    }
}
