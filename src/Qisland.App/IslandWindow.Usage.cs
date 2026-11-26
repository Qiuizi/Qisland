using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 4.3：AI 页里「本机 AI 工具」额度（Claude Code / Cursor / Gemini CLI / Kimi Code）。设置里关掉就完全不画、不请求。
/// </summary>
public sealed partial class IslandWindow
{
    public AiUsageService? Usage { get; set; }
    public QuotaWatch? QuotaWatch { get; set; }

    private static (Color, Color) LevelGradient(double used) =>
        used >= 90 ? (Color.FromRgb(0xFF, 0x6B, 0x5E), Color.FromRgb(0xFF, 0x2D, 0x55))
        : used >= 70 ? (Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x9F, 0x0A))
        : (Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58));

    private static Brush LevelBrush(double used) => used >= 90 ? Theme.Red : used >= 70 ? Theme.Orange : Theme.Primary;

    // ───────────── AI 页：本机 AI 工具额度 ─────────────

    /// <summary>每个工具一行：图标 + 名字，右边每个窗口一格（用量 % + 细进度条），悬停看重置时间。</summary>
    private FrameworkElement? UsageSection()
    {
        var snaps = Usage?.Fresh().ToList() ?? [];
        if (snaps.Count == 0) return null;
        var rows = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        foreach (var s in snaps)
        {
            var g = new Grid { MinHeight = 40, Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var tile = Tile(s.Tool == "claude" ? "claude-code" : s.Tool, 22, 6.5); tile.VerticalAlignment = VerticalAlignment.Center;
            name.Children.Add(tile);
            var nsp = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var nt = Txt(12.5, FontWeights.SemiBold, Theme.Primary); nt.Text = AiUsage.Short(s.Tool); nsp.Children.Add(nt);
            if (PlanName(s.Plan) is { Length: > 0 } plan) { var pt = Txt(10, FontWeights.Medium, Theme.Tertiary); pt.Text = plan; nsp.Children.Add(pt); }
            name.Children.Add(nsp);
            g.Children.Add(name);
            var cells = s.Windows.Take(3).ToList();
            for (var i = 0; i < cells.Count; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var cell = UsageCell(cells[i]);
                Grid.SetColumn(cell, i + 1);
                g.Children.Add(cell);
            }
            rows.Children.Add(g);
        }
        return Glassy(rows);
    }

    private static string PlanName(string plan) => plan.ToLowerInvariant() switch
    {
        "" => "",
        "max" => "Max",
        "pro" => "Pro",
        "team" => "Team",
        "enterprise" => "Enterprise",
        "free" => "Free",
        "level_intermediate" => "Allegretto",
        _ when plan.StartsWith("LEVEL_", StringComparison.OrdinalIgnoreCase) => "",
        _ => plan.Length > 14 ? "" : plan,
    };

    private static FrameworkElement UsageCell(AiUsage.Window w)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Background = Brushes.Transparent };
        var top = new Grid();
        var l = Txt(10.5, FontWeights.Medium, Theme.Tertiary); l.Text = w.Label; top.Children.Add(l);
        var v = Txt(12, FontWeights.SemiBold, LevelBrush(w.UsedPercent)); v.Text = $"{w.UsedPercent:0}%"; v.HorizontalAlignment = HorizontalAlignment.Right; top.Children.Add(v);
        sp.Children.Add(top);
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Fill, Margin = new Thickness(0, 4, 0, 0) };
        var (a, b) = LevelGradient(w.UsedPercent);
        var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = Theme.Freeze(new LinearGradientBrush(a, b, 0)) };
        track.Child = fill;
        var used = Math.Clamp(w.UsedPercent / 100, 0, 1);
        track.SizeChanged += (_, e) => fill.Width = Math.Max(4, e.NewSize.Width * used);
        sp.Children.Add(track);
        if (w.ResetsAt is { } r) sp.ToolTip = QuotaWatch.ResetText(r.ToLocalTime());
        return sp;
    }

    public void OnUsageChanged()
    {
        _pageCache.Remove(Page.Agents);
        if (_mode == Mode.Expanded && _page == Page.Agents && !_animating) RebuildExpanded(); else _expDirty = true;
    }
}
