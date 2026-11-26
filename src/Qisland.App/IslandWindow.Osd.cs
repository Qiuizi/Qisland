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
    private enum OsdKind { None, Volume, Brightness, Lock }
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

    private string _lockName = "";
    private bool _lockOn;

    /// <summary>大写 / 数字锁定切换：岛变成一条小提示，1.2 秒后缩回。全屏（游戏里常把 Caps 绑成按键）时不冒出来。</summary>
    public void ShowLock(string name, bool on)
    {
        if (_hidden || _manualHide || _mode == Mode.Expanded) return;
        _lockName = name; _lockOn = on;
        _osd = OsdKind.Lock;
        _osdTimer ??= MakeOsdTimer();
        _osdTimer.Interval = TimeSpan.FromMilliseconds(1200);
        _osdTimer.Stop(); _osdTimer.Start();
        RebuildCompact();
        SetMode(Mode.Compact);
    }

    private void OnBrightness(int pct)
    {
        if (DateTime.Now < _brightQuietUntil) return;
        ShowOsd(OsdKind.Brightness, pct / 100.0, false);
    }

    private void ShowOsd(OsdKind kind, double level, bool muted)
    {
        if (_mode == Mode.Expanded) return;   // 展开时不打扰
        if (_hidden && !_manualHide) { _hidden = false; _show.Target = 1; Kick(); }   // 全屏中按音量键也给反馈，结束后下一秒自动再藏
        else if (_manualHide) return;
        var same = _osd == kind && _mode == Mode.Compact && _osdFill != null;
        _osd = kind; _osdLevel = level; _osdMuted = muted;
        _osdTimer ??= MakeOsdTimer();
        _osdTimer.Interval = TimeSpan.FromMilliseconds(1600);
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
        if (_osd == OsdKind.Lock) { BuildLockOsd(); return; }
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

    private void BuildLockOsd()
    {
        _osdFill = null;
        _compact.Height = CompactH;
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caps = _lockName.StartsWith("大写");
        var through = _lockName == "点击穿透";   // 穿透锁定：键帽里换成锁的图标（开 = 上锁，关 = 打开的锁）
        // 「开」时键帽用主色填满，上面的字按主色深浅取黑 / 白（浅色、液态玻璃的主色是深色，原来写死黑字会看不清）
        var onInk = Theme.Primary is SolidColorBrush pb && (0.299 * pb.Color.R + 0.587 * pb.Color.G + 0.114 * pb.Color.B) < 128
            ? Theme.Freeze(new SolidColorBrush(Colors.White)) : Theme.Freeze(new SolidColorBrush(Colors.Black));
        var key = new Border
        {
            Width = 24, Height = 22, CornerRadius = new CornerRadius(6), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = _lockOn ? Theme.Primary : Theme.Fill,
            Child = through
                ? new TextBlock { Text = _lockOn ? "\uE72E" : "\uE785", FontFamily = DialogKit.Icons, FontSize = 12, Foreground = _lockOn ? onInk : Theme.Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new TextBlock { Text = caps ? "A" : "1", FontFamily = Theme.Text, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = _lockOn ? onInk : Theme.Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        _compact.Children.Add(key);
        var t = Txt(13, FontWeights.SemiBold, Theme.Primary);
        t.Text = _lockName; t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(10, 0, 10, 1);
        Grid.SetColumn(t, 1); _compact.Children.Add(t);
        var st = Txt(12, FontWeights.SemiBold, _lockOn ? Theme.Primary : Theme.Tertiary);
        st.Text = through ? (_lockOn ? "已锁定" : "已解除") : _lockOn ? "开" : "关"; st.VerticalAlignment = VerticalAlignment.Center; st.Margin = new Thickness(0, 0, 18, 1);
        Grid.SetColumn(st, 2); _compact.Children.Add(st);
        _compactW = through ? 216 : 200;
        _compact.Width = _compactW;
        Place(_compact, _compactW);
        if (_mode == Mode.Compact) _w.Target = _compactW;
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
