using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 「AI」页：所有 Agent 会话（Codex、Claude Code、DeepSeek、Gemini、Cursor、OpenCode…）的状态列表 + 额度环。
/// 只有 Codex / DeepSeek Harness 的审批卡带「允许 / 拒绝」；其它在等确认的只显示提示，点一下回到它自己的窗口去确认。
/// </summary>
public sealed partial class IslandWindow
{
    public AgentSessions Sessions { get; } = new();
    public DoubaoQuotaService? Doubao { get; set; }
    public List<AiClient.Balance> Balances { get; set; } = [];

    public void OnAgentEvent(IslandActivity a)
    {
        Sessions.Ingest(a, DateTime.Now);
        if (_mode == Mode.Expanded && _page == Page.Agents && !_animating) RebuildExpanded(); else { _expDirty = true; _pageCache.Remove(Page.Agents); }
    }

    public void OnAiQuotaChanged()
    {
        _pageCache.Remove(Page.Agents);
        if (_mode == Mode.Expanded && _page is Page.Agents or Page.Home && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    private void BuildAgents(StackPanel stack)
    {
        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        var ico = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6.5), Background = Theme.Fill, VerticalAlignment = VerticalAlignment.Center };
        var gl = Glyph("\uE99A", 12, Theme.Primary); gl.HorizontalAlignment = HorizontalAlignment.Center; ico.Child = gl;
        title.Children.Add(ico);
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "AI 助手"; tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        var list = Sessions.List(DateTime.Now);
        var n = Txt(12, FontWeights.Normal, Theme.Tertiary);
        var running = list.Count(s => s.Status is AgentStatus.Running or AgentStatus.Waiting);
        n.Text = list.Count == 0 ? "" : running > 0 ? $"  {running} 个进行中" : $"  {list.Count} 个会话";
        title.Children.Add(n);
        head.Children.Add(title);
        stack.Children.Add(head);

        foreach (var ac in ApprovalCards()) stack.Children.Add(ac);

        if (QuotaStrip() is { } q) stack.Children.Add(q);

        if (list.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary);
            empty.Text = "还没有 Agent 会话\nCodex / Claude Code / DeepSeek 已接好；Gemini、Cursor 等在托盘「接入其他 AI 工具」里逐个开启";
            empty.TextAlignment = TextAlignment.Center; empty.TextWrapping = TextWrapping.Wrap;
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 14, 0, 12);
            stack.Children.Add(empty);
            return;
        }
        var rows = new StackPanel();
        foreach (var s in list.Take(7)) rows.Children.Add(SessionRow(s));
        stack.Children.Add(Glassy(rows));
    }

    private FrameworkElement SessionRow(AgentSession s)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent, Cursor = Cursors.Hand, MinHeight = 38 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = Tile(s.Source, 24, 7); tile.VerticalAlignment = VerticalAlignment.Center; tile.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(tile);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var t = Txt(12.5, FontWeights.SemiBold, Theme.Primary);
        t.Text = AgentPolicy.Name(s.Source) + (s.Session is "default" or "" ? "" : " · " + (s.Session.Length > 8 ? s.Session[..8] : s.Session));
        mid.Children.Add(t);
        var d = Txt(11.5, FontWeights.Normal, Theme.Secondary);
        d.Text = s.Status == AgentStatus.Waiting && !AgentPolicy.CanApproveOnIsland(s.Source)
            ? "在等你确认 · 点一下回到它的窗口"
            : s.Detail.Length > 0 ? s.Detail : s.Title;
        mid.Children.Add(d);
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var (label, brush) = s.Status switch
        {
            AgentStatus.Waiting => ("等确认", Theme.Orange),
            AgentStatus.Running => ("进行中", Theme.Blue),
            AgentStatus.Error => ("出错", Theme.Red),
            _ => ("完成", Theme.Green),
        };
        var st = Txt(11.5, FontWeights.SemiBold, brush); st.Text = label; st.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(st);
        var el = Txt(10.5, FontWeights.Normal, Theme.Tertiary); el.Text = AgentSessions.Elapsed(s, DateTime.Now); el.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(el);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        AddHover(g);
        g.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var live = _store.Ranked().FirstOrDefault(x => x.Id == s.Key);
            if (live != null) Launcher.Open(live); else Launcher.Open(s.Source);
        };
        return g;
    }

    /// <summary>额度一排：Codex 5 小时 / 本周，豆包（连接了才有），DeepSeek / Kimi 余额（填了 Key 才有）。</summary>
    private FrameworkElement? QuotaStrip()
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        if (Quota is { Fresh: true } q)
        {
            if (q.Primary is { } a) wrap.Children.Add(Spaced(QuotaRing("Codex 5 小时", a)));
            if (q.Secondary is { } w) wrap.Children.Add(Spaced(QuotaRing("Codex 本周", w)));
        }
        if (Doubao is { Connected: true } db)
        {
            if (db.Fresh)
                foreach (var w in db.Last!.Windows.Take(2))
                    wrap.Children.Add(Spaced(QuotaRing("豆包 " + w.Label, new CodexQuota.Window(w.UsedPercent, (int)((w.Seconds ?? 0) / 60), w.ResetsAt ?? DateTime.MinValue))));
            else if (db.Error != null)
            {
                var e = Txt(11.5, FontWeights.Normal, Theme.Orange); e.Text = "豆包额度：" + db.Error; e.Margin = new Thickness(0, 4, 14, 4);
                wrap.Children.Add(e);
            }
        }
        foreach (var b in Balances)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 4, 18, 4), VerticalAlignment = VerticalAlignment.Center };
            var v = Txt(14, FontWeights.SemiBold, Theme.Primary); v.Text = b.Text; sp.Children.Add(v);
            var l = Txt(10.5, FontWeights.Medium, Theme.Tertiary); l.Text = b.Name + " 余额"; sp.Children.Add(l);
            wrap.Children.Add(sp);
        }
        return wrap.Children.Count == 0 ? null : Glassy(wrap);
    }

    private static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 4, 16, 4); return e; }
}
