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
    [Fact] public void MaximizedWithVisibleTaskbarOnlyFillsWorkArea()   // 任务栏常显：最大化只占工作区，没盖满
        => Assert.False(Fs(new(-13, -13, 3133, 1997), zoomed: true, cap: true, new(0, 0, 3120, 1984)));
    [Fact] public void RestoredExplorerSizedToScreenIsNotFullscreen()   // 以前误藏的元凶
        => Assert.False(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: true, new(11, 0, 3109, 2069), "CabinetWClass"));
    [Fact] public void RestoredWindowSizedToScreenAt100Percent()
        => Assert.False(FullscreenRule.IsFullscreen(new(0, 0, 1920, 1080), new(0, 0, 1920, 1080), false, true, "CabinetWClass", 0, new(7, 0, 1913, 1073)));
    [Fact] public void StretchedWindowWithoutDwmFrameIsNotFullscreen()   // 拿不到可见边框时，带框且四边伸出屏幕的是普通窗口
        => Assert.False(Fs(new(-11, 0, 3131, 2091), zoomed: false, cap: true, cls: "CabinetWClass"));
    [Fact] public void StretchedWindowNotCoveringTop() => Assert.False(Fs(new(-11, 40, 3131, 2091), zoomed: false, cap: true, new(0, 40, 3120, 2080)));
    [Fact] public void Taskbar() => Assert.False(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false, new(0, 0, 3120, 2080), "Shell_TrayWnd"));
    [Fact] public void NormalWindow() => Assert.False(Fs(new(526, 268, 2626, 1800), zoomed: false, cap: true, new(537, 268, 2615, 1789)));
    [Fact] public void Desktop() => Assert.False(Fs(new(0, 0, 3120, 2080), zoomed: false, cap: false, cls: "WorkerW"));

    // —— 该藏 ——
    // 任务栏自动隐藏时最大化窗口盖满整块屏：用户的要求是这也算全屏，岛让开（顶边仍能呼出）
    [Fact] public void MaximizedChromeCoveringScreen() => Assert.True(Fs(new(-13, -13, 3133, 2093), zoomed: true, cap: true, new(0, 0, 3120, 2080)));
    [Fact] public void MaximizedExplorerCoveringScreen() => Assert.True(Fs(new(-13, -13, 3133, 2093), zoomed: true, cap: true, new(0, 0, 3120, 2080), "CabinetWClass"));
    [Fact] public void MaximizedWinFormsCoveringScreen() => Assert.True(Fs(new(-16, -16, 3136, 2096), zoomed: true, cap: true, new(0, 0, 3120, 2080), "WindowsForms10"));
    [Fact] public void StretchedWindowWhoseVisibleFrameCovers() => Assert.True(Fs(new(-11, 0, 3131, 2091), zoomed: false, cap: true, new(0, 0, 3120, 2080), "CabinetWClass"));
    [Fact] public void MaximizedWithoutDwmFrame() => Assert.True(Fs(new(-13, -13, 3133, 2093), zoomed: true, cap: true));
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

public class EdgeSummonTests
{
    private static readonly Box Mon = new(0, 0, 3120, 2080);
    private static readonly DateTime T0 = new(2026, 11, 18, 22, 0, 0);

    // 岛在屏幕正中：物理像素 1300..1820
    [Theory]
    [InlineData(1560, 0, true)]
    [InlineData(1300 - 48, 0, true)]     // 左边留 48 像素（24 DIP × 200%）
    [InlineData(1300 - 49, 0, false)]
    [InlineData(1820 + 47, 2, true)]
    [InlineData(1820 + 48, 0, false)]
    [InlineData(100, 0, false)]          // 屏幕左上角（浏览器标签）
    [InlineData(3100, 0, false)]         // 右上角（关闭按钮）
    [InlineData(1560, 3, false)]         // 离开最上面 3 像素
    public void ZoneIsOnlyAboveTheIsland(int x, int y, bool hit)
        => Assert.Equal(hit, EdgeZone.Contains(x, y, Mon, 1300, 1820, 48));

    [Fact] public void ZoneClampedToMonitor() => Assert.True(EdgeZone.Contains(0, 0, Mon, 10, 500, 48));

    [Fact]
    public void DwellNeedsTimeAndStillness()
    {
        var g = new DwellGate(220, 10);
        Assert.False(g.Update(true, 100, 0, T0));
        Assert.False(g.Update(true, 104, 0, T0.AddMilliseconds(150)));
        Assert.True(g.Update(true, 106, 0, T0.AddMilliseconds(230)));
    }

    [Fact]
    public void QuickPassDoesNotTrigger()   // 一路划过去：每次轮询都挪了几十像素
    {
        var g = new DwellGate(220, 10);
        for (var i = 0; i < 30; i++) Assert.False(g.Update(true, 100 + i * 30, 0, T0.AddMilliseconds(i * 16)));
    }

    [Fact]
    public void MovingRestartsTheClock()
    {
        var g = new DwellGate(220, 10);
        g.Update(true, 100, 0, T0);
        Assert.False(g.Update(true, 140, 0, T0.AddMilliseconds(200)));   // 挪了 40：从这里重新计时
        Assert.False(g.Update(true, 141, 0, T0.AddMilliseconds(400)));
        Assert.True(g.Update(true, 142, 0, T0.AddMilliseconds(421)));
    }

    [Fact]
    public void LeavingResets()
    {
        var g = new DwellGate(220, 10);
        g.Update(true, 100, 0, T0);
        g.Update(false, 100, 50, T0.AddMilliseconds(100));
        Assert.False(g.Update(true, 100, 0, T0.AddMilliseconds(250)));
    }
}
