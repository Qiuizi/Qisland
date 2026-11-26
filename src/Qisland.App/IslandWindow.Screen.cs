using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 屏幕相关：
/// · 多显示器：岛跟着「前台窗口所在的那块屏」走（不跟鼠标，避免乱跳），换屏后按那块屏的 DPI 重新居中。
/// · 全屏 / 游戏自动隐藏：只看岛所在那块屏；D3D 独占全屏和演示模式也算。
/// · fullscreen.txt：show 名单全屏也照常显示，hide 名单只要在前台就隐藏（设置页可编辑，见 HideRules；
///   「按程序隐藏」可整体关掉；可选「只在挡住岛时才藏」，参考小糖岛仔的自动避让）。
/// · Ctrl+Alt+Q：全屏时临时叫回来；平时一键隐藏 / 显示。
/// </summary>
public sealed partial class IslandWindow
{
    private IntPtr _monitor;
    private Native.RECT _monRect;
    private bool _winAtTop;
    private bool _manualHide;
    private IntPtr _override;           // 用热键叫回来的那个全屏窗口
    private IntPtr _lastFg;
    private readonly Dictionary<uint, string> _procNames = new();
    private HashSet<string> _showList = new(StringComparer.OrdinalIgnoreCase), _hideList = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _listsStamp = DateTime.MinValue;
    private const int HotkeyId = 0x5149;

    public static string FullscreenListPath => Path.Combine(App.DataDir, "fullscreen.txt");

    private void SetupScreen()
    {
        if (SnapshotMode) return;
        if (!RegisterHotKey(_hwnd, HotkeyId, 0x0002 | 0x0001 | 0x4000 /*Ctrl+Alt, NOREPEAT*/, 0x51 /*Q*/))
            App.Log("热键 Ctrl+Alt+Q 被占用");
        ApplyAiHotkeys();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(() => { MoveTo(_monitor); ApplyFrame(); });
        if (!File.Exists(FullscreenListPath))
        {
            try
            {
                File.WriteAllText(FullscreenListPath,
                    "# 全屏时灵动岛默认自动隐藏。这里可以按程序名单独设置（进程名，不带 .exe，# 开头是注释）\n" +
                    "# show 名单：全屏也照常显示，例如看视频时仍想看到音量条和通知\n" +
                    "# show PotPlayerMini64\n" +
                    "# hide 名单：只要它在前台就隐藏，哪怕不是全屏，例如游戏启动器\n" +
                    "# hide VALORANT-Win64-Shipping\n");
            }
            catch { }
        }
    }

