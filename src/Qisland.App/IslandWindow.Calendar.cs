using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Qisland.Core;

namespace Qisland.App;

public sealed partial class IslandWindow
{
    public CalendarWatcher? Calendar { get; set; }

    public void OnCalendarChanged()
    {
        if (_mode == Mode.Expanded && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    /// <summary>课程组件里的「日程」：订阅日历里接下来的几个（今天剩下的 + 之后几天），有会议链接的带「加入」。</summary>
    private FrameworkElement? AgendaCard()
    {
        if (Calendar == null || !Features.On(Features.Calendar)) return null;
        var list = Calendar.Upcoming(4);
        if (list.Count == 0) return null;
        var now = DateTime.Now;
        var today = DateTime.Today;
        var sp = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = Txt(11.5, FontWeights.Medium, Theme.Secondary);
        head.Text = "日程";
        head.Margin = new Thickness(0, 0, 0, 4);
        sp.Children.Add(head);
        var red = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
        foreach (var e in list)
        {
            var on = e.Start <= now && !e.AllDay;
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = on ? red : Theme.Tertiary, VerticalAlignment = VerticalAlignment.Center });
            var t = Txt(13.5, FontWeights.SemiBold, on ? red : Theme.Primary);
            t.FontFamily = Theme.Display;
            var day = e.Start.Date == today ? "" : e.Start.Date == today.AddDays(1) ? "明天 " : e.Start.ToString("ddd ", new CultureInfo("zh-CN"));
            t.Text = e.AllDay ? day + "全天" : day + e.Start.ToString("HH:mm");
            t.MinWidth = 50; t.Margin = new Thickness(0, 0, 8, 0);
            t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(t, 1); g.Children.Add(t);
            var n = Txt(13, FontWeights.Medium, Theme.Primary);
            n.Text = e.Title;
            n.TextTrimming = TextTrimming.CharacterEllipsis;
            n.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(n, 2); g.Children.Add(n);
            if (e.JoinUrl is { } url)
            {
                var join = Chip("加入", () => Launcher.OpenUri(url, "calendar"), "加入" + e.JoinApp);
                join.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(join, 3); g.Children.Add(join);
            }
            else if (e.Location.Length > 0)
            {
                var loc = Txt(11.5, FontWeights.Normal, Theme.Secondary);
                loc.Text = e.Location.Length > 10 ? e.Location[..10] + "…" : e.Location;
                loc.VerticalAlignment = VerticalAlignment.Center;
                loc.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(loc, 3); g.Children.Add(loc);
            }
            g.ToolTip = string.Join("\n", new[] { e.Title, e.AllDay ? $"{e.Start:M/d} 全天" : $"{e.Start:M/d HH:mm}–{e.End:HH:mm}", e.Location, e.JoinApp ?? "" }.Where(x => x.Length > 0));
            sp.Children.Add(g);
        }
        return Glassy(sp);
    }
}
