namespace Qisland.Core;

/// <summary>窗口 / 显示器矩形（物理像素，right/bottom 不含）。</summary>
public readonly record struct Box(int Left, int Top, int Right, int Bottom);

/// <summary>
/// 判断前台窗口是不是「全屏」（该让岛让开）。纯函数，方便测。规则来自真机采样（Win11 26100、3120×2080、任务栏自动隐藏）：
/// · 只看一件事：窗口看得见的部分有没有盖满整块屏。用 DWM 的可见边框（DWMWA_EXTENDED_FRAME_BOUNDS），不用 GetWindowRect：
///   普通带框窗口的 GetWindowRect 含四周看不见的缩放边框（200% 时 11 像素），资源管理器「还原」状态下
///   窗口矩形正好等于屏幕、可见部分却缩进 11 像素——它没盖满，不算。可见边框每边允许 VisibleTolerance 误差
///   （Electron / ChatGPT 全屏会缩进 1–2 像素）。
/// · 最大化窗口也一样：任务栏自动隐藏时它盖满整块屏，就算全屏，岛让开（顶边仍可呼出）；
///   任务栏常显时最大化只占工作区，没盖满，岛照常显示。
/// · 拿不到可见边框（DWM 关闭等）才退回窗口矩形；这时带标题栏、四边都伸出屏幕的「拖大的普通窗口」不算。
/// · Shell 报告 D3D 独占全屏 / 演示模式（3 / 4）时直接算全屏；桌面 / 任务栏永远不算。
/// </summary>
public static class FullscreenRule
{
    public const int VisibleTolerance = 4;
    public const int FrameOverhang = 3;

    public static bool IsFullscreen(Box win, Box mon, bool zoomed, bool captioned, string cls, int notifyState = 0, Box? visible = null)
    {
        if (notifyState is 3 or 4) return true;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var hasVisible = visible is { } b && b.Right > b.Left && b.Bottom > b.Top;
        var v = hasVisible ? visible!.Value : win;
        const int t = VisibleTolerance;
        var covers = v.Left <= mon.Left + t && v.Top <= mon.Top + t && v.Right >= mon.Right - t && v.Bottom >= mon.Bottom - t;
        if (!covers) return false;
        if (hasVisible || zoomed) return true;
        var frame = mon.Left - win.Left >= FrameOverhang && win.Right - mon.Right >= FrameOverhang && win.Bottom - mon.Bottom >= FrameOverhang;
        return !(captioned && frame);
    }
}

/// <summary>
/// 「停一下才算」：指针在某个区域里连续停够 DwellMs、且这段时间内挪动不超过 StillPx，才触发一次。
/// 一路划过去（去点浏览器标签 / 关闭按钮）不会触发；挪动超过 StillPx 就从当前位置重新计时。
/// </summary>
public sealed class DwellGate
{
    public int DwellMs { get; }
    public int StillPx { get; }
    private DateTime _since = DateTime.MaxValue;
    private int _ax, _ay;

    public DwellGate(int dwellMs, int stillPx) { DwellMs = dwellMs; StillPx = stillPx; }

    public void Reset() => _since = DateTime.MaxValue;

    /// <summary>每次轮询调用；active = 指针在区域里（且没按鼠标键）。返回 true 表示该触发了。</summary>
    public bool Update(bool active, int x, int y, DateTime now)
    {
        if (!active) { _since = DateTime.MaxValue; return false; }
        if (_since == DateTime.MaxValue || Math.Abs(x - _ax) > StillPx || Math.Abs(y - _ay) > StillPx)
        {
            _since = now; _ax = x; _ay = y; return false;
        }
        return (now - _since).TotalMilliseconds >= DwellMs;
    }
}

/// <summary>顶边呼出的热区：只在岛正上方那一段（左右各留 Margin），屏幕最上面 Depth 像素。坐标均为物理像素。</summary>
public static class EdgeZone
{
    public const int Depth = 3;

    public static bool Contains(int x, int y, Box mon, int spanLeft, int spanRight, int margin)
        => y >= mon.Top && y < mon.Top + Depth
           && x >= Math.Max(mon.Left, spanLeft - margin) && x < Math.Min(mon.Right, spanRight + margin);
}
