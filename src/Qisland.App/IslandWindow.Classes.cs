using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Qisland.App;

public sealed partial class IslandWindow
{
    public ClassWatcher? Classes { get; set; }

    /// <summary>首页「今天的课」：只列今天还没下课的，正在上的那节标蓝点。</summary>
    private FrameworkElement? TodayClasses()
    {
        var left = Classes?.TodayLeft();
        if (left is not { Count: > 0 }) return null;
        var now = DateTime.Now;
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = Txt(11.5, FontWeights.Medium, Theme.Secondary);
        head.Text = left[0].Start <= now ? $"今天还有 {left.Count - 1} 节课" : $"今天还有 {left.Count} 节课";
        if (left.Count == 1 && left[0].Start <= now) head.Text = "最后一节课";
        head.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(head);
        var blue = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x7A, 0xF0)));
        foreach (var c in left.Take(4))
        {
            var on = c.Start <= now;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var dot = new Ellipse { Width = 6, Height = 6, Fill = on ? blue : Theme.Tertiary, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(dot);
            var t = Txt(14, FontWeights.SemiBold, on ? blue : Theme.Primary);
            t.FontFamily = Theme.Display;
            t.Text = c.Start.ToString("HH:mm");
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var n = Txt(13, FontWeights.Medium, Theme.Primary);
            n.Text = c.Name;
            n.TextTrimming = TextTrimming.CharacterEllipsis;
            n.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(n, 2); g.Children.Add(n);
            var loc = Txt(11.5, FontWeights.Normal, Theme.Secondary);
            loc.Text = on ? $"{c.Location}  {Math.Ceiling((c.End - now).TotalMinutes)} 分钟后下课".Trim() : c.Location;
            loc.VerticalAlignment = VerticalAlignment.Center;
            loc.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(loc, 3); g.Children.Add(loc);
            g.ToolTip = string.Join("\n", new[] { c.Name, $"{c.Start:HH:mm}–{c.End:HH:mm}", c.Location, c.Teacher }.Where(x => x.Length > 0));
            sp.Children.Add(g);
        }
        return Glassy(sp);
    }
}
