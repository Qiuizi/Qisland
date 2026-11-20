using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Qisland.App;

/// <summary>
/// 小窗（待办提醒、本机日程、设置）共用的外观：无边框圆角卡片、跟随深浅主题、可拖动、Esc 关闭。
/// 开关、分段按钮、按钮都在这里，保证几个窗口长得一样。
/// </summary>
public static class DialogKit
{
    public static bool Dark => Theme.Current != Theme.Look.Light;
    public static Brush Fg => Dark ? Brushes.White : B(0x1C, 0x1C, 0x1E);
    public static Brush Sub => Dark ? BA(0x99, 0xFF, 0xFF, 0xFF) : BA(0x99, 0, 0, 0);
    public static Brush Faint => Dark ? BA(0x14, 0xFF, 0xFF, 0xFF) : BA(0x0C, 0, 0, 0);
    public static Brush Line => Dark ? BA(0x22, 0xFF, 0xFF, 0xFF) : BA(0x14, 0, 0, 0);
    public static Brush Surface => Dark ? B(0x1C, 0x1C, 0x1E) : B(0xF7, 0xF7, 0xF8);
    public static readonly Brush Accent = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)));
    public static readonly Brush Danger = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static Brush B(byte r, byte g, byte b) => Theme.Freeze(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static Brush BA(byte a, byte r, byte g, byte b) => Theme.Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    /// <summary>把窗口做成圆角卡片：标题 + 关闭叉 + 内容。</summary>
    public static void Shell(Window w, string title, UIElement body, double width)
    {
        w.Title = title;
        w.WindowStyle = WindowStyle.None; w.AllowsTransparency = true; w.Background = Brushes.Transparent;
        w.ResizeMode = ResizeMode.NoResize; w.SizeToContent = SizeToContent.WidthAndHeight;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Topmost = true; w.ShowInTaskbar = true;
        var sp = new DockPanel { Margin = new Thickness(22, 18, 22, 20), Width = width, LastChildFill = true };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(Text(title, 17, FontWeights.SemiBold, Fg));
        var close = new TextBlock { Text = "\uE711", FontFamily = Icons, FontSize = 12, Foreground = Sub, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Padding = new Thickness(6) };
        close.MouseLeftButtonUp += (_, _) => w.Close();
        head.Children.Add(close);
        DockPanel.SetDock(head, Dock.Top);
        sp.Children.Add(head);
        sp.Children.Add(body);
        var card = new Border
        {
            Margin = new Thickness(20), CornerRadius = new CornerRadius(18), Child = sp, Background = Surface,
            BorderBrush = Dark ? BA(0x30, 0xFF, 0xFF, 0xFF) : BA(0x18, 0, 0, 0), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.28, Color = Colors.Black },
        };
        w.Content = card;
        w.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not TextBox && e.OriginalSource is not ButtonBase) try { w.DragMove(); } catch { } };
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        // 打开时轻轻浮起（精简动效时直接出现）
        if (!IslandWindow.ReducedMotion)
        {
            var tt = new TranslateTransform(0, 10);
            card.RenderTransform = tt;
            card.Opacity = 0;
            w.Loaded += (_, _) =>
            {
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
                tt.BeginAnimation(TranslateTransform.YProperty, SpringEase.Animation(0, 0.36, 0.80, from: 10));
            };
        }
    }

    public static TextBlock Text(string s, double size, FontWeight weight, Brush fg) =>
        new() { Text = s, FontFamily = Theme.Text, FontSize = size, FontWeight = weight, Foreground = fg, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Label(string s) { var t = Text(s, 12, FontWeights.SemiBold, Sub); t.Margin = new Thickness(0, 10, 0, 4); return t; }

    public static TextBox Box(string text = "", double width = double.NaN) => new()
    {
        Text = text, Width = width, FontFamily = Theme.Text, FontSize = 13, Padding = new Thickness(6, 4, 6, 4), Height = 30,
        VerticalContentAlignment = VerticalAlignment.Center, Background = Faint, Foreground = Fg, BorderBrush = Line, CaretBrush = Fg,
    };

    /// <summary>圆角按钮；primary = 蓝底白字。</summary>
    public static Border Button(string text, Action click, bool primary = false, bool danger = false)
    {
        var t = Text(text, 12.5, FontWeights.SemiBold, primary ? Brushes.White : danger ? Danger : Fg);
        t.TextWrapping = TextWrapping.NoWrap;
        var b = new Border { Child = t, CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 6, 14, 6), Cursor = Cursors.Hand, Background = primary ? Accent : Faint, Margin = new Thickness(8, 0, 0, 0) };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (b.IsEnabled) click(); };
        b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.4;
        return b;
    }

    /// <summary>iOS 风格开关：滑块用阻尼弹簧过去（和岛同一套弹簧参数），精简动效时直接跳。</summary>
    public static Border Switch(bool on, Action<bool> changed)
    {
        var knob = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
        var tt = new TranslateTransform(on ? 16 : 0, 0);
        knob.RenderTransform = tt;
        var track = new Border { Width = 38, Height = 22, CornerRadius = new CornerRadius(11), Background = on ? Accent : Line, Child = knob, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Tag = on };
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var v = !(bool)track.Tag;
            track.Tag = v;
            track.Background = v ? Accent : Line;
            Slide(tt, v ? 16 : 0);
            changed(v);
        };
        return track;
    }

    private static void Slide(TranslateTransform tt, double to)
    {
        if (IslandWindow.ReducedMotion) { tt.BeginAnimation(TranslateTransform.XProperty, null); tt.X = to; return; }
        // 岛体同一套解析弹簧（response 0.32、ζ 0.72）：轻微过冲再停
        tt.BeginAnimation(TranslateTransform.XProperty, SpringEase.Animation(to, 0.32, 0.72));
    }

    /// <summary>分段选择（单选），返回容器；选中项变蓝。</summary>
    public static Border Segmented(string[] items, int selected, Action<int> changed)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new Border { Background = Faint, CornerRadius = new CornerRadius(9), Padding = new Thickness(2), Child = sp, HorizontalAlignment = HorizontalAlignment.Left };
        var cells = new List<Border>();
        void Paint(int sel)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                cells[i].Background = i == sel ? Accent : Brushes.Transparent;
                ((TextBlock)cells[i].Child).Foreground = i == sel ? Brushes.White : Fg;
            }
        }
        for (var i = 0; i < items.Length; i++)
        {
            var idx = i;
            var t = Text(items[i], 12.5, FontWeights.SemiBold, Fg); t.TextWrapping = TextWrapping.NoWrap;
            var c = new Border { Child = t, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 4, 12, 5), Cursor = Cursors.Hand };
            c.MouseLeftButtonUp += (_, e) => { e.Handled = true; Paint(idx); changed(idx); };
            cells.Add(c);
            sp.Children.Add(c);
        }
        Paint(selected);
        return host;
    }

    /// <summary>可多选的小圆片（星期选择用）。</summary>
    public static Border Chip(string text, bool on, Action<bool> changed)
    {
        var t = Text(text, 12.5, FontWeights.SemiBold, on ? Brushes.White : Fg);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Border { Width = 32, Height = 30, CornerRadius = new CornerRadius(15), Background = on ? Accent : Faint, Child = t, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0), Tag = on };
        t.VerticalAlignment = VerticalAlignment.Center;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var v = !(bool)b.Tag; b.Tag = v;
            b.Background = v ? Accent : Faint; t.Foreground = v ? Brushes.White : Fg;
            changed(v);
        };
        return b;
    }

    /// <summary>一行设置：左边标题 + 说明，右边控件。</summary>
    public static Grid Row(string title, string? desc, UIElement control)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        left.Children.Add(Text(title, 13.5, FontWeights.Medium, Fg));
        if (!string.IsNullOrWhiteSpace(desc)) { var d = Text(desc, 11.5, FontWeights.Normal, Sub); d.Margin = new Thickness(0, 2, 0, 0); left.Children.Add(d); }
        g.Children.Add(left);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static bool TryTime(string s, out TimeSpan t)
    {
        t = default;
        s = s.Trim().Replace('：', ':');
        if (!TimeSpan.TryParseExact(s, ["h\\:mm", "hh\\:mm"], System.Globalization.CultureInfo.InvariantCulture, out t)) return false;
        return t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);
    }

    public static DatePicker Date(DateTime d) => new() { SelectedDate = d, Width = 150, FontFamily = Theme.Text, FontSize = 13, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
}

