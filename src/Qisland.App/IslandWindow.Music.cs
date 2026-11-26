using System.Buffers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 4.3 音乐质感：封面取色的柔和光晕 / 进度条强调色、切歌过渡和点按反馈、逐字歌词高亮。
/// 取色每张封面只算一次（132 px 的小图），逐字高亮只改一支渐变画刷的两个色标，不重建界面。
/// </summary>
public sealed partial class IslandWindow
{
    // ───────────── 封面取色 ─────────────

    private BitmapSource? _tintArt;
    private bool _tintDark;
    private Color? _tint;
    private BitmapSource? _glowShownFor;

    /// <summary>岛底是不是深色（看主文字色：浅色字 = 深色底；白色 / 磨砂玻璃主题是深色字）。</summary>
    private static bool DarkIsland => Theme.Primary is SolidColorBrush b && ArtColor.Luminance(new Rgb(b.Color.R, b.Color.G, b.Color.B)) > 0.5;

    /// <summary>当前封面的柔和强调色；关了取色、没有封面、或封面是黑白的时候返回 null（保持主题原样）。</summary>
    private Color? ArtAccent(MediaState m)
    {
        if (!Features.On(Features.MusicTint) || m.Art == null || m.Video) return null;
        var dark = DarkIsland;
        if (ReferenceEquals(m.Art, _tintArt) && dark == _tintDark) return _tint;
        _tintArt = m.Art; _tintDark = dark; _tint = null;
        try
        {
            BitmapSource src = m.Art.Format == PixelFormats.Bgra32 || m.Art.Format == PixelFormats.Pbgra32 ? m.Art : new FormatConvertedBitmap(m.Art, PixelFormats.Bgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;
            var stride = w * 4;
            var buf = ArrayPool<byte>.Shared.Rent(stride * h);
            try
            {
                src.CopyPixels(buf, stride, 0);
                if (ArtColor.Dominant(buf.AsSpan(0, stride * h), w, h, stride) is { } c)
                {
                    var s = ArtColor.Soften(c, dark);
                    _tint = Color.FromRgb(s.R, s.G, s.B);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
        catch (Exception ex) { App.Log("封面取色: " + ex.GetType().Name); }
        return _tint;
    }

    /// <summary>卡片背后的柔光：从封面那一侧晕开，越往右越淡；只在换封面时淡入一次，平时重建不闪。</summary>
    private FrameworkElement? MusicGlow(MediaState m)
    {
        if (ArtAccent(m) is not { } c) return null;
        var a = DarkIsland ? 0.26 : 0.18;
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.10, 0.42), GradientOrigin = new Point(0.10, 0.42), RadiusX = 0.85, RadiusY = 1.25,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a), c.R, c.G, c.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * a * 0.45), c.R, c.G, c.B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        brush.Freeze();
        var glow = new Border { Background = brush, CornerRadius = new CornerRadius(18), Margin = new Thickness(-10, -4, -10, -2), IsHitTestVisible = false };
        if (!ReferenceEquals(_glowShownFor, m.Art) && !ReducedMotion)
            glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        _glowShownFor = m.Art;
        return glow;
    }

    /// <summary>进度条强调色：有封面色用封面色，否则主题主色。</summary>
    private Brush ProgressBrush(MediaState m) => ArtAccent(m) is { } c ? Theme.Freeze(new SolidColorBrush(c)) : Theme.Primary;

    // ───────────── 切歌：点按反馈 + 换歌过渡 ─────────────

    private string? _musicKey;
    private int _skipDir;
    private DateTime _skipAt;
    private FrameworkElement? _musicMid, _musicArt;

    private static string TrackKey(MediaState m) => m.Title + "\u0001" + m.Artist + "\u0001" + m.AppSource;

    /// <summary>点了上一首 / 下一首：先让标题和封面往反方向轻轻让开、变淡，新歌到了再从另一侧滑进来；2.5 秒没换成（播放器没理）就复原。</summary>
    private void Skip(int dir)
    {
        _skipDir = dir; _skipAt = DateTime.Now;
        if (dir > 0) Media?.Next(); else Media?.Previous();
        if (ReducedMotion) return;
        foreach (var el in new[] { _musicMid, _musicArt })
        {
            if (el == null) continue;
            var tt = el.RenderTransform as TranslateTransform ?? new TranslateTransform();
            el.RenderTransform = tt;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dir * 10, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        }
        var key = _musicKey;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_musicKey != key) return;   // 已经换歌，新卡片自己会滑进来
            foreach (var el in new[] { _musicMid, _musicArt })
            {
                if (el == null) continue;
                (el.RenderTransform as TranslateTransform)?.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            }
        };
        t.Start();
    }

    /// <summary>新建的音乐卡片：如果是换了一首歌，标题和封面从切歌方向滑进来（自动换歌就只淡入）。</summary>
    private void TrackIn(MediaState m, FrameworkElement art, FrameworkElement mid)
    {
        _musicArt = art; _musicMid = mid;
        var key = TrackKey(m);
        var changed = _musicKey != null && _musicKey != key;
        _musicKey = key;
        if (!changed || ReducedMotion) return;
        var dir = (DateTime.Now - _skipAt).TotalSeconds < 4 ? _skipDir : 0;
        _skipDir = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var (el, delay) in new[] { (art, 0), (mid, 40) })
        {
            var tt = new TranslateTransform(dir * 14, dir == 0 ? 3 : 0);
            el.RenderTransform = tt;
            el.Opacity = 0;
            var begin = TimeSpan.FromMilliseconds(delay);
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease, BeginTime = begin });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease, BeginTime = begin });
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
        }
    }

    // ───────────── 逐字歌词 ─────────────

    private sealed class Karaoke
    {
        public LyricLine? Line;
        public double[] Widths = [];
        public LinearGradientBrush Brush = null!;
        public GradientStop A = null!, B = null!;
        public double Origin;
    }

    /// <summary>当前句有逐字时间时，把已唱的部分画成主色、没唱的画成次色；分界线用短动画追，看起来是连续扫过去的。</summary>
    private void KaraokeTick(LyricLine? line, TimeSpan clock)
    {
        var on = Features.On(Features.LyricWords) && line?.Words is { Count: > 0 };
        foreach (var tb in new[] { _compactLyric, _cardLyric })
        {
            if (tb == null) continue;
            if (!on || tb.Text != line!.Text || tb.ActualWidth <= 0)
            {
                if (tb.Tag is Karaoke) { tb.Tag = null; tb.Foreground = Theme.Primary; }
                continue;
            }
            var k = tb.Tag as Karaoke;
            var fresh = k == null || !ReferenceEquals(k.Line, line);
            if (fresh)
            {
                k ??= new Karaoke();
                k.Line = line;
                k.Widths = PrefixWidths(tb, line.Words!);
                var sung = (Theme.Primary as SolidColorBrush)?.Color ?? Colors.White;
                var rest = (Theme.Secondary as SolidColorBrush)?.Color ?? Color.FromArgb(0x9E, sung.R, sung.G, sung.B);
                k.Brush = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute };
                k.A = new GradientStop(sung, 0); k.B = new GradientStop(rest, 0);
                k.Brush.GradientStops.Add(new GradientStop(sung, 0));
                k.Brush.GradientStops.Add(k.A);
                k.Brush.GradientStops.Add(k.B);
                k.Brush.GradientStops.Add(new GradientStop(rest, 1));
                tb.Tag = k;
                tb.Foreground = k.Brush;
            }
            var w = k!.Widths;
            // 居中的胶囊歌词：文字起点跟着控件实际宽度走（换句后下一拍才量好，所以每拍都算）
            var origin = tb.TextAlignment == TextAlignment.Center ? Math.Max(0, (tb.ActualWidth - w[^1]) / 2) : 0;
            if (fresh || Math.Abs(origin - k.Origin) > 0.5)
            {
                k.Origin = origin;
                k.Brush.StartPoint = new Point(origin, 0);
                k.Brush.EndPoint = new Point(origin + Math.Max(1, w[^1]), 0);
            }
            var (i, f) = Lrc.WordAt(line.Words!, clock);
            var x = i < 0 ? 0 : i >= line.Words!.Count ? w[^1] : w[i] + f * (w[i + 1] - w[i]);
            var tot = Math.Max(1, w[^1]);
            var p = Math.Clamp(x / tot, 0, 1);
            var edge = Math.Min(0.08, 5 / tot);   // 5 DIP 的柔边
            var pb = Math.Min(1, p + edge);
            if (fresh || ReducedMotion || PerfGuard.Degraded)
            {
                k.A.BeginAnimation(GradientStop.OffsetProperty, null); k.B.BeginAnimation(GradientStop.OffsetProperty, null);
                k.A.Offset = p; k.B.Offset = pb;
            }
            else
            {
                var d = TimeSpan.FromMilliseconds(_lyricTimer.Interval.TotalMilliseconds + 20);
                k.A.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(p, d));
                k.B.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(pb, d));
            }
        }
    }

    /// <summary>逐字的累计宽度（第 0 项 = 0，最后一项 = 整句宽），按 TextBlock 实际字体量。</summary>
    private static double[] PrefixWidths(TextBlock tb, IReadOnlyList<LyricWord> words)
    {
        var res = new double[words.Count + 1];
        var tf = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var dip = VisualTreeHelper.GetDpi(tb).PixelsPerDip;
        var acc = "";
        for (var i = 0; i < words.Count; i++)
        {
            acc += words[i].Text;
            var ft = new FormattedText(acc, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, tb.FontSize, Brushes.White, dip);
            res[i + 1] = ft.WidthIncludingTrailingWhitespace;
        }
        return res;
    }
}
