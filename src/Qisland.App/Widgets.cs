using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Qisland.App;

/// <summary>
/// 圆环进度。值变化时用动画过渡，而不是整块重建——组件页"丝滑"的关键就在这里：
/// 每秒刷新只改一个依赖属性，WPF 只重画这一小块。
/// </summary>
public sealed class RingArc : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(RingArc), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public Brush Stroke { get; set; } = Brushes.White;
    public Brush? Track { get; set; }
    public double Thickness { get; set; } = 4;
    private double _target = -1;

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public void AnimateTo(double p, double ms = 520, double? from = null)
    {
        p = Math.Clamp(p, 0, 1);
        if (from == null && Math.Abs(p - _target) < 0.002) return;
        _target = p;
        var a = new DoubleAnimation(p, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease };
        if (from != null) a.From = from;
        BeginAnimation(ProgressProperty, a, HandoffBehavior.SnapshotAndReplace);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var t = Thickness;
        var r = (Math.Min(ActualWidth, ActualHeight) - t) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        if (Track != null) dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
        var p = Math.Clamp(Progress, 0, 1);
        if (p < 0.004) return;
        var pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (p > 0.998) { dc.DrawEllipse(null, pen, c, r, r); return; }
        var a = p * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>
/// 太阳轨迹：一道虚线弧从日出走到日落，走过的部分是暖色实线，太阳是一颗带光晕的点。
/// 夜里太阳落到地平线下，弧线变暗，只在日出那一端留一个小点。
/// </summary>
public sealed class SunArc : FrameworkElement
{
    public TimeSpan Rise { get; set; } = new(6, 30, 0);
    public TimeSpan Set { get; set; } = new(18, 30, 0);
    public Brush Dim { get; set; } = Brushes.Gray;

    private static readonly Color Sun = Color.FromRgb(0xFF, 0xB3, 0x40);
    private static readonly Brush Glow = Theme.Freeze(new RadialGradientBrush(Color.FromArgb(0x70, 0xFF, 0xB3, 0x40), Color.FromArgb(0, 0xFF, 0xB3, 0x40)));
    private static readonly Brush Core = Theme.Freeze(new RadialGradientBrush(Color.FromRgb(0xFF, 0xF0, 0xC0), Sun) { GradientOrigin = new Point(0.35, 0.35) });

    public double Fraction
    {
        get
        {
            var now = DateTime.Now.TimeOfDay;
            var len = (Set - Rise).TotalMinutes;
            return len <= 0 ? -1 : (now - Rise).TotalMinutes / len;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 10) return;
        var cx = w / 2; var by = h - 5; var rx = w / 2 - 8; var ry = h - 14;
        Point At(double f) { var th = Math.PI * (1 - f); return new Point(cx + rx * Math.Cos(th), by - ry * Math.Sin(th)); }
        StreamGeometry Arc(double f0, double f1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(At(f0), false, false);
                const int n = 40;
                var pts = new List<Point>(n);
                for (var i = 1; i <= n; i++) pts.Add(At(f0 + (f1 - f0) * i / n));
                ctx.PolyLineTo(pts, true, true);
            }
            g.Freeze();
            return g;
        }
        var dash = new Pen(Dim, 1.4) { DashStyle = new DashStyle([0.1, 3.2], 0), DashCap = PenLineCap.Round };
        dc.DrawGeometry(null, dash, Arc(0, 1));

        var f = Fraction;
        if (f is > 0 and < 1)
        {
            var warm = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0x9F, 0x0A), Sun, 0);
            dc.DrawGeometry(null, new Pen(warm, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(0, f));
            var p = At(f);
            dc.DrawEllipse(Glow, null, p, 11, 11);
            dc.DrawEllipse(Core, null, p, 4.6, 4.6);
        }
        else
        {
            dc.DrawEllipse(Dim, null, At(f >= 1 ? 1 : 0), 2.6, 2.6);
        }
    }
}

/// <summary>最近一分钟的网速折线，淡淡地铺在网络卡片底下。</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Queue<double> _v = new();
    public int Capacity { get; set; } = 40;
    public Color Color { get; set; } = Colors.DeepSkyBlue;

    public void Push(double x)
    {
        _v.Enqueue(Math.Max(0, x));
        while (_v.Count > Capacity) _v.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_v.Count < 2) return;
        double w = ActualWidth, h = ActualHeight;
        var max = Math.Max(_v.Max(), 64 * 1024);
        var step = w / (Capacity - 1);
        var x0 = w - (_v.Count - 1) * step;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0, h), true, true);
            var i = 0;
            foreach (var v in _v) ctx.LineTo(new Point(x0 + i++ * step, h - 2 - (h - 4) * Math.Sqrt(v / max)), true, true);
            ctx.LineTo(new Point(w, h), false, false);
        }
        g.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(0x50, Color.R, Color.G, Color.B), Color.FromArgb(0, Color.R, Color.G, Color.B), 90);
        dc.DrawGeometry(fill, null, g);
    }
}

/// <summary>温度区间条（像 iOS 天气那样）：灰色轨道上，一段冷到暖的渐变表示当天最低到最高。</summary>
public sealed class TempBar : FrameworkElement
{
    public double Min { get; set; } public double Max { get; set; }
    public double Low { get; set; } public double High { get; set; }
    public Brush Track { get; set; } = Brushes.Gray;

    private static Color TempColor(double t) => t switch
    {
        < 5 => Color.FromRgb(0x64, 0xD2, 0xFF),
        < 15 => Color.FromRgb(0x30, 0xD1, 0x58),
        < 25 => Color.FromRgb(0xFF, 0xD6, 0x0A),
        < 32 => Color.FromRgb(0xFF, 0x9F, 0x0A),
        _ => Color.FromRgb(0xFF, 0x45, 0x3A),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        var span = Math.Max(1, Max - Min);
        var x0 = w * (Low - Min) / span; var x1 = w * (High - Min) / span;
        if (x1 - x0 < h) x1 = x0 + h;
        dc.DrawRoundedRectangle(new LinearGradientBrush(TempColor(Low), TempColor(High), 0), null, new Rect(x0, 0, Math.Min(w, x1) - x0, h), r, r);
    }
}

public static class HhMm
{
    public static TimeSpan Parse(string hhmm, TimeSpan fallback) =>
        TimeSpan.TryParseExact(hhmm, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : fallback;
}
