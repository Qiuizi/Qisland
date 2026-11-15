using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Qisland.App;

/// <summary>组件页用的系统读数：只在组件页可见时每秒采样一次，平时完全不跑。</summary>
internal sealed class SystemStats
{
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus m);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus p);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine, Flag, Percent, SaverOn; public int LifeTime, FullLifeTime; }

    private long _idle, _total;
    private long _rx, _tx;
    private DateTime _netAt;

    public double Cpu { get; private set; }
    public double MemFraction { get; private set; }
    public double MemUsedGb { get; private set; }
    public double MemTotalGb { get; private set; }
    public int? Battery { get; private set; }
    public bool Charging { get; private set; }
    public double DownBps { get; private set; }
    public double UpBps { get; private set; }

    public void Sample()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel + user; // kernel 已含 idle
            var di = idle - _idle; var dt = total - _total;
            if (_total != 0 && dt > 0) Cpu = Math.Clamp(1 - (double)di / dt, 0, 1);
            _idle = idle; _total = total;
        }
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        if (GlobalMemoryStatusEx(ref m) && m.TotalPhys > 0)
        {
            MemTotalGb = m.TotalPhys / 1073741824.0;
            MemUsedGb = (m.TotalPhys - m.AvailPhys) / 1073741824.0;
            MemFraction = MemUsedGb / MemTotalGb;
        }
        if (GetSystemPowerStatus(out var p))
        {
            Battery = p.Flag == 128 || p.Percent == 255 ? null : p.Percent;
            Charging = p.AcLine == 1;
        }
        try
        {
            long rx = 0, tx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = n.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (_netAt != default)
            {
                var sec = Math.Max(0.2, (now - _netAt).TotalSeconds);
                DownBps = Math.Max(0, (rx - _rx) / sec);
                UpBps = Math.Max(0, (tx - _tx) / sec);
            }
            _rx = rx; _tx = tx; _netAt = now;
        }
        catch { }
    }

    public static string Speed(double bps) => bps switch
    {
        >= 1048576 => $"{bps / 1048576:0.0} MB/s",
        >= 1024 => $"{bps / 1024:0} KB/s",
        _ => $"{bps:0} B/s",
    };
}
