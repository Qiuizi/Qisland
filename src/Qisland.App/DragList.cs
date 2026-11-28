using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Qisland.App;

/// <summary>
/// 可拖拽排序的竖向列表（设置页「布局与 DIY」用）：按住任意一行就能上下拖，不用找把手。
/// - 开关、左 / 中 / 右、上下箭头、按钮、输入框这些控件上按下不会触发拖动（光标是手形的控件、标了 NoDrag 的控件都算）；
/// - 鼠标移到行上：整行淡淡亮起、光标变成四向箭头、左边的 ≡ 变亮；
/// - 拖动时这一行半透明、略放大并带阴影，跟着指针走；别的行让位，空出来的位置画一个蓝色虚框和一条插入线；
/// - 松手后这一行滑进空位、同时恢复不透明和原大小，再交给调用方按新顺序重画。
/// 键盘 / 触控板用户用每行的上下箭头，效果一样。
/// </summary>
internal sealed class DragList : Grid
{
    /// <summary>标了这个的控件（及其子元素）上按下时不触发拖动。</summary>
    public static readonly DependencyProperty NoDragProperty = DependencyProperty.RegisterAttached("NoDrag", typeof(bool), typeof(DragList), new PropertyMetadata(false));
    public static void SetNoDrag(DependencyObject d, bool v) => d.SetValue(NoDragProperty, v);
    public static bool GetNoDrag(DependencyObject d) => (bool)d.GetValue(NoDragProperty);

    /// <summary>--snapshot 渲染时为 true：动画一步到位，截图里看到的就是最终状态。</summary>
    internal static bool Instant;
    private static bool Still => IslandWindow.ReducedMotion || Instant;

    private const double Threshold = 4;   // 按下后挪动超过这么多像素才算拖动，单击不会误触

    private readonly StackPanel _stack = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _slot, _bar;
    private readonly Ellipse _dot;
    private readonly List<FrameworkElement> _rows = [];
    private readonly Dictionary<FrameworkElement, (Border Hover, FrameworkElement Grip)> _parts = [];
    private int _from = -1, _to = -1;
    private int _pending = -1;
    private double _startY, _pendingY;
    private double[] _tops = [], _pitch = [];
    private FrameworkElement? _active;

    /// <summary>拖完一行：从第 from 位挪到第 to 位（from != to 时才触发）。</summary>
    public event Action<int, int>? Moved;

    public static readonly FontFamily Icons = DialogKit.Icons;

