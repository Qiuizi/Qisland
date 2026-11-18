namespace Qisland.Core;

/// <summary>窗口 / 显示器矩形（物理像素，right/bottom 不含）。</summary>
public readonly record struct Box(int Left, int Top, int Right, int Bottom);

/// <summary>
/// 判断前台窗口是不是「全屏」（该让岛让开）。纯函数，方便测。规则来自真机采样（Win11 26100、3120×2080、任务栏自动隐藏）：
/// · 覆盖判断用 DWM 的可见边框（DWMWA_EXTENDED_FRAME_BOUNDS），不用 GetWindowRect：
///   普通带框窗口的 GetWindowRect 含四周看不见的缩放边框（200% 时 11 像素），资源管理器「还原」状态下
///   窗口矩形正好等于屏幕、可见部分却缩进 11 像素——以前就是它被当成全屏把岛藏掉。可见边框每边允许 VisibleTolerance 误差
///   （Electron / ChatGPT 全屏会缩进 1–2 像素）。
/// · 最大化（IsZoomed）且带标题栏的普通窗口不算：任务栏自动隐藏时它的可见部分正好盖满整块屏。
///   浏览器 F11 / 网页视频全屏时会去掉标题栏（实测 style 0x170B0000，仍是 zoomed），所以照样算全屏。
/// · 没最大化但被拖得四边都伸出屏幕（左、右、下各 ≥3 像素的边框特征）且带标题栏的，同样是普通窗口。
/// · Shell 报告 D3D 独占全屏 / 演示模式（3 / 4）时直接算全屏。
/// </summary>
public static class FullscreenRule
{
    public const int VisibleTolerance = 4;
    public const int FrameOverhang = 3;

    public static bool IsFullscreen(Box win, Box mon, bool zoomed, bool captioned, string cls, int notifyState = 0, Box? visible = null)
    {
        if (notifyState is 3 or 4) return true;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var v = visible is { } b && b.Right > b.Left && b.Bottom > b.Top ? b : win;
        const int t = VisibleTolerance;
        var covers = v.Left <= mon.Left + t && v.Top <= mon.Top + t && v.Right >= mon.Right - t && v.Bottom >= mon.Bottom - t;
        if (!covers) return false;
        if (zoomed) return !captioned;
        var frame = mon.Left - win.Left >= FrameOverhang && win.Right - mon.Right >= FrameOverhang && win.Bottom - mon.Bottom >= FrameOverhang;
        if (captioned && frame) return false;
        return true;
    }
}
