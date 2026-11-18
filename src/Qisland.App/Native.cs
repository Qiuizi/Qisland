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

    public static void SetExStyle(IntPtr hwnd, int flag, bool on)
    {
        var cur = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var next = on ? cur | flag : cur & ~flag;
        if (next != cur) SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(next));
    }

    /// <summary>前台窗口是否覆盖整个显示器（全屏视频、游戏、演示）。</summary>
    public static bool ForegroundIsFullscreen()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
        var sb = new System.Text.StringBuilder(64);
        GetClassName(fg, sb, sb.Capacity);
        var cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        // 最大化的普通窗口（带标题栏）不是全屏。任务栏设成自动隐藏时，最大化窗口的矩形会盖满整块屏（四边各多出几像素边框），
        // 以前会被当成全屏，岛在浏览器、编辑器最大化时一直藏着。真全屏（F11、视频全屏、游戏）都会去掉标题栏或先还原再铺满。
        if (IsZoomed(fg) && ((long)GetWindowLongPtr(fg, -16 /*GWL_STYLE*/) & 0x00C00000 /*WS_CAPTION*/) == 0x00C00000) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        var mon = MonitorFromWindow(fg, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref info)) return false;
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
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
