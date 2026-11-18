using Qisland.Core;
using Xunit;

namespace Qisland.Core.Tests;

/// <summary>矩形取自真机采样（3120×2080，200%，任务栏自动隐藏）。</summary>
public class FullscreenTests
{
    private static readonly Box Mon = new(0, 0, 3120, 2080);

    private static bool Fs(Box win, bool zoomed, bool cap, Box? vis = null, string cls = "Chrome_WidgetWin_1", int st = 0)
        => FullscreenRule.IsFullscreen(win, Mon, zoomed, cap, cls, st, vis);

    // —— 不该藏 ——
    [Fact] public void MaximizedChrome() => Assert.False(Fs(new(-13, -13, 3133, 2093), zoomed: true, cap: true, new(0, 0, 3120, 2080)));
    [Fact] public void MaximizedExplorer() => Assert.False(Fs(new(-13, -13, 3133, 2093), zoomed: true, cap: true, new(0, 0, 3120, 2080), "CabinetWClass"));
    [Fact] public void MaximizedWinForms() => Assert.False(Fs(new(-16, -16, 3136, 2096), zoomed: true, cap: true, new(0, 0, 3120, 2080), "WindowsForms10"));
    [Fact] public void RestoredExplorerSizedToScreenIsNotFullscreen()   // 以前误藏的元凶
        => Assert.False(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: true, new(11, 0, 3109, 2069), "CabinetWClass"));
    [Fact] public void RestoredWindowSizedToScreenAt100Percent()
        => Assert.False(FullscreenRule.IsFullscreen(new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), false, true, "CabinetWClass", 0, new(7, 0, 1913, 1073)));
    [Fact] public void StretchedWindowWithFrameOverhang() => Assert.False(Fs(new(-11, 0, 3131, 2091), zoomed: false, cap: true, new(0, 0, 3120, 2080), "CabinetWClass"));
    [Fact] public void NormalWindow() => Assert.False(Fs(new(526, 268, 2626, 1800), zoomed: false, cap: true, new(537, 268, 2615, 1789)));
    [Fact] public void Desktop() => Assert.False(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false, cls: "WorkerW"));

    // —— 该藏 ——
    [Fact] public void ChromeStartFullscreen() => Assert.True(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false, new(0, 0, 3120, 2080)));
    [Fact] public void ChromeMaximizedThenF11OrVideoFullscreen() => Assert.True(Fs(new(0, 0, 3120, 2080), zoomed: true, cap: false, new(0, 0, 3120, 2080)));
    [Fact] public void ElectronFullscreenOnePixelInsideKeepingCaption() => Assert.True(Fs(new(2, 2, 3122, 2082), zoomed: false, cap: true, new(2, 2, 3122, 2082)));
    [Fact] public void BorderlessForm() => Assert.True(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false, new(0, 0, 3120, 2080), "WindowsForms10"));
    [Fact] public void BorderlessMaximizedForm() => Assert.True(Fs(new(0, 0, 3120, 2080), zoomed: true, cap: false, new(0, 0, 3120, 2080), "WindowsForms10"));
    [Fact] public void NoDwmRectFallsBackToWindowRect() => Assert.True(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false));
    [Fact] public void GameOnSecondMonitor()
        => Assert.True(FullscreenRule.IsFullscreen(new(3120, 0, 5040, 1080), new(3120, 0, 5040, 1080), false, false, "UnityWndClass"));
    [Fact] public void ShellD3DOrPresentation()
    {
        Assert.True(Fs(new(0, 0, 100, 100), false, true, st: 3));
        Assert.True(Fs(new(0, 0, 100, 100), false, true, st: 4));
    }
}
