using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Qisland.App;

/// <summary>文件架：拖到岛上的文件只记路径（不复制），存在 %APPDATA%\Qisland\shelf.txt。</summary>
internal sealed class Shelf
{
    public const int Max = 12;
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qisland", "shelf.txt");
    private readonly List<string> _items = [];
    public IReadOnlyList<string> Items => _items;
    public event Action? Changed;

    public Shelf()
    {
        try
        {
            if (File.Exists(FilePath))
                foreach (var l in File.ReadAllLines(FilePath))
                    if (l.Length > 0 && (File.Exists(l) || Directory.Exists(l)) && !_items.Contains(l)) _items.Add(l);
        }
        catch { }
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            _items.Remove(p);
            _items.Insert(0, p);
        }
        while (_items.Count > Max) _items.RemoveAt(_items.Count - 1);
        Save();
    }

    public void Remove(string p) { if (_items.Remove(p)) Save(); }
    public void Clear() { _items.Clear(); Save(); }

    public void Prune()
    {
        if (_items.RemoveAll(p => !File.Exists(p) && !Directory.Exists(p)) > 0) Save();
    }

    private void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllLines(FilePath, _items); } catch { }
        Changed?.Invoke();
    }
}

/// <summary>取文件的大图标：图片直接缩略，其余走系统 256px 图标（没有大图标的退回 48px）。</summary>
internal static class ShellIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "#727")] private static extern int SHGetImageList(int list, ref Guid riid, out IntPtr ppv);
    [DllImport("comctl32.dll")] private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static ImageSource? Get(string path) => Cache.GetOrAdd(path, Load);

    private static ImageSource? Load(string path)
    {
        try
        {
            if (Pictures.Contains(Path.GetExtension(path)) && File.Exists(path))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 144;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }
        catch { }
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x4000); // SHGFI_SYSICONINDEX
            var big = FromList(4, info.iIcon); // SHIL_JUMBO
            if (big != null && !Small(big)) return big;
            return FromList(2, info.iIcon) ?? big; // SHIL_EXTRALARGE
        }
        catch { return null; }
    }

    private static BitmapSource? FromList(int list, int index)
    {
        var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
        if (SHGetImageList(list, ref iid, out var himl) != 0 || himl == IntPtr.Zero) return null;
        var h = ImageList_GetIcon(himl, index, 1);
        if (h == IntPtr.Zero) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        finally { DestroyIcon(h); }
    }

    // 很多老程序没有 256px 图标，系统会把 48px 的贴在左上角，这种就改用 48px 那一档
    private static bool Small(BitmapSource b)
    {
        if (b.PixelWidth < 200) return false;
        var w = b.PixelWidth; var h = b.PixelHeight;
        var px = new byte[w * h * 4];
        b.CopyPixels(px, w * 4, 0);
        for (var y = 0; y < h; y += 2)
            for (var x = 0; x < w; x += 2)
                if (px[(y * w + x) * 4 + 3] > 16 && (x > 100 || y > 100)) return false;
        return true;
    }
}
