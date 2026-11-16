using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Qisland.App;

/// <summary>
/// 消息来源的真实软件图标（微信、QQ、网易云……），和桌面、任务栏上看到的是同一个。
/// 找 exe 的顺序：正在运行的进程 → Launcher 里登记的安装路径 → 开始菜单快捷方式。
/// 读图标走后台线程，结果冻结后缓存；拿不到就返回 null，界面退回原来的字形图标。
/// </summary>
public static class AppIcons
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();
    private static readonly ConcurrentDictionary<string, byte> Loading = new();

    /// <summary>岛上的图标重新加载完时触发（在后台线程上），界面据此重建一次。</summary>
    public static event Action? Loaded;

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>有就返回，没有就在后台开始找，这一次先返回 null。</summary>
    public static BitmapSource? Get(string source)
    {
        if (Cache.TryGetValue(source, out var bmp)) return bmp;
        Warm(source);
        return null;
    }

    public static void Warm(string source)
    {
        if (Cache.ContainsKey(source) || !Loading.TryAdd(source, 0)) return;
        Task.Run(() =>
        {
            BitmapSource? b = null;
            try { if (Launcher.ExePath(source) is { } exe) b = Extract(exe); }
            catch (Exception ex) { App.Log($"图标 {source}: {ex.Message}"); }
            Cache[source] = b;
            Loading.TryRemove(source, out _);
            if (b != null) Loaded?.Invoke();
        });
    }

    /// <summary>软件更新换了图标或者刚装上：清掉没找到的，下次再找。</summary>
    public static void ForgetMissing()
    {
        foreach (var (k, v) in Cache) if (v == null) Cache.TryRemove(k, out _);
    }

    internal static string? ProcessPath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); var n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
        }
        finally { CloseHandle(h); }
    }

    private static BitmapSource? Extract(string exe)
    {
        // 96px 够 300% 缩放下的 24px 小方块用；exe 里有 256px 的会按需缩下来
        using var icon = System.Drawing.Icon.ExtractIcon(exe, 0, 96) ?? System.Drawing.Icon.ExtractAssociatedIcon(exe);
        if (icon is null) return null;
        var b = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        b.Freeze();
        return b;
    }
}
