using System.Runtime.InteropServices;

namespace Qisland.App;

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x80000;

    public const int DWMWA_NCRENDERING_POLICY = 2;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    private static bool _fast;

    /// <summary>
    /// 动画期间要 120 Hz 的节拍：岛从不是前台窗口，Win11 会给这种进程上 EcoQoS（效率核、降频）并忽略它的定时器精度请求，
    /// WPF 不出画面的那几拍就只能按 15.6 ms 的系统时钟醒来——实测「什么都不动、只挂节拍器」也只有 40–55 fps。
    /// 所以动画开始时：退出执行速度节流和「忽略定时器精度」，把系统定时器调到 1 ms；停稳后立刻还原（空闲不耗电）。
    /// </summary>
    public static void FastTimers(bool on)
    {
        if (on == _fast) return;
        _fast = on;
        try
        {
            // PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 1, IGNORE_TIMER_RESOLUTION = 4；StateMask 置位 = 开节流，清零 = 关
            var p = new PowerThrottling { Version = 1, ControlMask = on ? 1u | 4u : 0u, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), 4 /*ProcessPowerThrottling*/, ref p, Marshal.SizeOf<PowerThrottling>());
            if (on) timeBeginPeriod(1); else timeEndPeriod(1);
        }
        catch { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, byte[] info);
    /// <summary>DWM 全局合成计数（DWM_TIMING_INFO，按 1 字节对齐的偏移读）：刷新次数、合成帧数、迟到 / 丢 / 错过的帧。</summary>
    public static (long refresh, long frame, long late, long dropped, long missed)? DwmTiming()
    {
        var b = new byte[292];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), 292);
        try
        {
            if (DwmGetCompositionTimingInfo(IntPtr.Zero, b) != 0) return null;
            long U(int o) => BitConverter.ToInt64(b, o);
            return (U(36), U(56), U(112), U(212), U(220));
        }
        catch { return null; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage, PrivateWorkingSetSize, SharedCommitUsage;
    }
    [DllImport("psapi.dll", SetLastError = true)] private static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX2 c, uint cb);

    /// <summary>
    /// 三个内存口径（MB）：工作集（含共享的系统 DLL 页）、私有工作集（任务管理器「内存」列默认显示的就是它）、私有提交（Private Bytes）。
    /// 私有工作集要 Windows 10 21H2 之后的结构体，老系统读不到时返回 -1。
    /// </summary>
    public static (double ws, double privateWs, double commit) MemoryMb()
    {
        var c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>() };
        var me = System.Diagnostics.Process.GetCurrentProcess().Handle;
        if (GetProcessMemoryInfo(me, out c, c.cb))
        {
            var pws = c.PrivateWorkingSetSize.ToUInt64();   // 老系统不认识这个字段时会留 0
            return (c.WorkingSetSize.ToUInt64() / 1048576.0, pws == 0 ? -1 : pws / 1048576.0, c.PrivateUsage.ToUInt64() / 1048576.0);
        }
        var p = System.Diagnostics.Process.GetCurrentProcess();
        return (p.WorkingSet64 / 1048576.0, -1, p.PrivateMemorySize64 / 1048576.0);
    }

    public static void SetExStyle(IntPtr hwnd, int flag, bool on)
    {
        var cur = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var next = on ? cur | flag : cur & ~flag;
        if (next != cur) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }

    /// <summary>前台窗口是否全屏（全屏视频、游戏、演示、F11）。规则见 Qisland.Core.FullscreenRule。</summary>
    public static bool ForegroundIsFullscreen(int notifyState = 0) => IsFullscreen(GetForegroundWindow(), notifyState);

    public static bool IsFullscreen(IntPtr fg, int notifyState = 0)
    {
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        if (!GetWindowRect(fg, out var r)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref info)) return false;
        var m = info.rcMonitor;
        var captioned = ((long)GetWindowLongPtr(fg, -16 /*GWL_STYLE*/) & 0x00C00000 /*WS_CAPTION*/) == 0x00C00000;
        Qisland.Core.Box? vis = DwmGetWindowAttribute(fg, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out var d, Marshal.SizeOf<RECT>()) == 0
            ? new(d.Left, d.Top, d.Right, d.Bottom) : null;
        return Qisland.Core.FullscreenRule.IsFullscreen(new(r.Left, r.Top, r.Right, r.Bottom), new(m.Left, m.Top, m.Right, m.Bottom),
            IsZoomed(fg), captioned, sb.ToString(), notifyState, vis);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    /// <summary>调试：前台窗口的判定依据，写进「岛隐藏 / 岛显示」日志，方便查「为什么没藏」。</summary>
    public static string Describe(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        GetWindowRect(fg, out var r);
        var vis = DwmGetWindowAttribute(fg, 9, out var d, Marshal.SizeOf<RECT>()) == 0 ? $"{d.Left},{d.Top},{d.Right},{d.Bottom}" : "-";
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(fg, 2), ref info);
        var m = info.rcMonitor;
        var style = (long)GetWindowLongPtr(fg, -16);
        return $"{sb} rect={r.Left},{r.Top},{r.Right},{r.Bottom} vis={vis} mon={m.Left},{m.Top},{m.Right},{m.Bottom} zoomed={IsZoomed(fg)} style=0x{style:X8}";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);

    /// <summary>主屏刷新率（Hz），读不到时返回 60。</summary>
    public static int RefreshRate()
    {
        var dc = GetDC(IntPtr.Zero);
        try { var hz = GetDeviceCaps(dc, 116 /* VREFRESH */); return hz is > 30 and < 500 ? hz : 60; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);

    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags, Gradient, Anim; }
    [StructLayout(LayoutKind.Sequential)] private struct WinCompAttr { public int Attr; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompAttr d);

    /// <summary>ACCENT_ENABLE_ACRYLICBLURBEHIND：窗口背后的内容做高斯模糊+噪点，颜色由我们自己的半透明底色决定。</summary>
    public static void SetAccent(IntPtr hwnd, bool on)
    {
        var p = new AccentPolicy { State = on ? 3 : 0, Flags = 2, Gradient = on ? 0x01FFFFFF : 0 };
        var size = Marshal.SizeOf<AccentPolicy>();
        var mem = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(p, mem, false);
            var d = new WinCompAttr { Attr = 19, Data = mem, Size = size };
            SetWindowCompositionAttribute(hwnd, ref d);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }
}
