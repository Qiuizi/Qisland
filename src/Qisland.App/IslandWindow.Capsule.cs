using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 收起胶囊：4.3 的精简系统状态 / 额度小环，加上 5.0 的左中右槽位编排（空闲、播放分开配，存在 capsule-layout.txt）。
/// 精简状态和额度环仍然默认关、关着时不采样；编排只决定「放在哪、显不显示」。
/// </summary>
public sealed partial class IslandWindow
{
    // ───────────── 收起胶囊：精简系统状态 + 额度小环 ─────────────

    private const double IdleBaseW = 176;
    private double _idleExtraW;
    private double IdleW => IdleBaseW + _idleExtraW;
    private readonly SystemStats _capStats = new();
    private readonly StackPanel _idleStats = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _idleQuota = new() { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly RingArc _idleQuotaRing = new() { Thickness = 2.4 };
    private TextBlock? _capCpu, _capMem, _capNet;
    private CompactStats.Items _capItems = CompactStats.Items.None;
    private DateTime _capSampled = DateTime.MinValue;

    // 5.0 槽位：左 / 中 / 右三个横排容器；隐私小点和输入法指示固定在右槽最前面
    private readonly StackPanel _idleL = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
    private readonly StackPanel _idleC = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 0, 10, 0) };
    private readonly StackPanel _idleR = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 15, 0) };
    private bool _idleTrueCenter;
    private readonly StackPanel _idleWx = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<FrameworkElement> _idleFixed = [];
    // 播放胶囊里可选的时间 / 额度环（每次重建胶囊时新建，时间每秒更新）
    private TextBlock? _compactClock;
    private RingArc? _compactQuotaRing;

    public static string CapsuleStatsFile => System.IO.Path.Combine(App.DataDir, "capsule-stats.txt");
    public static CompactStats.Items CapsuleItems
    {
        get { try { return CompactStats.Parse(System.IO.File.Exists(CapsuleStatsFile) ? System.IO.File.ReadAllText(CapsuleStatsFile) : ""); } catch { return CompactStats.Parse(""); } }
        set { try { System.IO.File.WriteAllText(CapsuleStatsFile, CompactStats.Serialize(value)); } catch { } }
    }

    /// <summary>建胶囊时调一次：把原来固定在右边的那串（隐私点、输入法、天气）拆成可编排的几项。</summary>
    private void SetupIdleExtras(StackPanel wx)
    {
        // wx 里现在是：麦克风点、摄像头点、输入法、天气符号、气温。前三个是固定指示，后两个合成「天气」一项
        foreach (var el in wx.Children.OfType<FrameworkElement>().ToList())
        {
            wx.Children.Remove(el);
            if (el == _idleWeatherGlyph || el == _idleTemp) _idleWx.Children.Add(el);
            else _idleFixed.Add(el);
        }
        _idle.Children.Remove(wx);
        _idle.Children.Remove(_idleClock);
        _idleClock.Margin = new Thickness(0);
        Grid.SetColumn(_idleL, 0); Grid.SetColumn(_idleC, 1); Grid.SetColumn(_idleR, 2);
        _idle.Children.Add(_idleL); _idle.Children.Add(_idleC); _idle.Children.Add(_idleR);
        _idleQuota.Children.Add(_idleQuotaRing);
        _idleQuota.Visibility = Visibility.Collapsed;
        _idleStats.Visibility = Visibility.Collapsed;
        CapsuleStore.Changed += () => Dispatcher.BeginInvoke(OnCapsuleLayoutChanged);
        ArrangeIdle();
    }

    private void OnCapsuleLayoutChanged()
    {
        ArrangeIdle();
        UpdateIdleExtras();
        RebuildCompact();
        if (_mode == Mode.Compact) SetMode(Mode.Compact);
    }

    private FrameworkElement? IdleItem(string id) => id switch
    {
        "clock" => _idleClock,
        "weather" => _idleWx,
        "countdown" => _idleCountdown,
        "stats" => _idleStats,
        "quota" => _idleQuota,
        _ => null,
    };

    /// <summary>按编排把各项放进左 / 中 / 右槽。项之间留 8 DIP，额度小环和天气之间沿用原来的 7 DIP。</summary>
    private void ArrangeIdle()
    {
        foreach (var p in new[] { _idleL, _idleC, _idleR }) p.Children.Clear();
        foreach (var id in new[] { "clock", "weather", "countdown", "stats", "quota" })
            if (IdleItem(id) is { Parent: Panel old } el) old.Children.Remove(el);
        _idleCountdown.FontFamily = Theme.Text;
        var layout = CapsuleStore.Current;
        foreach (var f in _idleFixed) _idleR.Children.Add(f);
        foreach (var (slot, panel) in new[] { (CapsuleSlot.Left, _idleL), (CapsuleSlot.Center, _idleC), (CapsuleSlot.Right, _idleR) })
        {
            var ids = layout.Shown(CapsuleState.Idle, slot);
            for (var i = 0; i < ids.Count; i++)
            {
                if (IdleItem(ids[i]) is not { } el) continue;
                var last = i == ids.Count - 1;
                el.Margin = slot switch
                {
                    CapsuleSlot.Left => new Thickness(0, 0, last ? 0 : 8, ids[i] is "stats" or "countdown" ? 1 : 0),
                    CapsuleSlot.Right => new Thickness(0, 0, last ? 0 : ids[i] == "quota" ? 7 : 8, ids[i] is "stats" or "countdown" ? 1 : 0),
                    _ => new Thickness(i == 0 ? 0 : 8, 0, 0, ids[i] is "stats" or "countdown" ? 1 : 0),
                };
                el.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(el);
            }
        }
        // 自己编排过、中间放了东西：中间槽位按整条胶囊居中（左右两边等宽留白），不再只在剩下的空里居中。
        // 默认编排保持 4.3 的样子（倒计时在时钟和天气之间的空里居中）。
        _idleTrueCenter = !layout.IsDefault(CapsuleState.Idle) && layout.Shown(CapsuleState.Idle, CapsuleSlot.Center).Count > 0;
        Grid.SetColumn(_idleC, _idleTrueCenter ? 0 : 1);
        Grid.SetColumnSpan(_idleC, _idleTrueCenter ? 3 : 1);
        // 没放进任何槽位的项不显示：从可视树里拿掉就行（倒计时 / 状态 / 小环自己的 Visibility 照常由各自的逻辑管）
    }

    /// <summary>一格读数：小号灰色标签 + 等宽数字；宽度按最宽的可能值预留，数字变化时胶囊不跟着抖。</summary>
    private TextBlock CapCell(string label, string widest, bool first)
    {
        var l = Txt(9.5, FontWeights.SemiBold, Theme.Tertiary); l.Text = label; l.VerticalAlignment = VerticalAlignment.Center;
        l.Margin = new Thickness(first ? 0 : 8, 0, 3, 0);
        _idleStats.Children.Add(l);
        var v = Txt(11, FontWeights.SemiBold, Theme.Secondary); v.VerticalAlignment = VerticalAlignment.Center;
        v.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
        v.Text = widest; v.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        v.Width = Math.Ceiling(v.DesiredSize.Width); v.TextAlignment = TextAlignment.Right; v.Text = "";
        _idleStats.Children.Add(v);
        return v;
    }

    private void RebuildCapCells()
    {
        _idleStats.Children.Clear();
        _capCpu = _capMem = _capNet = null;
        var first = true;
        if (_capItems.HasFlag(CompactStats.Items.Cpu)) { _capCpu = CapCell("CPU", "100%", first); first = false; }
        if (_capItems.HasFlag(CompactStats.Items.Mem)) { _capMem = CapCell("内存", "100%", first); first = false; }
        if (_capItems.HasFlag(CompactStats.Items.Net)) { _capNet = CapCell("↓", "999K", first); }
    }

    /// <summary>开关 / 选项 / 额度 / 主题 / 倒计时 / 编排变化时调：决定画什么、胶囊要多宽。</summary>
    public void UpdateIdleExtras()
    {
        var layout = CapsuleStore.Current;
        var statsOn = Features.On(Features.CapsuleStats) && !Side && !Minimal && layout.IsOn(CapsuleState.Idle, "stats");
        var items = statsOn ? CapsuleItems : CompactStats.Items.None;
        if (items != _capItems) { _capItems = items; RebuildCapCells(); _capSampled = DateTime.MinValue; }
        // 课程 / 考试倒计时在同一个槽位显示时让给它（它更要紧、也只是一会儿）
        // 倒计时还没放进胶囊（启动早期）时它的 Visibility 也是默认的 Visible，要按「真的在胶囊里显示」判断
        var countdownShown = _idleCountdown.Parent != null && _idleCountdown.Visibility == Visibility.Visible;
        var sameSlot = countdownShown && _idleCountdown.Parent == _idleStats.Parent;
        var showStats = statsOn && !sameSlot && _idleStats.Children.Count > 0;
        _idleStats.Visibility = showStats ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in _idleStats.Children.OfType<TextBlock>()) c.Foreground = c.FontSize < 10 ? Theme.Tertiary : Theme.Secondary;
        if (showStats) SampleCapsule(force: true);

        var quotaOn = Features.On(Features.QuotaCapsule) && !Side && !Minimal;
        var tight = quotaOn ? QuotaWatch?.Tightest() : null;
        if (tight != null && layout.IsOn(CapsuleState.Idle, "quota"))
        {
            _idleQuotaRing.Gradient = LevelGradient(tight.Used);
            _idleQuotaRing.Track = Theme.Fill;
            _idleQuotaRing.Progress = Math.Max(0.04, Math.Clamp(tight.Used / 100, 0, 1));
            _idleQuota.Visibility = Visibility.Visible;
        }
        else _idleQuota.Visibility = Visibility.Collapsed;
        if (_compactQuotaRing != null && tight != null)
        {
            _compactQuotaRing.Gradient = LevelGradient(tight.Used);
            _compactQuotaRing.Track = Theme.Fill;
            _compactQuotaRing.Progress = Math.Max(0.04, Math.Clamp(tight.Used / 100, 0, 1));
        }

        // 宽度：三个槽位量一次就固定（数字格本身按最宽值预留），不随读数变
        var m = new Size(double.PositiveInfinity, double.PositiveInfinity);
        _idleL.Measure(m); _idleC.Measure(m); _idleR.Measure(m);
        var center = _idleC.Children.OfType<UIElement>().Any(c => c.Visibility == Visibility.Visible) ? _idleC.DesiredSize.Width : 0;
        var need = _idleTrueCenter && center > 0
            ? 2 * Math.Max(_idleL.DesiredSize.Width, _idleR.DesiredSize.Width) + center + 12
            : _idleL.DesiredSize.Width + center + _idleR.DesiredSize.Width + (center > 0 ? 20 : 6);
        var extra = Math.Max(0, Math.Ceiling(need - IdleBaseW));
        if (_idleQuota.Visibility == Visibility.Visible && _idleQuota.Parent != null) extra = Math.Max(extra, 12);
        if (Math.Abs(extra - _idleExtraW) > 0.5)
        {
            _idleExtraW = extra;
            _idle.Width = IdleW;
            Place(_idle, IdleW);
            if (_mode == Mode.Idle) SetMode(Mode.Idle);
        }
    }

    /// <summary>每秒被 OnSecond 调：胶囊收起、开着读数时每 2 秒采样一次；其余时候什么都不做。</summary>
    private void TickCapsuleStats()
    {
        if (_compactClock != null && _compactClock.Text != _clockText) _compactClock.Text = _clockText;
        if (_capItems == CompactStats.Items.None || _idleStats.Visibility != Visibility.Visible) return;
        if (_mode != Mode.Idle || _hidden) return;
        SampleCapsule(force: false);
    }

    private void SampleCapsule(bool force)
    {
        if (!force && DateTime.Now - _capSampled < TimeSpan.FromSeconds(2)) return;
        _capSampled = DateTime.Now;
        _capStats.Sample();
        if (_capCpu != null) _capCpu.Text = CompactStats.Pct(_capStats.Cpu);
        if (_capMem != null) _capMem.Text = CompactStats.Pct(_capStats.MemFraction);
        if (_capNet != null) _capNet.Text = CompactStats.Speed(_capStats.DownBps);
    }

    // ───────────── 播放胶囊：按编排排封面 / 歌名 / 频谱 / 时间 / 天气 / 额度环 ─────────────

    /// <summary>播放胶囊里除了歌名之外的一项（每次重建都新建，避免和空闲胶囊抢同一个控件）。</summary>
    private FrameworkElement? PlayingItem(string id, MediaState m, out double width)
    {
        width = 0;
        switch (id)
        {
            case "cover":
            {
                var tile = ArtTile(m, 24, 6);
                width = m.Art != null && m.Video ? 43 : 24;
                return tile;
            }
            case "bars":
                width = 14;
                return Bars(14);
            case "clock":
            {
                var t = Txt(13, FontWeights.SemiBold, Theme.Primary); t.Text = _clockText;
                t.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
                _compactClock = t;
                width = MeasureText(string.IsNullOrEmpty(_clockText) ? "00:00" : _clockText, 13, FontWeights.SemiBold) + 1;
                return t;
            }
            case "weather":
            {
                if (_weather is not { } w) return null;
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                var g = WxGlyph(Theme.WeatherGlyph(w.Code, Theme.IsNight(DateTime.Now)), 12.5, Theme.Secondary); g.Margin = new Thickness(0, 1, 5, 0); g.VerticalAlignment = VerticalAlignment.Center;
                var tt = Txt(12.5, FontWeights.Medium, Theme.Secondary); tt.Text = $"{w.Temperature:0}°"; tt.VerticalAlignment = VerticalAlignment.Center;
                sp.Children.Add(g); sp.Children.Add(tt);
                sp.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width = Math.Ceiling(sp.DesiredSize.Width);
                return sp;
            }
            case "quota":
            {
                var tight = Features.On(Features.QuotaCapsule) ? QuotaWatch?.Tightest() : null;
                if (tight == null) return null;
                var host = new Grid { Width = 13, Height = 13 };
                var ring = new RingArc { Thickness = 2.4, Gradient = LevelGradient(tight.Used), Track = Theme.Fill, Progress = Math.Max(0.04, Math.Clamp(tight.Used / 100, 0, 1)) };
                host.Children.Add(ring);
                _compactQuotaRing = ring;
                width = 13;
                return host;
            }
        }
        return null;
    }

    /// <summary>左 / 右槽：一排项，项之间 8 DIP。返回整排宽度。</summary>
    private double PlayingSide(CapsuleSlot slot, MediaState m, int column, Thickness margin)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
        var total = 0.0;
        foreach (var id in CapsuleStore.Current.Shown(CapsuleState.Playing, slot))
        {
            if (id == "title" || PlayingItem(id, m, out var w) is not { } el) continue;
            el.VerticalAlignment = VerticalAlignment.Center;
            if (sp.Children.Count > 0) { el.Margin = new Thickness(8, 0, 0, 0); total += 8; }
            sp.Children.Add(el);
            total += w;
        }
        Grid.SetColumn(sp, column);
        _compact.Children.Add(sp);
        return total;
    }
}
