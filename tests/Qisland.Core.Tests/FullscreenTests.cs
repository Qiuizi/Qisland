using Qisland.Core;
using Xunit;

namespace Qisland.Core.Tests;

public class FullscreenTests
{
    private static readonly Box Mon4K = new(0, 0, 3840, 2160);
    private static readonly Box MonSmall = new(0, 0, 1560, 1040);

    [Fact] public void MaximizedExplorerWithAutoHideTaskbarIsNotFullscreen()
        => Assert.False(FullscreenRule.IsFullscreen(new(-16, -16, 3856, 2176), Mon4K, zoomed: true, captioned: true, "CabinetWClass"));

    [Fact] public void MaximizedEdgeWithAutoHideTaskbarIsNotFullscreen()
        => Assert.False(FullscreenRule.IsFullscreen(new(-11, -11, 3851, 2171), Mon4K, zoomed: true, captioned: true, "Chrome_WidgetWin_1"));

    [Fact] public void StretchedNonMaximizedWindowWithFrameOverhangIsNotFullscreen()
        => Assert.False(FullscreenRule.IsFullscreen(new(-11, 0, 3851, 2171), Mon4K, zoomed: false, captioned: true, "CabinetWClass"));

    [Fact] public void ElectronFullscreenKeepingCaptionIsFullscreen()
        => Assert.True(FullscreenRule.IsFullscreen(new(1, 1, 1561, 1041), MonSmall, zoomed: false, captioned: true, "Chrome_WidgetWin_1"));

    [Fact] public void ChromeF11IsFullscreen()
        => Assert.True(FullscreenRule.IsFullscreen(new(0, 0, 3840, 2160), Mon4K, zoomed: false, captioned: false, "Chrome_WidgetWin_1"));

    [Fact] public void CaptionedWindowExactlyOnMonitorIsFullscreen()
        => Assert.True(FullscreenRule.IsFullscreen(new(0, 0, 3840, 2160), Mon4K, zoomed: false, captioned: true, "Chrome_WidgetWin_1"));

    [Fact] public void MaximizedWithoutCaptionIsFullscreen()
        => Assert.True(FullscreenRule.IsFullscreen(new(0, 0, 3840, 2160), Mon4K, zoomed: true, captioned: false, "PotPlayer64"));

    [Fact] public void BorderlessGameOnSecondMonitorIsFullscreen()
        => Assert.True(FullscreenRule.IsFullscreen(new(3840, 0, 5760, 1080), new(3840, 0, 5760, 1080), zoomed: false, captioned: false, "UnityWndClass"));

    [Fact] public void WindowSmallerThanMonitorIsNotFullscreen()
        => Assert.False(FullscreenRule.IsFullscreen(new(0, 0, 3840, 2100), Mon4K, zoomed: false, captioned: false, "Chrome_WidgetWin_1"));

    [Fact] public void DesktopIsNotFullscreen()
        => Assert.False(FullscreenRule.IsFullscreen(new(0, 0, 3840, 2160), Mon4K, zoomed: false, captioned: false, "WorkerW"));

    [Fact] public void ShellD3DOrPresentationStateIsFullscreen()
    {
        Assert.True(FullscreenRule.IsFullscreen(new(0, 0, 100, 100), Mon4K, false, true, "x", notifyState: 3));
        Assert.True(FullscreenRule.IsFullscreen(new(0, 0, 100, 100), Mon4K, false, true, "x", notifyState: 4));
    }
}
