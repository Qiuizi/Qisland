using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 4.3：收起胶囊上可选的精简系统状态（CPU / 内存 / 网速）和最紧额度的小环。都默认关，关着时不采样。
/// </summary>
public sealed partial class IslandWindow
{
    // ───────────── 收起胶囊：精简系统状态 + 额度小环 ─────────────

    private const double IdleBaseW = 176;
    private double _idleExtraW;
    private double IdleW => IdleBaseW + _idleExtraW;
    private readonly SystemStats _capStats = new();
    private readonly StackPanel _idleStats = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 1) };
    private readonly Grid _idleQuota = new() { Width = 13, Height = 13, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly RingArc _idleQuotaRing = new() { Thickness = 2.4 };
    private TextBlock? _capCpu, _capMem, _capNet;
    private CompactStats.Items _capItems = CompactStats.Items.None;
    private DateTime _capSampled = DateTime.MinValue;

    public static string CapsuleStatsFile => System.IO.Path.Combine(App.DataDir, "capsule-stats.txt");
    public static CompactStats.Items CapsuleItems
    {
        get { try { return CompactStats.Parse(System.IO.File.Exists(CapsuleStatsFile) ? System.IO.File.ReadAllText(CapsuleStatsFile) : ""); } catch { return CompactStats.Parse(""); } }
        set { try { System.IO.File.WriteAllText(CapsuleStatsFile, CompactStats.Serialize(value)); } catch { } }
    }

    private void SetupIdleExtras(StackPanel wx)
    {
        Grid.SetColumn(_idleStats, 1);
        _idleStats.Visibility = Visibility.Collapsed;
        _idle.Children.Add(_idleStats);
        _idleQuota.Children.Add(_idleQuotaRing);
        _idleQuota.Visibility = Visibility.Collapsed;
        wx.Children.Insert(wx.Children.IndexOf(_idleWeatherGlyph), _idleQuota);
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

    /// <summary>开关 / 选项 / 额度 / 主题 / 倒计时变化时调：决定画什么、胶囊要多宽。</summary>
    public void UpdateIdleExtras()
    {
        var statsOn = Features.On(Features.CapsuleStats) && !Side && !Minimal;
        var items = statsOn ? CapsuleItems : CompactStats.Items.None;
        if (items != _capItems) { _capItems = items; RebuildCapCells(); _capSampled = DateTime.MinValue; }
        // 课程 / 考试倒计时在中间显示时让给它（它更要紧、也只是一会儿）
        var showStats = statsOn && _idleCountdown.Visibility != Visibility.Visible && _idleStats.Children.Count > 0;
        _idleStats.Visibility = showStats ? Visibility.Visible : Visibility.Collapsed;
        foreach (var c in _idleStats.Children.OfType<TextBlock>()) c.Foreground = c.FontSize < 10 ? Theme.Tertiary : Theme.Secondary;
        if (showStats) SampleCapsule(force: true);

        var tight = Features.On(Features.QuotaCapsule) && !Side && !Minimal ? QuotaWatch?.Tightest() : null;
        if (tight != null)
        {
            _idleQuotaRing.Gradient = LevelGradient(tight.Used);
            _idleQuotaRing.Track = Theme.Fill;
            _idleQuotaRing.Progress = Math.Max(0.04, Math.Clamp(tight.Used / 100, 0, 1));
            _idleQuota.Visibility = Visibility.Visible;
        }
        else _idleQuota.Visibility = Visibility.Collapsed;

        // 宽度：中间要放下读数，右边多一个小环；量一次就固定，不随数字变
        var extra = 0.0;
        if (showStats)
        {
            _idleStats.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _idleClock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var wx = (FrameworkElement)_idleTemp.Parent;
            wx.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var need = 16 + _idleClock.DesiredSize.Width + _idleStats.DesiredSize.Width + 20 + wx.DesiredSize.Width + 15;
            extra = Math.Max(0, Math.Ceiling(need - IdleBaseW));
        }
        else if (tight != null) extra = 12;
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
}
