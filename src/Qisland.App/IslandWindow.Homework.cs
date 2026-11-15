using Qisland.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Qisland.App;

public sealed partial class IslandWindow
{
    public HomeworkHub? Homework { get; set; }

    /// <summary>首页「作业 DDL」：最近 3 项没交的，3 小时内红、24 小时内橙；点一下打开对应课程页。</summary>
    private FrameworkElement? HomeworkCard()
    {
        var items = Homework?.Pending();
        if (items is not { Count: > 0 }) return null;
        var now = DateTime.Now;
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = Txt(11.5, FontWeights.Medium, Theme.Secondary);
        head.Text = items.Count > 3 ? $"待交作业 {items.Count} 项 · 最近 3 项" : $"待交作业 {items.Count} 项";
        head.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(head);
        var purple = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xAF, 0x52, 0xDE)));
        foreach (var h in items.Take(3))
        {
            var left = h.Deadline - now;
            var tone = left <= TimeSpan.FromHours(3) ? Theme.Red : left <= TimeSpan.FromHours(24) ? Theme.Orange : Theme.Secondary;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = left <= TimeSpan.FromHours(24) ? tone : purple, VerticalAlignment = VerticalAlignment.Center });
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var t = Txt(13, FontWeights.Medium, Theme.Primary);
            t.Text = h.Title;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            col.Children.Add(t);
            var c = Txt(11, FontWeights.Normal, Theme.Tertiary);
            c.Text = $"{h.Course} · {h.Platform}";
            c.TextTrimming = TextTrimming.CharacterEllipsis;
            col.Children.Add(c);
            Grid.SetColumn(col, 1); g.Children.Add(col);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var l = Txt(13, FontWeights.SemiBold, tone);
            l.FontFamily = Theme.Display;
            l.Text = h.Left(now);
            l.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(l);
            var d = Txt(11, FontWeights.Normal, Theme.Tertiary);
            d.Text = h.Deadline.Date == now.Date ? $"今天 {h.Deadline:HH:mm}" : h.Deadline.Date == now.Date.AddDays(1) ? $"明天 {h.Deadline:HH:mm}" : h.Deadline.ToString("M/d HH:mm");
            d.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(d);
            Grid.SetColumn(right, 2); g.Children.Add(right);
            g.ToolTip = $"{h.Kind} · {h.Course}\n{h.Title}\n{h.Deadline:yyyy-MM-dd HH:mm} 截止";
            if (h.Url.Length > 0)
            {
                g.Cursor = Cursors.Hand;
                var url = h.Url;
                g.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } };
            }
            sp.Children.Add(g);
        }
        return Glassy(sp);
    }

    public void OnHomeworkChanged() => OnCountdownChanged();
}
