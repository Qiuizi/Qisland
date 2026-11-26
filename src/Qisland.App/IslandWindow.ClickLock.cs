using System.Runtime.InteropServices;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 整岛点击穿透锁定 + 截图录屏时隐藏。
/// · 穿透锁定（参考 pyisland 托盘里的「鼠标穿透」）：打开后整座岛连同胶囊本身都不接鼠标，点击、悬停、滚轮全部落到下面的窗口，
///   岛也不再因为悬停展开、顶边呼出；通知、音乐照常显示。快捷键 Ctrl+Alt+Shift+Q（和隐藏岛的 Ctrl+Alt+Q 挨着）或托盘 / 设置里开关。
///   不跨重启保存：重启后岛总是能点的，免得忘了锁过、以为岛坏了。
///   做法同小糖岛仔（MIT）：WS_EX_LAYERED + WS_EX_TRANSPARENT。非分层窗口的 WS_EX_TRANSPARENT 不会把点击让给别的程序，
///   而 WPF（没开 AllowsTransparency）会在 WM_STYLECHANGING 里把 LAYERED 去掉，所以锁定期间在 WndProc 里把它护住。
/// · 截图录屏时隐藏（参考小糖岛仔 0.6.70，默认关）：SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
///   截图、录屏、共享屏幕里看不到岛，自己屏幕上照常显示。
/// </summary>
public sealed partial class IslandWindow
{
    private const int ClickLockHotkeyId = 0x5152;
    public const string ClickLockHotkeyText = "Ctrl+Alt+Shift+Q";
    public bool ClickLocked { get; private set; }
    public bool ClickLockHotkeyActive { get; private set; }
    public event Action? ClickLockChanged;

    private void SetupClickLock()
    {
        if (SnapshotMode) return;
        ApplyClickLockHotkey();
        ApplyCaptureHide();
        Features.Changed += k =>
        {
            if (k == Features.ClickLockHotkey) Dispatcher.BeginInvoke(ApplyClickLockHotkey);
            else if (k == Features.HideFromCapture) Dispatcher.BeginInvoke(ApplyCaptureHide);
        };
    }

    private void ApplyClickLockHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        UnregHotKey(_hwnd, ClickLockHotkeyId);
        ClickLockHotkeyActive = false;
        if (Features.On(Features.ClickLockHotkey))
        {
            ClickLockHotkeyActive = RegisterHotKey(_hwnd, ClickLockHotkeyId, 0x0002 | 0x0001 | 0x0004 | 0x4000 /*Ctrl+Alt+Shift, NOREPEAT*/, 0x51 /*Q*/);
            if (!ClickLockHotkeyActive) App.Log($"热键 {ClickLockHotkeyText} 被占用：穿透锁定只能从托盘 / 设置里开关");
        }
        ClickLockChanged?.Invoke();
    }

    public void ToggleClickLock(string from) => SetClickLock(!ClickLocked, from);

    public void SetClickLock(bool on, string from)
    {
        if (on == ClickLocked || _hwnd == IntPtr.Zero || SnapshotMode) return;
        ClickLocked = on;
        if (on && _mode == Mode.Expanded) { _pinned = false; SetMode(DesiredRestMode()); }
        if (on) { _hoverGate.Reset(); _edgeGate.Reset(); SetSwell(false); }
        ApplyClickLockStyle();
        App.Log($"点击穿透锁定：{(on ? "开" : "关")}（{from}）");
        ShowLock("点击穿透", on);
        ClickLockChanged?.Invoke();
    }

    private void ApplyClickLockStyle()
    {
        var on = ClickLocked;
        Native.SetExStyle(_hwnd, Native.WS_EX_LAYERED, on);
        if (on)
        {
            // 分层窗口要先给一次不透明度，不然 DWM 认为还没内容、整块不画
            SetLayeredWindowAttributes(_hwnd, 0, 255, 0x2 /*LWA_ALPHA*/);
            Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, true);
            _clickThrough = true;
        }
        _sys?.SetClickLock(on);
        // 摘掉 LAYERED 之后让 DWM 重新取一次画面（不挪不改大小不激活）
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020 /*NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED*/);
        var ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        if (on && (ex & Native.WS_EX_LAYERED) == 0) App.Log("点击穿透锁定：分层样式没加上，胶囊本身可能仍会挡鼠标");
    }

    /// <summary>WndProc 里调用：锁定期间别让 WPF 把 WS_EX_LAYERED 摘掉。</summary>
    private bool KeepLayered(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != 0x007C /*WM_STYLECHANGING*/ || wParam.ToInt64() != Native.GWL_EXSTYLE || !ClickLocked) return false;
        var styleNew = Marshal.ReadInt32(lParam, 4);   // STYLESTRUCT { styleOld, styleNew }
        Marshal.WriteInt32(lParam, 4, styleNew | Native.WS_EX_LAYERED);
        return true;
    }

    private bool _captureHideLogged;

    private void ApplyCaptureHide()
    {
        if (_hwnd == IntPtr.Zero) return;
        var on = Features.On(Features.HideFromCapture);
        GlassBackdrop.KeepExcluded = on;
        // 截屏模糊的玻璃背景本来就要把岛排除在截屏外（不然越叠越糊），两者取「或」
        var ok = SetWindowDisplayAffinity(_hwnd, on || _glassRender == GlassRender.Capture ? 0x11u : 0u);
        SystemGlass.ExcludeFromCapture = on;
        _sys?.ApplyCaptureHide();
        if (on || _captureHideLogged) App.Log($"截图录屏时隐藏：{(on ? "开" : "关")}{(ok ? "" : "（没设上：Win10 2004 以下的系统没有这个功能）")}");
        _captureHideLogged = true;
    }

    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
