using System.Windows;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

internal static class Theme
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public enum Look { Dark, Light, Glass }
    public static Look Current { get; private set; } = Look.Dark;
    public static bool IsGlass => Current == Look.Glass;

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
        Rim = null; Sheen = null;
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
                break;
            case Look.Glass:
                Primary = S(0xFF, 0x12, 0x12, 0x14);
                Secondary = S(0xB3, 0x2A, 0x2A, 0x30);
                Tertiary = S(0x80, 0x2A, 0x2A, 0x30);
                Fill = S(0x1F, 0x00, 0x00, 0x00);
                Hover = S(0x2E, 0xFF, 0xFF, 0xFF);
                Press = S(0x14, 0x00, 0x00, 0x00);
                Pill = Freeze(new LinearGradientBrush(Color.FromArgb(0xD8, 0xFA, 0xFB, 0xFD), Color.FromArgb(0xB8, 0xF0, 0xF2, 0xF7), 90));
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
                break;
        }
    }

    public static string LookName(Look l) => l switch { Look.Light => "白色", Look.Glass => "液态玻璃", _ => "深色" };

    public static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    public static (Color tile, string glyph, string? text) Mark(string source) => source switch
    {
        "claude-code" or "claude" => (Color.FromRgb(0xD9, 0x77, 0x57), "", "✻"),
        "codex" => (Color.FromRgb(0x2B, 0x2B, 0x2E), "\uE756", null),
        "deepseek-harness" or "deepseek" => (Color.FromRgb(0x4D, 0x6B, 0xFE), "\uE756", null),
        "agents" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE756", null),
        "info-station" => (Color.FromRgb(0x63, 0x66, 0xF1), "\uE8A5", null),
        "info-deadline" => (Color.FromRgb(0x63, 0x66, 0xF1), "\uE787", null),
        "coursedesk" or "course" => (Color.FromRgb(0x0B, 0x8F, 0x66), "\uE7BE", null),
        "jarvis" => (Color.FromRgb(0x25, 0x63, 0xEB), "\uE8F1", null),
        "wechat" => (Color.FromRgb(0x07, 0xC1, 0x60), "\uE8BD", null),
        "qq" => (Color.FromRgb(0x12, 0xB7, 0xF5), "\uE8BD", null),
        "timer" => (Color.FromRgb(0xFF, 0x9F, 0x0A), "\uE823", null),
        "battery" => (Color.FromRgb(0x30, 0xD1, 0x58), "\uEA93", null),
        "battery-low" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE850", null),
        "audio" => (Color.FromRgb(0x0A, 0x84, 0xFF), "\uE7F6", null),
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
        "info-station" => "信息站",
        "info-deadline" => "截止",
        "coursedesk" or "course" => "课程",
        "jarvis" => "JARVIS",
        "wechat" => "微信",
        "qq" => "QQ",
        "timer" => "计时",
        "battery" or "battery-low" => "电池",
        "audio" => "声音",
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