/// <summary>
/// 把 Core 的解析弹簧（SpringMotion.Solve）包成 WPF 缓动：动画时长 = 弹簧停稳所需时间，
/// 曲线就是同一条弹簧轨迹，所以小窗、开关和岛体的手感一致；动画结束就停，不留渲染循环。
/// </summary>
public sealed class SpringEase : EasingFunctionBase
{
    public double Response { get; init; } = 0.35;
    public double Zeta { get; init; } = 0.75;
    public double Seconds { get; init; } = 0.5;

    protected override double EaseInCore(double t)
    {
        var (x, _) = Qisland.Core.SpringMotion.Solve(-1, 0, Response, Zeta, t * Seconds);
        return t >= 1 ? 1 : 1 + x;
    }

    protected override Freezable CreateInstanceCore() => new SpringEase { Response = Response, Zeta = Zeta, Seconds = Seconds };

    public static DoubleAnimation Animation(double to, double response, double zeta, double? from = null)
    {
        var secs = Math.Clamp(Qisland.Core.SpringMotion.SettleTime(response, zeta, 1, 0.002), 0.15, 1.2);
        var a = new DoubleAnimation(to, TimeSpan.FromSeconds(secs)) { EasingFunction = new SpringEase { Response = response, Zeta = zeta, Seconds = secs, EasingMode = EasingMode.EaseIn } };
        if (from != null) a.From = from;
        return a;
    }
}
