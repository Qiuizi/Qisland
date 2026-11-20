namespace Qisland.Core;

/// <summary>
/// 岛的投影：N 层同心圆角矩形，每层扩 spread·(i+1)/N、同样淡，叠起来就是从胶囊边缘向外线性变淡的软影。
/// 左右、上下扩得一样，只整体往下挪一点点（drop 比最小一层的扩展还小，所以上沿也有影子），
/// 看起来是「在胶囊正下方、四周均匀」，不会像一块偏到右下的灰板。
/// 收起时扩展必须落在窗口区域的余量里（左右 6、下 11 DIP），否则会被区域硬切成一块直边的板。
/// </summary>
public static class IslandShadow
{
    public const int Layers = 6;
    public const double CollapsedSpread = 5, ExpandedSpread = 12;
    public const double CollapsedDrop = 1, ExpandedDrop = 3;

    public static (double spread, double drop) Size(double progress)
    {
        var p = Math.Clamp(progress, 0, 1);
        return (CollapsedSpread + (ExpandedSpread - CollapsedSpread) * p, CollapsedDrop + (ExpandedDrop - CollapsedDrop) * p);
    }

    /// <summary>第 i 层（0 最小）的矩形和圆角。</summary>
    public static (double x, double y, double w, double h, double r) Layer(int i, double left, double top, double w, double h, double r, double progress)
    {
        var (spread, drop) = Size(progress);
        var k = spread * (i + 1) / Layers;
        return (left - k, top - k + drop, w + 2 * k, h + 2 * k, r + k);
    }

    /// <summary>每层的透明度（0–255）：总浓度约为主题阴影浓度的 1.2 倍，平均摊到每层。</summary>
    public static byte Alpha(byte themeAlpha) => (byte)Math.Clamp(Math.Round(themeAlpha * 1.2 / Layers), 1, 255);
}
