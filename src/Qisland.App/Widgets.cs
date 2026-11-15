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
    /// <summary>设了就用沿圆环从上到下的渐变代替纯色。</summary>
    public (Color a, Color b)? Gradient { get; set; }
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
        Brush stroke = Stroke;
        if (Gradient is { } gr)
            stroke = new LinearGradientBrush(gr.a, gr.b, new Point(ActualWidth, 0), new Point(0, ActualHeight)) { MappingMode = BrushMappingMode.Absolute };
        var pen = new Pen(stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
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

/// <summary>
/// 彩色天气图标（WPF 不支持彩色 emoji，所以自己画）：太阳是暖色渐变带光芒，云是带体积感的白色渐变，
/// 雨滴是蓝色，雷是黄色闪电。按 WMO 天气代码组合。
/// </summary>
public sealed class WeatherIcon : FrameworkElement
{
    public int Code { get; set; }
    public bool Night { get; set; }

    private static Brush F(Brush b) { b.Freeze(); return b; }
    private static readonly Brush SunCore = F(new RadialGradientBrush(Color.FromRgb(0xFF, 0xEB, 0x8A), Color.FromRgb(0xFF, 0xA8, 0x1F)) { GradientOrigin = new Point(0.35, 0.3) });
    private static readonly Brush SunRay = F(new SolidColorBrush(Color.FromRgb(0xFF, 0xC2, 0x3D)));
    private static readonly Brush MoonFill = F(new LinearGradientBrush(Color.FromRgb(0xFF, 0xF6, 0xD2), Color.FromRgb(0xE6, 0xCF, 0x7C), 60));
    private static readonly Brush CloudLight = F(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD3, 0xDB, 0xE6), 90));
    private static readonly Brush CloudBack = F(new LinearGradientBrush(Color.FromRgb(0xC9, 0xD2, 0xDE), Color.FromRgb(0x9A, 0xA6, 0xB6), 90));
    private static readonly Brush CloudDark = F(new LinearGradientBrush(Color.FromRgb(0xC2, 0xCB, 0xD6), Color.FromRgb(0x84, 0x90, 0xA2), 90));
    private static readonly Brush Drop = F(new SolidColorBrush(Color.FromRgb(0x4F, 0xB8, 0xFF)));
    private static readonly Brush Bolt = F(new LinearGradientBrush(Color.FromRgb(0xFF, 0xE6, 0x5C), Color.FromRgb(0xFF, 0xAA, 0x00), 90));
    private static readonly Brush Fog = F(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)));

    protected override void OnRender(DrawingContext dc)
    {
        var s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var ox = (ActualWidth - s) / 2; var oy = (ActualHeight - s) / 2;
        dc.PushTransform(new TranslateTransform(ox, oy));
        var c = Code;
        if (c is 0) Celestial(dc, s * 0.5, s * 0.5, s * 0.25, rays: true);
        else if (c is 1 or 2) { Celestial(dc, s * 0.36, s * 0.36, s * 0.19, rays: c == 1 || !Night); Cloud(dc, s * 0.22, s * 0.40, s * 0.74, CloudLight); }
        else if (c is 3) { Cloud(dc, s * 0.30, s * 0.18, s * 0.62, CloudBack); Cloud(dc, s * 0.08, s * 0.36, s * 0.78, CloudLight); }
        else if (c is 45 or 48)
        {
            Cloud(dc, s * 0.12, s * 0.14, s * 0.76, CloudLight);
            for (var i = 0; i < 3; i++) Line(dc, Fog, s * (0.16 + i * 0.06), s * (0.70 + i * 0.1), s * (0.84 - i * 0.06), s * (0.70 + i * 0.1), s * 0.055);
        }
        else if (c is >= 71 and <= 77 or 85 or 86)
        {
            Cloud(dc, s * 0.12, s * 0.10, s * 0.76, CloudLight);
            foreach (var (x, y) in new[] { (0.32, 0.74), (0.52, 0.84), (0.70, 0.72), (0.42, 0.95) }) dc.DrawEllipse(Brushes.White, null, new Point(s * x, s * y), s * 0.045, s * 0.045);
        }
        else if (c >= 95)
        {
            Cloud(dc, s * 0.12, s * 0.08, s * 0.76, CloudDark);
            var g = new StreamGeometry();
            using (var x = g.Open())
            {
                x.BeginFigure(new Point(s * 0.54, s * 0.52), true, true);
                x.PolyLineTo([new(s * 0.38, s * 0.76), new(s * 0.50, s * 0.76), new(s * 0.42, s * 0.97), new(s * 0.66, s * 0.68), new(s * 0.53, s * 0.68), new(s * 0.62, s * 0.52)], true, true);
            }
            g.Freeze();
            dc.DrawGeometry(Bolt, null, g);
        }
        else
        {
            var heavy = c is 63 or 65 or 81 or 82 or 67;
            Cloud(dc, s * 0.12, s * 0.08, s * 0.76, CloudDark);
            var n = heavy ? 4 : 3;
            for (var i = 0; i < n; i++)
            {
                var x = s * (0.30 + i * (0.42 / (n - 1)));
                Line(dc, Drop, x, s * 0.66, x - s * 0.07, s * 0.86, s * 0.06);
            }
        }
        dc.Pop();
    }

    private static void Line(DrawingContext dc, Brush b, double x0, double y0, double x1, double y1, double t) =>
        dc.DrawLine(new Pen(b, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(x0, y0), new Point(x1, y1));

    private void Celestial(DrawingContext dc, double cx, double cy, double r, bool rays)
    {
        if (Night)
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(cx, cy), r, r), new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.85, r * 0.85));
            moon.Freeze();
            dc.DrawGeometry(MoonFill, null, moon);
            return;
        }
        if (rays)
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                Line(dc, SunRay, cx + Math.Cos(a) * r * 1.38, cy + Math.Sin(a) * r * 1.38, cx + Math.Cos(a) * r * 1.75, cy + Math.Sin(a) * r * 1.75, r * 0.26);
            }
        dc.DrawEllipse(SunCore, null, new Point(cx, cy), r, r);
    }

    private static void Cloud(DrawingContext dc, double x, double y, double w, Brush fill)
    {
        var h = w * 0.62;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(x + 0.30 * w, y + 0.64 * h), 0.21 * w, 0.21 * w));
        g.Children.Add(new EllipseGeometry(new Point(x + 0.56 * w, y + 0.46 * h), 0.28 * w, 0.28 * w));
        g.Children.Add(new EllipseGeometry(new Point(x + 0.78 * w, y + 0.70 * h), 0.18 * w, 0.18 * w));
        g.Children.Add(new RectangleGeometry(new Rect(x + 0.12 * w, y + 0.62 * h, 0.80 * w, 0.38 * h), 0.19 * h, 0.19 * h));
        g.Freeze();
        dc.DrawGeometry(fill, null, g);
    }
}

