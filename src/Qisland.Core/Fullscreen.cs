namespace Qisland.Core;

/// <summary>窗口 / 显示器矩形（物理像素，right/bottom 不含）。</summary>
public readonly record struct Box(int Left, int Top, int Right, int Bottom);

/// <summary>
/// 判断前台窗口是不是「全屏」（该让岛让开）。纯函数，方便测：
/// · 最大化（IsZoomed）且带标题栏的普通窗口不算：任务栏自动隐藏时它会盖满整块屏，四边还伸出几像素看不见的缩放边框。
/// · 没最大化、却被拖 / 吸附得四边都伸出屏幕（左、右、下各 ≥3 像素的边框特征）且带标题栏的，同样是普通窗口。
/// · 其余盖满整块屏（每边允许 Tolerance 像素误差）的都算全屏：F11、网页视频全屏、Electron 全屏（这类保留 WS_CAPTION）、
///   PotPlayer、无边框游戏。
/// · Shell 报告 D3D 独占全屏 / 演示模式（3 / 4）时直接算全屏。
/// </summary>
public static class FullscreenRule
{
    public const int Tolerance = 8;
    public const int FrameOverhang = 3;

    public static bool IsFullscreen(Box win, Box mon, bool zoomed, bool captioned, string cls, int notifyState = 0)
    {
        if (notifyState is 3 or 4) return true;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var covers = win.Left <= mon.Left + Tolerance && win.Top <= mon.Top + Tolerance
                  && win.Right >= mon.Right - Tolerance && win.Bottom >= mon.Bottom - Tolerance;
        if (!covers) return false;
        if (zoomed) return !captioned;
        var frame = mon.Left - win.Left >= FrameOverhang && win.Right - mon.Right >= FrameOverhang && win.Bottom - mon.Bottom >= FrameOverhang;
        if (captioned && frame) return false;
        return true;
    }
}
