using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Qisland.App;

/// <summary>考试倒计时：主页一排胶囊；3 天内收起的岛上也常驻显示。</summary>
public sealed partial class IslandWindow
{
    public Countdowns? Countdown { get; set; }
    private readonly TextBlock _idleCountdown = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 1) };

    private static Brush CountdownBrush(int days) => days <= 2 ? Theme.Red : days <= 7 ? Theme.Orange : Theme.Secondary;

    public void OnCountdownChanged()
    {
        // 放在哪个槽位由收起胶囊编排决定（ArrangeIdle），这里只管内容和显隐
        _idleCountdown.FontFamily = Theme.Text;
        var near = Countdown?.Items.FirstOrDefault();
        var cls = Classes?.Current ?? (Classes?.Next is { } nx && nx.Start - DateTime.Now <= ClassWatcher.Lead ? nx : null);
        if (cls != null)
        {
            // 课优先：上课中显示下课时间，快上课显示开课时间
            var now = DateTime.Now;
            var nm = cls.Name.Length > 4 ? cls.Name[..4] : cls.Name;
            // 快上课了：教室比课名更要紧（走到楼下才想起在哪间）
            var room = Room(cls.Location);
            _idleCountdown.Text = cls.Start <= now ? nm : room.Length > 0 ? $"{room} {cls.Start:H:mm}" : $"{nm} {cls.Start:H:mm}";
            _idleCountdown.ToolTip = $"{cls.Name}  {cls.Start:HH:mm}–{cls.End:HH:mm}  {cls.Location}";
            _idleCountdown.Foreground = cls.Start <= now ? Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0x7A, 0xF0)))
                : cls.Start - now <= TimeSpan.FromMinutes(10) ? Theme.Orange : Theme.Secondary;
            _idleCountdown.Visibility = Visibility.Visible;
        }
        else if (near != null && near.Days <= 3)
        {
            _idleCountdown.Text = $"{near.Short} {near.Left}";
            _idleCountdown.Foreground = CountdownBrush(near.Days);
            _idleCountdown.Visibility = Visibility.Visible;
        }
        else _idleCountdown.Visibility = Visibility.Collapsed;
        UpdateIdleExtras();   // 中间的精简系统状态给倒计时让位
        _expDirty = true;
        if (_mode == Mode.Expanded && !_animating && _page == Page.Home) RebuildExpanded();
    }

    private FrameworkElement? CountdownStrip()
    {
        var items = Countdown?.Items.Take(3).ToList();
        if (items is not { Count: > 0 }) return null;
        var g = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Columns = items.Count, Margin = new Thickness(0, 2, 0, 10) };
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            var glass = Theme.CardRim != null;
            var normal = glass ? Theme.Card : Theme.Fill;
            var hover = glass ? Theme.CardHover : HoverFill;
            var b = new Border { CornerRadius = new CornerRadius(glass ? 18 : 14), Background = normal, BorderBrush = Theme.CardRim, BorderThickness = new Thickness(glass ? 1 : 0), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(i == 0 ? 0 : 4, 0, i == items.Count - 1 ? 0 : 4, 0), Cursor = Cursors.Hand, ToolTip = $"{it.Name}  {it.At:M月d日 dddd HH:mm}".Replace(" 00:00", "") };
            var sp = new StackPanel();
            var name = Txt(11.5, FontWeights.Medium, Theme.Secondary);
            name.Text = it.Name;
            sp.Children.Add(name);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 0) };
            if (it.Days > 2)
            {
                var n = new TextBlock { Text = it.Days.ToString(), FontFamily = Theme.Display, FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = CountdownBrush(it.Days) };
                row.Children.Add(n);
                var unit = Txt(12, FontWeights.Medium, Theme.Secondary); unit.Text = " 天"; unit.VerticalAlignment = VerticalAlignment.Bottom; unit.Margin = new Thickness(0, 0, 0, 4);
                row.Children.Add(unit);
            }
            else
            {
                var n = new TextBlock { Text = it.Left, FontFamily = Theme.Display, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = CountdownBrush(it.Days) };
                row.Children.Add(n);
            }
            sp.Children.Add(row);
            b.Child = sp;
            b.MouseEnter += (_, _) => b.Background = hover;
            b.MouseLeave += (_, _) => b.Background = normal;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", "\"" + Countdowns.PathName + "\"") { UseShellExecute = true });
            };
            g.Children.Add(b);
        }
        return g;
    }
}
