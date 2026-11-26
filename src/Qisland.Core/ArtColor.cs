namespace Qisland.Core;

/// <summary>8 位 RGB 颜色。</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// 从专辑封面取一个柔和的主色，给展开音乐卡片的光晕和进度条用。
/// 只做两件事：① 找封面里「有颜色、面积大」的那一簇色相；② 把它钳到不刺眼、不糊字的亮度 / 饱和度区间，
/// 并保证作为进度条时和岛底色至少 3:1 的对比度（WCAG 非文字元素的标准）。文字本身仍用主题色，不吃这个颜色。
/// </summary>
public static class ArtColor
{
    private const int HueBins = 18;

    /// <summary>BGRA32 像素 → 主色；封面几乎全是黑白灰时返回 null（这时候不上色，保持主题原样）。</summary>
    public static Rgb? Dominant(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) return null;
        Span<double> wSum = stackalloc double[HueBins];
        Span<double> rSum = stackalloc double[HueBins];
        Span<double> gSum = stackalloc double[HueBins];
        Span<double> bSum = stackalloc double[HueBins];
        wSum.Clear(); rSum.Clear(); gSum.Clear(); bSum.Clear();
        double total = 0, colorful = 0;
        // 大图隔点取样，封面一般 132 px，全取也就 1.7 万点
        var step = Math.Max(1, Math.Max(width, height) / 96);
        for (var y = 0; y < height; y += step)
        {
            var row = y * stride;
            for (var x = 0; x < width; x += step)
            {
                var i = row + x * 4;
                if (bgra[i + 3] < 128) continue;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
                var (h, s, l) = ToHsl(new Rgb(r, g, b));
                total++;
                // 太黑、太白、太灰的像素不参与（背景、文字、留白）
                if (l < 0.10 || l > 0.93 || s < 0.16) continue;
                // 越鲜艳、亮度越居中的像素越能代表封面的「颜色」
                var w = s * (1 - Math.Abs(2 * l - 1)) + 0.02;
                var bin = (int)(h / 360.0 * HueBins) % HueBins;
                wSum[bin] += w; rSum[bin] += r * w; gSum[bin] += g * w; bSum[bin] += b * w;
                colorful++;
            }
        }
        // 有颜色的像素不到 4%：当成黑白封面
        if (total == 0 || colorful / total < 0.04) return null;
        // 相邻色相合并看（红色常跨 0° 两边），取权重最大的一簇
        int best = 0; double bestW = -1;
        for (var k = 0; k < HueBins; k++)
        {
            var w = wSum[k] + 0.5 * (wSum[(k + 1) % HueBins] + wSum[(k + HueBins - 1) % HueBins]);
            if (w > bestW) { bestW = w; best = k; }
        }
        double ws = 0, rs = 0, gs = 0, bs = 0;
        foreach (var k in new[] { best, (best + 1) % HueBins, (best + HueBins - 1) % HueBins })
        {
            var f = k == best ? 1 : 0.5;
            ws += wSum[k] * f; rs += rSum[k] * f; gs += gSum[k] * f; bs += bSum[k] * f;
        }
        if (ws <= 0) return null;
        return new Rgb(B8(rs / ws), B8(gs / ws), B8(bs / ws));
    }

    /// <summary>
    /// 钳到柔和区间：深色岛上亮度 0.55–0.72、饱和度 0.30–0.60（不发荧光、不发灰）；
    /// 浅色岛上亮度 0.36–0.48，免得在白底上发虚。然后保证和底色至少 minContrast。
    /// </summary>
    public static Rgb Soften(Rgb c, bool darkIsland, double minContrast = 3.0)
    {
        var (h, s, l) = ToHsl(c);
        s = Math.Clamp(s, 0.30, 0.60);
        l = darkIsland ? Math.Clamp(l, 0.55, 0.72) : Math.Clamp(l, 0.36, 0.48);
        var res = FromHsl(h, s, l);
        return EnsureContrast(res, darkIsland ? DarkBase : LightBase, minContrast);
    }

    public static readonly Rgb DarkBase = new(0x0A, 0x0A, 0x0C);
    public static readonly Rgb LightBase = new(0xF5, 0xF5, 0xF7);

    /// <summary>沿亮度方向（远离底色）一点点调，直到对比度够；调到头也不够就返回最接近的。</summary>
    public static Rgb EnsureContrast(Rgb c, Rgb bg, double min)
    {
        if (Contrast(c, bg) >= min) return c;
        var (h, s, l) = ToHsl(c);
        var dir = Luminance(bg) < 0.5 ? 1 : -1;
        for (var i = 0; i < 50; i++)
        {
            l = Math.Clamp(l + dir * 0.02, 0, 1);
            var t = FromHsl(h, s, l);
            if (Contrast(t, bg) >= min || l is 0 or 1) return t;
        }
        return FromHsl(h, s, l);
    }

    /// <summary>a 以 alpha 叠在 b 上的结果（算光晕下面文字的实际底色用）。</summary>
    public static Rgb Over(Rgb a, double alpha, Rgb b) =>
        new(B8(a.R * alpha + b.R * (1 - alpha)), B8(a.G * alpha + b.G * (1 - alpha)), B8(a.B * alpha + b.B * (1 - alpha)));

    /// <summary>WCAG 相对亮度。</summary>
    public static double Luminance(Rgb c)
    {
        static double Ch(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度（1–21）。</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        if (s <= 0) { var v = B8(l * 255); return new Rgb(v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        var hh = h / 360.0;
        return new Rgb(B8(Hue(hh + 1 / 3.0) * 255), B8(Hue(hh) * 255), B8(Hue(hh - 1 / 3.0) * 255));
    }

    private static byte B8(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