    public DragList()
    {
        var tint = new SolidColorBrush(Color.FromArgb(0x1C, 0x0A, 0x84, 0xFF));
        var edge = new SolidColorBrush(Color.FromArgb(0xB0, 0x0A, 0x84, 0xFF));
        _slot = new Border { CornerRadius = new CornerRadius(9), Background = tint, BorderBrush = edge, BorderThickness = new Thickness(1.2), Visibility = Visibility.Collapsed, Opacity = 0 };
        _bar = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = DialogKit.Accent, Visibility = Visibility.Collapsed, Opacity = 0 };
        _dot = new Ellipse { Width = 7, Height = 7, Fill = DialogKit.Accent, Visibility = Visibility.Collapsed, Opacity = 0 };
        _overlay.Children.Add(_slot); _overlay.Children.Add(_bar); _overlay.Children.Add(_dot);
        Children.Add(_overlay);   // 先加：插入标记在行的下面，拖动中的半透明行盖在它上面
        Children.Add(_stack);
    }

    /// <summary>一行左边的拖拽把手（六个点）。整行都能拖，把手只是提示：悬停时变亮。</summary>
    public static FrameworkElement Grip()
    {
        var t = new TextBlock { Text = "\uE76F", FontFamily = Icons, FontSize = 14, Foreground = DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        return new Border { Width = 22, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Child = t, ToolTip = "按住这一行任意位置拖动排序", Margin = new Thickness(0, 0, 6, 0) };
    }

    private static void Tint(FrameworkElement grip, Brush b) { if (grip is Border { Child: TextBlock t }) t.Foreground = b; }

    public void AddRow(FrameworkElement row, FrameworkElement grip)
    {
        var index = _rows.Count;
        _rows.Add(row);
        row.RenderTransform = new TranslateTransform();
        row.Cursor = Cursors.SizeAll;
        // 悬停底色：一层圆角的淡色，垫在整行下面，渐入渐出
        var hover = new Border { CornerRadius = new CornerRadius(9), Background = DialogKit.Faint, Opacity = 0, IsHitTestVisible = false, Margin = new Thickness(-4, 1, -4, 1) };
        if (row is Grid g) { SetColumnSpan(hover, 99); g.Children.Insert(0, hover); }
        _parts[row] = (hover, grip);
        _stack.Children.Add(row);

        row.MouseEnter += (_, _) => { if (_active == null || _active == row) { Fade(hover, 1, 110); Tint(grip, _active == row ? DialogKit.Accent : DialogKit.Fg); } };
        row.MouseLeave += (_, _) => { if (_active != row) { Fade(hover, 0, 150); Tint(grip, DialogKit.Sub); if (_pending == index) _pending = -1; } };
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_active != null || Interactive(e.OriginalSource as DependencyObject, row)) return;
            _pending = index; _pendingY = e.GetPosition(this).Y;
        };
        row.MouseMove += (_, e) =>
        {
            var y = e.GetPosition(this).Y;
            if (_active == row) { Track(y); return; }
            if (_pending == index && _active == null && e.LeftButton == MouseButtonState.Pressed && Math.Abs(y - _pendingY) > Threshold) { _pending = -1; Begin(index, row, _pendingY); Track(y); }
        };
        row.MouseLeftButtonUp += (_, _) => { _pending = -1; if (_active == row) End(); };
        row.LostMouseCapture += (_, _) => { if (_active == row) End(); };
    }

    /// <summary>截图用：第 index 行处于「鼠标悬停」的样子。</summary>
    internal void DemoHover(int index)
    {
        var (hover, grip) = _parts[_rows[index]];
        hover.Opacity = 1; Tint(grip, DialogKit.Fg);
    }

    /// <summary>截图用：第 index 行正被拖向第 to 位（半透明、带阴影、空位和插入线都画出来），不用真的按鼠标。</summary>
    internal void DemoDrag(int index, int to)
    {
        Begin(index, _rows[index], 0, capture: false);
        var target = 0.0;
        if (to > index) for (var j = index + 1; j <= to; j++) target += _pitch[j];
        else for (var j = to; j < index; j++) target -= _pitch[j];
        Track(target + (to > index ? 6 : -6));
    }

    /// <summary>按下的位置是不是一个会自己处理点击的控件（开关、按钮、分段、输入框……）。</summary>
    private static bool Interactive(DependencyObject? d, FrameworkElement row)
    {
        while (d != null && !ReferenceEquals(d, row))
        {
            if (GetNoDrag(d) || d is TextBoxBase or ButtonBase) return true;
            if (d is FrameworkElement fe && ReferenceEquals(fe.Cursor, Cursors.Hand)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static void Fade(UIElement e, double to, double ms)
    {
        if (Still) { e.BeginAnimation(OpacityProperty, null); e.Opacity = to; return; }
        e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private void Begin(int index, FrameworkElement row, double y, bool capture = true)
    {
        if (_active != null) return;
        _from = _to = index; _startY = y; _active = row;
        _tops = new double[_rows.Count]; _pitch = new double[_rows.Count];
        var acc = 0.0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            _pitch[i] = r.ActualHeight + r.Margin.Top + r.Margin.Bottom;
            _tops[i] = acc; acc += _pitch[i];
        }
        SetZIndex(row, 10);
        row.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        if (Still) { Scale(row).ScaleX = Scale(row).ScaleY = 1.015; row.Opacity = 0.78; }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Scale(row).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.015, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            Scale(row).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.015, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(0.78, TimeSpan.FromMilliseconds(120)));
        }
        var (hover, grip) = _parts[row];
        Fade(hover, 1, 80); Tint(grip, DialogKit.Accent);
        ShowMarker(animate: false);
        if (capture) row.CaptureMouse();
    }

    private static ScaleTransform Scale(FrameworkElement r) => (ScaleTransform)((TransformGroup)r.RenderTransform).Children[0];

    private TranslateTransform Shift(FrameworkElement r) => r.RenderTransform switch
    {
        TranslateTransform t => t,
        TransformGroup g => (TranslateTransform)g.Children[1],
        _ => (TranslateTransform)(r.RenderTransform = new TranslateTransform()),
    };

    private void Track(double y)
    {
        if (_from < 0) return;
        var dy = y - _startY;
        var min = -_tops[_from]; var max = _tops[^1] + _pitch[^1] - _tops[_from] - _pitch[_from];
        dy = Math.Clamp(dy, min, max);
        Shift(_rows[_from]).Y = dy;
        var center = _tops[_from] + _pitch[_from] / 2 + dy;
        var to = 0;
        for (var j = 0; j < _rows.Count; j++)
            if (j != _from && _tops[j] + _pitch[j] / 2 < center) to++;
        if (to == _to) return;
        _to = to;
        for (var j = 0; j < _rows.Count; j++)
        {
            if (j == _from) continue;
            var off = _from < _to && j > _from && j <= _to ? -_pitch[_from] : _to < _from && j >= _to && j < _from ? _pitch[_from] : 0;
            Animate(Shift(_rows[j]), off, 150);
        }
        ShowMarker(animate: true);
    }

    /// <summary>目标位置：空位（蓝色虚框）+ 空位上沿的一条插入线（左端带圆点）。</summary>
    private void ShowMarker(bool animate)
    {
        var top = 0.0; var seen = 0;
        for (var j = 0; j < _rows.Count; j++)
        {
            if (j == _from) continue;
            if (seen == _to) break;
            top += _pitch[j]; seen++;
        }
        var w = Math.Max(0, ActualWidth);
        var h = _pitch[_from];
        var barTop = Math.Max(0, top - 1.5);
        foreach (var e in new UIElement[] { _slot, _bar, _dot }) e.Visibility = Visibility.Visible;
        _slot.Width = Math.Max(0, w + 8); _slot.Height = Math.Max(0, h - 2); Canvas.SetLeft(_slot, -4);
        _bar.Width = Math.Max(0, w - 6); Canvas.SetLeft(_bar, 3);
        Canvas.SetLeft(_dot, -3);
        MoveTo(_slot, top + 1, animate); MoveTo(_bar, barTop, animate); MoveTo(_dot, barTop - 2, animate);
        Fade(_slot, 1, 100); Fade(_bar, 1, 100); Fade(_dot, 1, 100);
    }

    private static void MoveTo(UIElement e, double top, bool animate)
    {
        if (!animate || Still) { e.BeginAnimation(Canvas.TopProperty, null); Canvas.SetTop(e, top); return; }
        e.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(top, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    private static void Animate(TranslateTransform t, double to, double ms, Action? done = null)
    {
        if (Still) { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = to; done?.Invoke(); return; }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void End()
    {
        if (_active == null) return;
        var row = _active; _active = null;
        if (row.IsMouseCaptured) row.ReleaseMouseCapture();
        var from = _from; var to = _to;
        _from = _to = -1;
        if (from < 0) return;
        // 落到新位置：先滑到那一格，同时恢复不透明和原大小，再交给调用方按新顺序重画（重画前不清变换，避免闪回原位）
        var target = 0.0;
        if (to > from) for (var j = from + 1; j <= to; j++) target += _pitch[j];
        else for (var j = to; j < from; j++) target -= _pitch[j];
        var t = Shift(row);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        Fade(_slot, 0, 120); Fade(_bar, 0, 120); Fade(_dot, 0, 120);
        if (Still) { row.BeginAnimation(OpacityProperty, null); row.Opacity = 1; }
        else
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            row.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            Scale(row).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
            Scale(row).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        }
        var (hover, grip) = _parts[row];
        if (!row.IsMouseOver) { Fade(hover, 0, 160); Tint(grip, DialogKit.Sub); } else Tint(grip, DialogKit.Fg);
        Animate(t, target, 140, () =>
        {
            row.Effect = null;
            SetZIndex(row, 0);
            row.BeginAnimation(OpacityProperty, null); row.Opacity = 1;
            foreach (var e in new UIElement[] { _slot, _bar, _dot }) e.Visibility = Visibility.Collapsed;
            if (to != from) Moved?.Invoke(from, to);
            else row.RenderTransform = new TranslateTransform();
        });
    }
}