    private void LoadLists()
    {
        try
        {
            var t = File.GetLastWriteTime(FullscreenListPath);
            if (t == _listsStamp) return;
            _listsStamp = t;
            var show = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hide = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(FullscreenListPath))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                var parts = l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;
                var name = parts[1].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
                if (parts[0] is "show" or "显示") show.Add(name);
                else if (parts[0] is "hide" or "隐藏") hide.Add(name);
            }
            _showList = show; _hideList = hide;
        }
        catch { }
    }

    private void OnHotkey()
    {
        var fg = GetForegroundWindow();
        if (_hidden && !_manualHide) { _override = fg; App.Log("热键：全屏中临时显示"); }
        else { _manualHide = !_manualHide; _override = IntPtr.Zero; App.Log(_manualHide ? "热键：隐藏" : "热键：显示"); }
        UpdateVisibility();
    }

    /// <summary>每秒一次：决定显示 / 隐藏，并跟随前台窗口换屏。</summary>
    public bool SnapshotMode { get; init; }

    private void UpdateVisibility()
    {
        if (SnapshotMode) return;
        var fg = GetForegroundWindow();
        if (fg != _lastFg)
        {
            _lastFg = fg;
            if (fg != _override) _override = IntPtr.Zero;
            Follow(fg);
        }
        LoadLists();
        var proc = ProcName(fg);
        var listed = proc.Length > 0 && Features.On(Features.HideList) && _hideList.Contains(proc)
                     && (!Features.On(Features.HideOnlyCovering) || CoversIsland(fg));
        bool hide;
        if (_manualHide) hide = true;
        else if (listed) hide = true;
        else if (fg == _override && fg != IntPtr.Zero) hide = false;
        else if (proc.Length > 0 && _showList.Contains(proc)) hide = false;
        else hide = FullscreenOnMyScreen(fg);
        if (OsdActive) hide = false;   // 按音量键时总要看到反馈
#if DEBUG
        if (Perf.ForceShow && !_manualHide) hide = false;   // 调试版量内存：前台全屏也不藏，量的是岛露出来时的真实占用
#endif
        if (DateTime.Now < _benchShowUntil && !_manualHide) hide = false;   // 本机测速期间：全屏程序在前台也让岛露出来，量的才是真实画面
        if (_edgeShow && (_mode == Mode.Expanded || _animating) && !_manualHide) hide = false;   // 顶边呼出的岛展开期间不藏
        else _edgeShow = false;
        if (hide != _hidden)
        {
            _hidden = hide;
            // 显示 / 隐藏切换不频繁（换前台程序才会变），记下来方便查「岛怎么不见了」
            App.Log((hide ? $"岛隐藏：前台 {(proc.Length > 0 ? proc : "?")}{(_manualHide ? "（手动）" : listed ? "（hide 名单）" : "（全屏）")}" : $"岛显示：前台 {(proc.Length > 0 ? proc : "?")}")
                + $" · {Native.Describe(fg)}");
            _show.Target = hide ? 0 : 1;
            if (hide && _mode == Mode.Expanded && !_pinned) SetMode(DesiredRestMode());
            Kick();
        }
    }

    private DateTime _benchShowUntil = DateTime.MinValue;

    /// <summary>
    /// 前台窗口有没有盖到岛（「只在挡住岛时才藏」用）：按窗口实际可见边框（DWM 扩展边框，不含阴影）和岛此刻的胶囊位置比；
    /// 最小化、读不到边框时按「盖住」算，退回原来的规则，宁可藏也不挡人。
    /// </summary>
    private bool CoversIsland(IntPtr fg)
    {
        if (fg == IntPtr.Zero || IsIconic(fg)) return false;
        if (DwmGetWindowAttribute(fg, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out var r, Marshal.SizeOf<Native.RECT>()) != 0 && !Native.GetWindowRect(fg, out r)) return true;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var (pl, pt) = Origin(_w.Target, _h.Target);
        var l = (int)Math.Round((Left + pl) * dpi.DpiScaleX); var t = (int)Math.Round((Top + pt) * dpi.DpiScaleY);
        var rr = (int)Math.Round((Left + pl + _w.Target) * dpi.DpiScaleX); var b = (int)Math.Round((Top + pt + _h.Target) * dpi.DpiScaleY);
        return r.Left < rr && r.Right > l && r.Top < b && r.Bottom > t;
    }

    private bool FullscreenOnMyScreen(IntPtr fg)
    {
        if (SHQueryUserNotificationState(out var st) == 0 && st is 3 or 4) return true;   // D3D 独占全屏 / 演示模式
        if (fg == IntPtr.Zero || fg == _hwnd || !Native.IsFullscreen(fg)) return false;
        // 用岛窗口此刻所在的屏比较，不用缓存的 _monitor：插拔显示器 / 改分辨率后旧句柄会失效，导致全屏永远判不出来
        return MonitorFromWindow(fg, 2) == MonitorFromWindow(_hwnd, 2);
    }

    private void Follow(IntPtr fg)
    {
        if (fg == IntPtr.Zero || fg == _hwnd || GetSystemMetrics(80 /*SM_CMONITORS*/) < 2) return;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return;
        var mon = MonitorFromWindow(fg, 2);
        if (mon == _monitor || mon == IntPtr.Zero) return;
        if (_mode == Mode.Expanded || _animating || _hoverSince != DateTime.MaxValue) { _lastFg = IntPtr.Zero; return; }   // 下一秒再试
        MoveTo(mon);
    }

    private void PlaceOnScreen()
    {
        if (SnapshotMode) return;
        var mon = _monitor;
        // 显示器拔掉 / 重新排列后旧的句柄会失效：失效就回主屏，不然岛会留在一块已经不存在的屏上
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (mon == IntPtr.Zero || GetSystemMetrics(80) < 2 || !Native.GetMonitorInfo(mon, ref info)) mon = MonitorFromPoint(new POINT(), 1 /*PRIMARY*/);
        MoveTo(mon);
    }

    // ───────────── 置顶守护 ─────────────
    // 别的程序（全屏游戏切换、某些启动器、Electron 窗口）可能把岛的 WS_EX_TOPMOST 摘掉，岛就沉到普通窗口后面去了。
    // 每 2 秒看一眼这个位，掉了才补回（不去和截图工具这类同样置顶的窗口抢层级）；展开 / 偷看时主动顶到置顶层最上面。
    private int _topLost;
    private void GuardTopmost()
    {
        if (_hwnd == IntPtr.Zero || SnapshotMode) return;
        _sys?.Below(); // 系统模糊底板：别的置顶窗口插到岛和底板之间时压回来（已经紧贴就什么都不做）
        if (((long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE) & 0x8 /*WS_EX_TOPMOST*/) != 0) return;
        _topLost++;
        if (_topLost <= 3 || _topLost % 50 == 0) App.Log($"置顶被别的程序摘掉了，补回（第 {_topLost} 次）");
        RaiseTop();
    }

    private void RaiseTop()
    {
        if (_hwnd == IntPtr.Zero || SnapshotMode) return;
        SetWindowPos(_hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200 /*NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER*/);
        _sys?.Below();
    }

    /// <summary>睡眠唤醒、解锁、远程桌面连回、改时区之后：重新摆位置、补置顶、刷新时钟和毛玻璃底。</summary>
    public void OnResume()
    {
        PlaceOnScreen();
        RaiseTop();
        _clockText = "";
        OnSecond();
        if (_glass != null && _glass.Host.Visibility == Visibility.Visible) _glass.Refresh();
        ApplyFrame();
    }

    // ───────────── OLED 防烧屏（默认关） ─────────────
    // 岛常驻屏幕顶部同一位置，OLED 屏久了会留影。打开后每 3 分钟在 0 → 1 → 2 → 1 → 0 → -1 → -2 → -1 像素间挪一格，
    // 只在收起且静止时挪（不打断动画和悬停）。参考小糖岛仔（MIT）的像素位移。
    private static readonly int[] OledCycle = [0, 1, 2, 1, 0, -1, -2, -1];
    private int _oledStep, _oledOffset;

    private void OledTick(DateTime now)
    {
        if (now.Second != 30 || now.Minute % 3 != 0) return;
        var on = Features.On(Features.OledShift);
        if (!on && _oledOffset == 0) return;
        if (_mode == Mode.Expanded || _animating || _hoverSince != DateTime.MaxValue) return;
        _oledStep = on ? (_oledStep + 1) % OledCycle.Length : 0;
        _oledOffset = OledCycle[_oledStep];
        MoveTo(_monitor);
    }

    private void MoveTo(IntPtr mon)
    {
        if (_hwnd == IntPtr.Zero || mon == IntPtr.Zero) return;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref info)) return;
        if (GetDpiForMonitor(mon, 0, out var dx, out _) != 0) dx = 96;
        var widthPx = (int)Math.Round(WinW * dx / 96.0);
        var m = info.rcMonitor; var w = info.rcWork;
        var shift = (int)Math.Round(_oledOffset * dx / 96.0);
        int x, y;
        if (Dock == DockSide.Top)
        {
            x = m.Left + (m.Right - m.Left - widthPx) / 2 + shift;
            y = w.Top > m.Top + 1 ? w.Top : m.Top;   // 任务栏在顶部时让开它
        }
        else
        {
            // 侧边：窗口贴住屏幕左 / 右边（任务栏在那一侧时贴工作区），竖直居中在工作区里；OLED 位移改成上下挪
            var heightPx = (int)Math.Round(WinH * dx / 96.0);
            x = Dock == DockSide.Left ? w.Left : w.Right - widthPx;
            y = w.Top + (w.Bottom - w.Top - heightPx) / 2 + shift;
        }
        _monitor = mon;
        _monRect = Dock == DockSide.Top ? m : w; _winAtTop = Dock == DockSide.Top && y == m.Top;
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    private string ProcName(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        GetWindowThreadProcessId(h, out var pid);
        if (_procNames.TryGetValue(pid, out var n)) return n;
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch { n = ""; }
        if (_procNames.Count > 200) _procNames.Clear();
        return _procNames[pid] = n;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dx, out uint dy);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Native.RECT value, int size);
}
