using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Qisland.App;

// Codex 审批卡片 + 额度环
public partial class IslandWindow
{
    public ApprovalBroker? Approvals { get; set; }
    public CodexQuota? Quota { get; set; }
    private int _lastApprovals;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>有新请求：岛自动展开到首页并一直停住，直到你点了或超时。</summary>
    public void OnApprovalsChanged()
    {
        var n = Approvals?.Pending.Count ?? 0;
        var grew = n > _lastApprovals;
        _lastApprovals = n;
        _expDirty = true;
        if (grew)
        {
            _page = Page.Home;
            _peekUntil = DateTime.Now + ApprovalBroker.Wait;
            if (_mode == Mode.Expanded) RebuildExpanded();
            SetMode(Mode.Expanded);
            Kick();
        }
        else
        {
            if (n == 0 && _peekUntil > DateTime.Now.AddSeconds(1)) _peekUntil = DateTime.Now.AddSeconds(1);
            if (_mode == Mode.Expanded && _page == Page.Home && !_animating) RebuildExpanded();
        }
    }

    public void OnQuotaChanged()
    {
        if (_mode == Mode.Expanded && _page == Page.Home && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    private IEnumerable<FrameworkElement> ApprovalCards()
    {
        foreach (var p in (Approvals?.Pending ?? []).Take(2)) yield return ApprovalCard(p);
    }

    private FrameworkElement ApprovalCard(PendingApproval p)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(18), Background = Theme.Card, BorderBrush = Theme.CardRim,
            BorderThickness = new Thickness(Theme.CardRim == null ? 0 : 1), Padding = new Thickness(14, 12, 12, 12), Margin = new Thickness(0, 0, 0, 10),
        };
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = Tile(p.Source, 22, 6.5);
        head.Children.Add(tile);
        var h = Txt(12.5, FontWeights.SemiBold, Theme.Primary);
        var dsh = p.Source == "deepseek-harness";
        var name = p.Source switch { "codex" => "Codex", "deepseek-harness" => "DeepSeek", "claude-code" => "Claude", _ => p.Source };
        h.Text = $"{name} 请求{(p.Tool is "Bash" or "shell" or "exec_command" or "exec" or "bash" ? "执行命令" : "使用 " + p.Tool)}" + (p.Project.Length > 0 ? "  ·  " + p.Project : "");
        h.Margin = new Thickness(8, 0, 0, 0); h.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(h, 1);
        head.Children.Add(h);
        sp.Children.Add(head);

        var cmd = new TextBlock
        {
            Text = p.Command, FontFamily = Mono, FontSize = 12.5, Foreground = Theme.Primary, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = p.Command,
        };
        sp.Children.Add(new Border
        {
            Background = Theme.Fill, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 9, 0, 9), Child = cmd,
        });

        var foot = new Grid();
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = (int)Math.Max(0, (ApprovalBroker.Wait - (DateTime.Now - p.At)).TotalSeconds);
        var hint = Txt(11, FontWeights.Medium, Theme.Tertiary);
        hint.Text = dsh ? $"{left} 秒内不点就回 DSH 里问你" : $"{left} 秒内不点就回终端问你";
        hint.VerticalAlignment = VerticalAlignment.Center;
        foot.Children.Add(hint);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(Pill(dsh ? "回 DSH" : "回终端", Theme.Fill, Theme.Primary, () => Approvals?.Decide(p.Id, "")));
        btns.Children.Add(Pill("拒绝", Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x45, 0x3A))), Theme.Red, () => Approvals?.Decide(p.Id, "deny")));
        btns.Children.Add(Pill("允许", Theme.Green, Brushes.White, () => Approvals?.Decide(p.Id, "allow")));
        Grid.SetColumn(btns, 1);
        foot.Children.Add(btns);
        sp.Children.Add(foot);
        b.Child = sp;
        return b;
    }

    /// <summary>Codex 在忙、有审批，或额度用得多了才出现在首页。</summary>
    private FrameworkElement? QuotaCard(bool force = false)
    {
        var q = Quota;
        if (q is not { Fresh: true }) return null;
        var p5 = q.Primary?.UsedPercent ?? 0;
        var pw = q.Secondary?.UsedPercent ?? 0;
        var busy = _store.Ranked().Any(a => a.Source == "codex") || (Approvals?.Pending.Count ?? 0) > 0;
        if (!force && !busy && p5 < 60 && pw < 80) return null;

        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        var tile = Tile("codex", 26, 7.5); tile.HorizontalAlignment = HorizontalAlignment.Left;
        label.Children.Add(tile);
        var pl = Txt(10.5, FontWeights.Medium, Theme.Tertiary); pl.Text = "额度" + (q.Plan.Length > 0 ? " · " + char.ToUpper(q.Plan[0]) + q.Plan[1..] : ""); pl.Margin = new Thickness(0, 4, 0, 0);
        label.Children.Add(pl);
        g.Children.Add(label);
        if (q.Primary is { } a) { var r = QuotaRing("5 小时", a); Grid.SetColumn(r, 1); g.Children.Add(r); }
        if (q.Secondary is { } w) { var r = QuotaRing("本周", w); Grid.SetColumn(r, 2); g.Children.Add(r); }
        return Glassy(g);
    }

    private static readonly string[] WeekCn = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    private static FrameworkElement QuotaRing(string name, CodexQuota.Window w)
    {
        var used = Math.Clamp(w.UsedPercent / 100, 0, 1);
        (Color, Color) grad = used >= 0.9 ? (Color.FromRgb(0xFF, 0x6B, 0x5E), Color.FromRgb(0xFF, 0x2D, 0x55))
            : used >= 0.7 ? (Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x9F, 0x0A))
            : (Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x30, 0xD1, 0x58));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var box = new Grid { Width = 40, Height = 40 };
        var ring = new RingArc { Thickness = 4.5, Gradient = grad, Track = Theme.Fill, Progress = Math.Max(used, 0.015) };
        box.Children.Add(ring);
        var pct = Txt(11, FontWeights.SemiBold, Theme.Primary); pct.Text = $"{w.UsedPercent:0}%";
        pct.HorizontalAlignment = HorizontalAlignment.Center; pct.VerticalAlignment = VerticalAlignment.Center;
        box.Children.Add(pct);
        row.Children.Add(box);
        var sp = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var t = Txt(13, FontWeights.SemiBold, Theme.Primary); t.Text = name; sp.Children.Add(t);
        var s = Txt(10.5, FontWeights.Medium, Theme.Tertiary);
        s.Text = w.ResetsAt == DateTime.MinValue ? $"剩 {100 - w.UsedPercent:0}%"
            : w.ResetsAt.Date == DateTime.Today ? $"{w.ResetsAt:HH:mm} 重置"
            : w.ResetsAt.Date == DateTime.Today.AddDays(1) ? $"明天 {w.ResetsAt:HH:mm} 重置"
            : $"{WeekCn[(int)w.ResetsAt.DayOfWeek]} {w.ResetsAt:HH:mm} 重置";
        sp.Children.Add(s);
        row.Children.Add(sp);
        return row;
    }
}
