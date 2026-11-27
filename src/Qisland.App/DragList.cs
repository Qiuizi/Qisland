using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Qisland.App;

/// <summary>
/// 可拖拽排序的竖向列表（设置页「布局与 DIY」用）：按住每行左边的把手上下拖，别的行用短动画让位，松手落到新位置。
/// 拖动中的那一行浮起一点（淡阴影 + 略放大），落位后再交给调用方重画。键盘 / 触控板用户用每行的上下箭头，效果一样。
/// </summary>
internal sealed class DragList : StackPanel
{
    private readonly List<FrameworkElement> _rows = [];
    private int _from = -1, _to = -1;
    private double _startY;
    private double[] _tops = [], _pitch = [];
    private FrameworkElement? _grip;

    /// <summary>拖完一行：从第 from 位挪到第 to 位（from != to 时才触发）。</summary>
    public event Action<int, int>? Moved;

    public static readonly FontFamily Icons = DialogKit.Icons;

    /// <summary>一行左边的拖拽把手（六个点）。</summary>
    public static FrameworkElement Grip()
    {
        var t = new TextBlock { Text = "\uE76F", FontFamily = Icons, FontSize = 14, Foreground = DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var b = new Border { Width = 22, Height = 30, Background = Brushes.Transparent, Cursor = Cursors.SizeNS, Child = t, ToolTip = "按住上下拖动排序", Margin = new Thickness(0, 0, 6, 0) };
        b.MouseEnter += (_, _) => t.Foreground = DialogKit.Fg;
        b.MouseLeave += (_, _) => t.Foreground = DialogKit.Sub;
        return b;
    }

    public void AddRow(FrameworkElement row, FrameworkElement grip)
    {
        var index = _rows.Count;
        _rows.Add(row);
        row.RenderTransform = new TranslateTransform();
        Children.Add(row);
        grip.MouseLeftButtonDown += (_, e) => { e.Handled = true; Begin(index, grip, e.GetPosition(this).Y); };
        grip.MouseMove += (_, e) => { if (_grip == grip) Track(e.GetPosition(this).Y); };
        grip.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (_grip == grip) End(); };
        grip.LostMouseCapture += (_, _) => { if (_grip == grip) End(); };
    }

    private void Begin(int index, FrameworkElement grip, double y)
    {
        if (_grip != null) return;
        _from = _to = index; _startY = y; _grip = grip;
        _tops = new double[_rows.Count]; _pitch = new double[_rows.Count];
        var acc = 0.0;
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            _pitch[i] = r.ActualHeight + r.Margin.Top + r.Margin.Bottom;
            _tops[i] = acc; acc += _pitch[i];
        }
        var row = _rows[index];
        SetZIndex(row, 10);
        row.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
        if (row is Panel p && p.Background is null or SolidColorBrush { Color.A: 0 }) p.Background = DialogKit.Faint;
        if (row is Border b0 && b0.Background is null or SolidColorBrush { Color.A: 0 }) b0.Background = DialogKit.Faint;
        row.RenderTransformOrigin = new Point(0.5, 0.5);
        row.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1.015, 1.015), new TranslateTransform() } };
        grip.CaptureMouse();
    }

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
    }

    private static void Animate(TranslateTransform t, double to, double ms, Action? done = null)
    {
        if (IslandWindow.ReducedMotion) { t.BeginAnimation(TranslateTransform.YProperty, null); t.Y = to; done?.Invoke(); return; }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (done != null) a.Completed += (_, _) => done();
        t.BeginAnimation(TranslateTransform.YProperty, a);
    }

    private void End()
    {
        if (_grip == null) return;
        var grip = _grip; _grip = null;
        if (grip.IsMouseCaptured) grip.ReleaseMouseCapture();
        var from = _from; var to = _to;
        _from = _to = -1;
        if (from < 0) return;
        var row = _rows[from];
        // 落到新位置：先滑到那一格，再交给调用方按新顺序重画（重画前不清变换，避免闪回原位）
        var target = 0.0;
        if (to > from) for (var j = from + 1; j <= to; j++) target += _pitch[j];
        else for (var j = to; j < from; j++) target -= _pitch[j];
        var t = Shift(row);
        t.BeginAnimation(TranslateTransform.YProperty, null);
        Animate(t, target, 140, () =>
        {
            row.Effect = null;
            SetZIndex(row, 0);
            if (to != from) Moved?.Invoke(from, to);
            else row.RenderTransform = new TranslateTransform();
        });
    }
}
