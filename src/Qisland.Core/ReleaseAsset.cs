namespace Qisland.Core;

/// <summary>
/// 自动更新挑哪个发布附件。每个版本发两个 zip：
/// 「Qisland-x.y.z-win-x64.zip」依赖本机 .NET 8 桌面运行时（体积小）；
/// 「Qisland-x.y.z-full-x64.zip」自带运行时（解压即用）。自带运行时的包名故意不含「win-x64」：
/// 4.2.0 及更早的更新器只认第一个 zip 或名字里带 win-x64 的 zip，这样它们永远拿到依赖运行时的那个，不会装错。
/// </summary>
public static class ReleaseAsset
{
    public static bool IsFull(string name) => name.Contains("-full", StringComparison.OrdinalIgnoreCase);

    /// <summary>越大越合适；-1 = 不能用。selfContained：当前装的是自带运行时的版本（程序目录里有 hostfxr.dll）。</summary>
    public static int Rank(string name, bool selfContained)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return -1;
        var full = IsFull(name);
        // 自带运行时的安装只能用自带运行时的包覆盖（依赖运行时的包会把 runtimeconfig 换掉，没装 .NET 的电脑就起不来了）
        if (selfContained) return full ? 2 : -1;
        if (full) return -1;
        return name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
    }
}
