using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Qisland.App;

/// <summary>
/// 性能护栏：岛自己每 5 秒看一眼自己的 CPU、句柄、线程（GetProcessTimes / GetProcessHandleCount，几微秒），
/// 30 秒平均 CPU 超预算就降级——动画帧率降到 60、频谱条停跳、玻璃背景少截屏、歌词计时器放慢；
/// 回落到预算以下一分钟再恢复。每次升降级和每 10 分钟的概况都写进 qisland.log。
/// </summary>
public sealed class PerfGuard
{
    /// <summary>超过这个（占整机 CPU 的百分比，30 秒平均）就降级。</summary>
    public const double Budget = 3.0;
    private const double RestoreBelow = 1.2;

    public static bool Degraded { get; private set; }
    public static event Action? Changed;
    /// <summary>动画该用的帧率：降级时最多 60。</summary>
    public static int Hz => Degraded ? Math.Min(60, App.Hz) : App.Hz;
    public static PerfGuard? Current { get; private set; }

    [DllImport("kernel32.dll")] private static extern bool GetProcessHandleCount(IntPtr h, out uint count);

    private readonly Process _p = Process.GetCurrentProcess();
    private readonly List<double> _samples = [];
    private TimeSpan _lastCpu;
    private long _lastTick;
    private int _n, _calm;
    public double Cpu { get; private set; }
    public double Avg30 { get; private set; }
    public double Peak { get; private set; }
    public int Threads { get; private set; }
    public int Handles { get; private set; }
    public double PrivateMb { get; private set; }

    public PerfGuard() { Current = this; _lastCpu = _p.TotalProcessorTime; _lastTick = Environment.TickCount64; }

    public void Sample()
    {
        var now = Environment.TickCount64;
        var cpu = _p.TotalProcessorTime;
        var wall = now - _lastTick;
        if (wall < 500) return;
        Cpu = (cpu - _lastCpu).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
        _lastCpu = cpu; _lastTick = now;
        _samples.Add(Cpu);
        if (_samples.Count > 12) _samples.RemoveAt(0);
        Avg30 = _samples.TakeLast(6).Average();
        Peak = Math.Max(Peak, Cpu);
        if (GetProcessHandleCount(_p.Handle, out var hc)) Handles = (int)hc;
        _n++;
        if (_n % 12 == 1)   // 线程数要拍系统快照，一分钟一次就够
        {
            _p.Refresh();
            Threads = _p.Threads.Count;
            PrivateMb = _p.PrivateMemorySize64 / 1048576.0;
        }

        if (!Degraded && _samples.Count >= 6 && Avg30 > Budget)
        {
            Degraded = true; _calm = 0;
            App.Log($"性能护栏: 30 秒平均 CPU {Avg30:0.0}% 超过 {Budget}% → 降级（60 fps、频谱静止、玻璃少截屏）");
            Changed?.Invoke();
        }
        else if (Degraded)
        {
            _calm = Cpu < RestoreBelow ? _calm + 1 : 0;
            if (_calm >= 12)
            {
                Degraded = false;
                App.Log($"性能护栏: CPU 回落到 {Avg30:0.0}% → 恢复全部效果");
                Changed?.Invoke();
            }
        }
        if (Threads > 120 || Handles > 4000) { if (_n % 60 == 1) App.Log($"性能护栏: 线程 {Threads} / 句柄 {Handles} 偏多，可能有泄漏"); }
        if (_n % 120 == 0) { App.Log($"perf: CPU 30s 平均 {Avg30:0.00}%，峰值 {Peak:0.0}%，线程 {Threads}，句柄 {Handles}，私有内存 {PrivateMb:0} MB{(Degraded ? "（降级中）" : "")}"); Peak = 0; }
    }

    public string Json() =>
        $"{{\"ok\":true,\"cpu\":{Cpu:0.00},\"cpu_avg30\":{Avg30:0.00},\"threads\":{Threads},\"handles\":{Handles},\"private_mb\":{PrivateMb:0.0},\"degraded\":{(Degraded ? "true" : "false")},\"budget\":{Budget}}}";
}
