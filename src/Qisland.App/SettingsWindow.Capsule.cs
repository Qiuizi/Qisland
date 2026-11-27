using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>4.3 设置「设备与状态」页里的收起胶囊系统状态。</summary>
public sealed partial class SettingsWindow
{
    // ───────────── 设备与状态 → 收起时的系统状态 ─────────────

    private void CapsuleSection(StackPanel p)
    {
        p.Children.Add(Section("收起时的系统状态"));
        var b = new StackPanel();
        b.Children.Add(DialogKit.Row("收起时显示系统状态", "岛收起时中间一行小字，每 2 秒刷新；课程 / 考试倒计时出现时让位。默认关",
            DialogKit.Switch(Features.On(Features.CapsuleStats), v =>
            {
                Features.Set(Features.CapsuleStats, v);
                // 5.0：打开时如果收起胶囊编排里把它藏起来了，顺手放回空闲胶囊（位置沿用编排里的）
                if (v && !CapsuleStore.Current.IsOn(CapsuleState.Idle, "stats") && !CapsuleStore.Current.IsOn(CapsuleState.Playing, "stats"))
                { var l = CapsuleStore.Current.Clone(); l.Set(CapsuleState.Idle, "stats", on: true); CapsuleStore.Save(l); }
                RenderSoon();
            })));
        if (Features.On(Features.CapsuleStats))
        {
            var items = IslandWindow.CapsuleItems;
            var pills = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (flag, label) in new[] { (CompactStats.Items.Cpu, "CPU"), (CompactStats.Items.Mem, "内存"), (CompactStats.Items.Net, "下载网速") })
            {
                var f = flag;
                pills.Children.Add(Pill(label, items.HasFlag(f), v =>
                {
                    var cur = IslandWindow.CapsuleItems;
                    cur = v ? cur | f : cur & ~f;
                    if (cur == CompactStats.Items.None) { RenderSoon(); return; }   // 至少留一项
                    IslandWindow.CapsuleItems = cur;
                    _app.Island?.UpdateIdleExtras();
                }));
            }
            b.Children.Add(DialogKit.Row("显示哪几项", "至少留一项；项越多胶囊越宽", pills));
        }
        p.Children.Add(Card(b));
    }

    /// <summary>可多选的文字圆角片（和分段选择同一套配色）。</summary>
    private static Border Pill(string text, bool on, Action<bool> changed)
    {
        var t = DialogKit.Text(text, 12.5, FontWeights.SemiBold, on ? Brushes.White : DialogKit.Fg);
        t.TextWrapping = TextWrapping.NoWrap;
        var b = new Border { Child = t, CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(6, 0, 0, 0), Background = on ? DialogKit.Accent : DialogKit.Faint, Cursor = Cursors.Hand, Tag = on };
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var v = !(bool)b.Tag; b.Tag = v;
            b.Background = v ? DialogKit.Accent : DialogKit.Faint; t.Foreground = v ? Brushes.White : DialogKit.Fg;
            changed(v);
        };
        return b;
    }
}