public static class Sky
{
    /// <summary>天气卡片的天空：按天气和昼夜（以及日出日落前后一小时的霞光）给出背景渐变。</summary>
    public static Brush For(int code, bool night, bool golden)
    {
        (string a, string b) = (code, night, golden) switch
        {
            (>= 95, _, _) => ("#2B3240", "#4B5568"),
            (>= 51 and < 95 and not (>= 71 and <= 77) and not (85 or 86), true, _) => ("#151C29", "#2D3748"),
            (>= 51 and < 95 and not (>= 71 and <= 77) and not (85 or 86), false, _) => ("#3D5066", "#6A7F96"),
            (>= 71 and <= 77 or 85 or 86, _, _) => night ? ("#1F2A3D", "#46536B") : ("#7C8FA8", "#A9B9CC"),
            (45 or 48, _, _) => night ? ("#232833", "#434A58") : ("#77808C", "#A3ABB5"),
            (_, _, true) when code <= 2 => ("#3A4A8F", "#D98366"),
            (3, true, _) => ("#1A2030", "#353D50"),
            (3, false, _) => ("#56708C", "#879DB4"),
            (_, true, _) => ("#0A1633", "#24376A"),
            _ => ("#1E6FD0", "#5FAEF2"),
        };
        var g = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(a), (Color)ColorConverter.ConvertFromString(b), new Point(0.2, 0), new Point(0.8, 1));
        g.Freeze();
        return g;
    }
}
