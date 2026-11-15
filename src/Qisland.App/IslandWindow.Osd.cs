using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>音量 / 亮度浮窗：岛本身变成一条细滑块，1.6 秒后缩回。</summary>
public sealed partial class IslandWindow
{
    private enum OsdKind { None, Volume, Brightness }
    private OsdKind _osd = OsdKind.None;
    private double _osdLevel;
    private bool _osdMuted;
    private DispatcherTimer? _osdTimer;
    private Border? _osdFill;
    private TextBlock? _osdGlyph, _osdPct;
    private const double OsdW = 300, OsdTrack = 196;
    private DateTime _brightQuietUntil = DateTime.Now.AddSeconds(3);

    private bool OsdActive => _osd != OsdKind.None;

    public void ShowVolume(float level, bool muted) => ShowOsd(OsdKind.Volume, level, muted);

    private void OnBrightness(int pct)
    {
        if (DateTime.Now < _brightQuietUntil) return;
        ShowOsd(OsdKind.Brightness, pct / 100.0, false);
    }

    private void ShowOsd(OsdKind kind, double level, bool muted)
    {
        if (_mode == Mode.Expanded || _hidden) return;   // 展开时不打扰
        var same = _osd == kind && _mode == Mode.Compact && _osdFill != null;
        _osd = kind; _osdLevel = level; _osdMuted = muted;
        _osdTimer ??= MakeOsdTimer();
        _osdTimer.Stop(); _osdTimer.Start();
        if (same) { UpdateOsd(true); return; }
        RebuildCompact();
        SetMode(Mode.Compact);
    }

    private DispatcherTimer MakeOsdTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _osd = OsdKind.None; _osdFill = null;
            RebuildCompact();
            if (_mode == Mode.Compact) SetMode(DesiredRestMode());
        };
        return t;
    }

    private void BuildOsd()
    {
        _compact.Height = CompactH;
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _osdGlyph = Glyph("", 15, Theme.Primary);
        _osdGlyph.Width = 22; _osdGlyph.TextAlignment = TextAlignment.Center;
        _osdGlyph.Margin = new Thickness(14, 0, 0, 0);
        _compact.Children.Add(_osdGlyph);

        var track = new Border { Height = 5, Width = OsdTrack, CornerRadius = new CornerRadius(2.5), Background = Theme.Fill, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ClipToBounds = true };
        _osdFill = new Border { Height = 5, CornerRadius = new CornerRadius(2.5), Background = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Child = _osdFill;
        Grid.SetColumn(track, 1);
        _compact.Children.Add(track);

        _osdPct = Txt(12, FontWeights.SemiBold, Theme.Secondary);
        _osdPct.Width = 30; _osdPct.TextAlignment = TextAlignment.Right; _osdPct.VerticalAlignment = VerticalAlignment.Center;
        _osdPct.Margin = new Thickness(0, 0, 16, 1);
        _osdPct.FontFeatures();
        Grid.SetColumn(_osdPct, 2);
        _compact.Children.Add(_osdPct);

        _compactW = OsdW;
        _compact.Width = _compactW;
        Place(_compact, _compactW);
        if (_mode == Mode.Compact) _w.Target = _compactW;
        UpdateOsd(false);
    }

    private void UpdateOsd(bool animate)
    {
        if (_osdFill == null || _osdGlyph == null || _osdPct == null) return;
        var lv = Math.Clamp(_osdLevel, 0, 1);
        var pct = (int)Math.Round(lv * 100);
        _osdGlyph.Text = _osd == OsdKind.Brightness ? "\uE706"
            : _osdMuted || pct == 0 ? "\uE74F" : pct < 34 ? "\uE993" : pct < 67 ? "\uE994" : "\uE995";
        _osdPct.Text = _osdMuted ? "静音" : pct.ToString();
        var target = _osdMuted ? 0 : OsdTrack * lv;
        _osdFill.Opacity = _osdMuted ? 0.4 : 1;
        if (!animate) { _osdFill.BeginAnimation(WidthProperty, null); _osdFill.Width = target; return; }
        var a = new DoubleAnimation(target, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Timeline.SetDesiredFrameRate(a, App.Hz);
        _osdFill.BeginAnimation(WidthProperty, a);
    }
}

internal static class OsdExt
{
    /// <summary>等宽数字，百分比跳动时不左右晃。</summary>
    public static void FontFeatures(this TextBlock t) => Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
}
