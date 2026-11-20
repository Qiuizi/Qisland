namespace Qisland.Core;

/// <summary>
/// 一次动画（从开始推弹簧到停稳）的逐帧统计：帧间隔、UI 线程每帧耗时（推弹簧 + 布局 + 渲染提交），
/// 以及期间发生的「贵」事件（改窗口区域、改窗口高度、布局次数、玻璃贴图、GC），写进日志一行。
/// </summary>
public sealed class FrameStats
{
    private readonly List<double> _dt = new(256);
    private readonly List<double> _ui = new(256);
    private readonly Dictionary<string, int> _events = new();
    private int _gc0, _gc1, _gc2;

    public int Frames => _dt.Count;
    public double Duration { get; private set; }
    public bool Running { get; private set; }

    public void Begin()
    {
        _dt.Clear(); _ui.Clear(); _events.Clear(); Duration = 0; Running = true;
        (_gc0, _gc1, _gc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }

    /// <summary>记一帧：dt 是和上一帧的间隔（秒；第一帧传 null，不算间隔）。</summary>
    public void Frame(double? dt)
    {
        if (!Running) return;
        if (dt is { } d) { _dt.Add(d); Duration += d; }
    }

    /// <summary>UI 线程这一帧忙了多久（毫秒）。</summary>
    public void Ui(double ms) { if (Running) _ui.Add(ms); }

    public void Count(string what, int n = 1)
    {
        if (!Running) return;
        _events[what] = (_events.TryGetValue(what, out var v) ? v : 0) + n;
    }

    public int Events(string what) => _events.TryGetValue(what, out var v) ? v : 0;

    /// <summary>超过 1.5 个刷新周期算掉帧（至少 12.5 ms，免得 60 Hz 屏上全算掉帧）。</summary>
    public static double SlowThreshold(int hz) => Math.Max(1.5 / Math.Max(1, hz), 0.0125);

    public int Slow(int hz) => _dt.Count(d => d > SlowThreshold(hz));

    /// <summary>按刷新率算这段时间里本该出多少帧。</summary>
    public int Expected(int hz) => (int)Math.Round(Duration * hz);

    public static double Percentile(IReadOnlyList<double> xs, double p)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        var i = (int)Math.Ceiling(p * s.Count) - 1;
        return s[Math.Clamp(i, 0, s.Count - 1)];
    }

    public double DtP95Ms => Percentile(_dt, 0.95) * 1000;
    public double DtMaxMs => _dt.Count == 0 ? 0 : _dt.Max() * 1000;
    public double UiAvgMs => _ui.Count == 0 ? 0 : _ui.Average();
    public double UiMaxMs => _ui.Count == 0 ? 0 : _ui.Max();

    public string End(string label, int hz)
    {
        Running = false;
        Count("gc0", GC.CollectionCount(0) - _gc0);
        Count("gc1", GC.CollectionCount(1) - _gc1);
        Count("gc2", GC.CollectionCount(2) - _gc2);
        var fps = Duration > 0 ? _dt.Count / Duration : 0;
        var ev = string.Join(" ", _events.Where(kv => kv.Value != 0).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        return $"anim → {label}: {_dt.Count + 1} 帧 / {Duration:0.00}s（应 {Expected(hz) + 1}）{fps:0} fps, 掉帧 {Slow(hz)}, p95 {DtP95Ms:0.0} ms, 最长 {DtMaxMs:0} ms; UI 每帧 平均 {UiAvgMs:0.0} / 最长 {UiMaxMs:0.0} ms{(ev.Length > 0 ? "; " + ev : "")}";
    }
}
