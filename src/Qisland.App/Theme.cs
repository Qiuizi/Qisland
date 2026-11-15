using System.Windows;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

internal static class Theme
{
    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly Brush Primary = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly Brush Secondary = Freeze(new SolidColorBrush(Color.FromArgb(0x9E, 0xEB, 0xEB, 0xF5)));
    public static readonly Brush Tertiary = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xEB, 0xEB, 0xF5)));
    public static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    public static readonly Brush Orange = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)));
    public static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    public static readonly Brush Blue = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));

    /// <summary>纯黑会和深色壁纸糊在一起，用极浅的竖向渐变给一点体积感。</summary>
    public static readonly Brush Pill = Freeze(new LinearGradientBrush(
        Color.FromRgb(0x12, 0x12, 0x14), Color.FromRgb(0x02, 0x02, 0x03), 90));

    public static T Freeze<T>(T b) where T : Freezable { b.Freeze(); return b; }

    public static (Color tile, string glyph, string? text) Mark(string source) => source switch
    {
        "claude-code" or "claude" => (Color.FromRgb(0xD9, 0x77, 0x57), "", "✻"),
        "codex" => (Color.FromRgb(0x2B, 0x2B, 0x2E), "\uE756", null),
        "deepseek-harness" or "deepseek" => (Color.FromRgb(0x4D, 0x6B, 0xFE), "\uE756", null),
        "agents" => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE756", null),
        "info-station" => (Color.FromRgb(0x63, 0x66, 0xF1), "\uE8A5", null),
        "coursedesk" or "course" => (Color.FromRgb(0x0B, 0x8F, 0x66), "\uE7BE", null),
        "jarvis" => (Color.FromRgb(0x25, 0x63, 0xEB), "\uE8F1", null),
        "wechat" => (Color.FromRgb(0x07, 0xC1, 0x60), "\uE8BD", null),
        "qq" => (Color.FromRgb(0x12, 0xB7, 0xF5), "\uE8BD", null),
        "timer" => (Color.FromRgb(0xFF, 0x9F, 0x0A), "\uE823", null),
        "todo" => (Color.FromRgb(0xFF, 0x45, 0x3A), "\uE73E", null),
        _ => (Color.FromRgb(0x5E, 0x5C, 0xE6), "\uE946", null),
    };

    public static string SourceName(string source) => source switch
    {
        "claude-code" or "claude" => "Claude Code",
        "codex" => "Codex",
        "deepseek-harness" or "deepseek" => "DeepSeek",
        "info-station" => "信息站",
        "coursedesk" or "course" => "课程",
        "jarvis" => "JARVIS",
        "wechat" => "微信",
        "qq" => "QQ",
        "timer" => "计时",
        "todo" => "待办",
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

    public static string WeatherGlyph(int code) => code switch
    {
        0 or 1 => "\uE706",
        _ => "\uE753",
    };

    public static string Ago(DateTime t)
    {
        var s = (DateTime.Now - t).TotalSeconds;
        if (s < 50) return "现在";
        if (s < 3600) return $"{(int)(s / 60)} 分钟";
        if (s < 86400) return $"{(int)(s / 3600)} 小时";
        return t.ToString("M/d");
    }
}
