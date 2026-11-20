using System.Globalization;

namespace Qisland.Core;

public enum DockSide { Top, Left, Right }

/// <summary>
/// 岛停在哪、收起时多大：顶部居中（默认）/ 左侧 / 右侧停靠，以及极简模式（收起时只剩一小段线）。
/// 侧边停靠：收起是贴边的竖向胶囊（时 / 分两行 + 天气），展开面板从边缘滑出，页面内容和顶部一样。
/// 思路参考小糖岛仔（MIT）的 side_dock / minimal，C# 重写。
/// </summary>
public static class IslandLayout
{
    public const double SideW = 40, SideH = 96, MiniLong = 44, MiniThick = 5, SideInset = 6;

    /// <summary>收起（空闲）时的尺寸和圆角半径。</summary>
    public static (double W, double H, double R) Rest(DockSide dock, bool minimal, double idleW, double idleH)
    {
        if (minimal) return dock == DockSide.Top ? (MiniLong, MiniThick, MiniThick / 2) : (MiniThick, MiniLong, MiniThick / 2);
        return dock == DockSide.Top ? (idleW, idleH, idleH / 2) : (SideW, SideH, SideW / 2);
    }

    /// <summary>极简小条被指针罩住时胀到多大（比原来的 6% 明显，提示「这里能展开」）。</summary>
    public static (double W, double H) MiniSwell(DockSide dock) => dock == DockSide.Top ? (72, 10) : (10, 72);

    /// <summary>胶囊在窗口里的左上角：顶部居中挂在 topGap 下面；侧边贴边、竖直居中。</summary>
    public static (double X, double Y) Origin(DockSide dock, double winW, double winH, double topGap, double w, double h) => dock switch
    {
        DockSide.Left => (SideInset, Math.Round((winH - h) / 2, 2)),
        DockSide.Right => (winW - w - SideInset, Math.Round((winH - h) / 2, 2)),
        _ => ((winW - w) / 2, topGap),
    };

    /// <summary>藏起来（全屏时）往哪个方向滑出去。</summary>
    public static (double X, double Y) HideShift(DockSide dock, double show, double w, double h, double topGap) => dock switch
    {
        DockSide.Left => (-(1 - show) * (w + SideInset + 4), 0),
        DockSide.Right => ((1 - show) * (w + SideInset + 4), 0),
        _ => (0, -(1 - show) * (topGap + h)),
    };

    /// <summary>layout.txt：「dock=left」「minimal=1」一行一项。</summary>
    public static (DockSide Dock, bool Minimal) Parse(string? text)
    {
        var dock = DockSide.Top; var minimal = false;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var l = raw.Trim();
            var eq = l.IndexOf('=');
            if (eq <= 0) continue;
            var k = l[..eq].Trim().ToLowerInvariant(); var v = l[(eq + 1)..].Trim().ToLowerInvariant();
            if (k == "dock") dock = v switch { "left" => DockSide.Left, "right" => DockSide.Right, _ => DockSide.Top };
            else if (k == "minimal") minimal = v is "1" or "true" or "on";
        }
        return (dock, minimal);
    }

    public static string Serialize(DockSide dock, bool minimal) =>
        string.Create(CultureInfo.InvariantCulture, $"dock={dock.ToString().ToLowerInvariant()}\nminimal={(minimal ? 1 : 0)}\n");
}
