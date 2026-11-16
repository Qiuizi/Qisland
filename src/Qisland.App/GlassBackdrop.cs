using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 真·透明玻璃的底板。系统亚克力在这台 Win11 上不跟窗口区域走（会糊满整块方形窗口），
/// 所以自己做：把岛自己从截屏里排除（WDA_EXCLUDEFROMCAPTURE），每 50 ms 截岛背后那一小块屏幕，
/// 截的时候顺手缩到 1/4（StretchBlt HALFTONE），在 CPU 上对这张小图做三遍盒式模糊 + 提饱和，
/// 再由 GPU 双线性放大回去——放大本身又是一层柔化，所以看起来像大半径高斯模糊。
/// 形状由外层 Canvas 的 Clip（就是胶囊的几何）决定，弹簧每帧都跟着走，不需要每帧改窗口区域。
/// </summary>
public sealed class GlassBackdrop
{
    private const int Down = 4;
    private readonly Window _win;
    private readonly Func<Rect?> _area;
    private readonly Image _img = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _wb;
    private IntPtr _memDc, _dib, _bits, _old;
    private int _bw, _bh;
    private byte[] _buf = [], _tmp = [];
    private bool _on;

    public Canvas Host { get; } = new() { IsHitTestVisible = false };
    /// <summary>岛整体被平移（全屏自动隐藏的滑出）时，截屏位置要跟着挪。</summary>
    public Func<double> ShiftY { get; set; } = () => 0;
    /// <summary>岛正在变形时为 true：这时背后的画面基本不变，不必每 50 ms 截一次屏（截屏会和动画抢 UI 线程）。</summary>
    public Func<bool> Animating { get; set; } = () => false;
    private Rect _lastArea;
    private long _lastCap, _slowLogged;

    public GlassBackdrop(Window win, Func<Rect?> area, double width, double height)
    {
        _win = win; _area = area;
        Host.Width = width; Host.Height = height;
        Host.Children.Add(_img);
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.Linear);
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Capture();
        Host.Visibility = Visibility.Collapsed;
    }

    public void Enable(IntPtr hwnd, bool on)
    {
        if (on == _on) return;
        _on = on;
        // 把自己排除在截屏之外，否则会截到自己、越叠越糊。副作用：录屏/截图里看不到岛（只在玻璃主题下）。
        SetWindowDisplayAffinity(hwnd, on ? 0x11u : 0u);
        Host.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) { Capture(); _timer.Start(); } else { _timer.Stop(); _img.Source = null; _wb = null; Free(); }
    }

    private void Capture()
    {
        if (!_on) return;
        var area = _area();
        if (area is not { } a || a.Width < 4 || a.Height < 4) return;
        var now = Environment.TickCount64;
        // 变形动画期间沿用上一帧背景：每次 GDI 截屏要在 UI 线程上花 10ms+，会把 120 帧的动画压到 60 帧。
        if (Animating() && _wb != null) return;
        _lastArea = a; _lastCap = now;
        var sw0 = System.Diagnostics.Stopwatch.StartNew();
        try { CaptureCore(a); }
        finally
        {
            var ms = sw0.Elapsed.TotalMilliseconds;
            if (ms > 8 && now - _slowLogged > 60000) { _slowLogged = now; App.Log($"glass capture {ms:0.0} ms ({a.Width:0}×{a.Height:0})"); }
        }
    }

    private void CaptureCore(Rect a)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(_win).Handle;
        if (!Native.GetWindowRect(hwnd, out var wr)) return;
        var s = VisualTreeHelper.GetDpi(_win).DpiScaleX;
        int sx = wr.Left + (int)Math.Floor(a.X * s), sy = wr.Top + (int)Math.Floor((a.Y + ShiftY()) * s);
        int sw = (int)Math.Ceiling(a.Width * s), sh = (int)Math.Ceiling(a.Height * s);
        int bw = Math.Max(4, sw / Down), bh = Math.Max(4, sh / Down);
        if (bw != _bw || bh != _bh) Alloc(bw, bh);
        var screen = GetDC(IntPtr.Zero);
        try
        {
            SetStretchBltMode(_memDc, 4); // HALFTONE：缩小时取平均，不会闪烁
            SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
            StretchBlt(_memDc, 0, 0, bw, bh, screen, sx, sy, sw, sh, 0x00CC0020);
        }
        finally { ReleaseDC(IntPtr.Zero, screen); }
        GdiFlush();
        var n = bw * bh * 4;
        Marshal.Copy(_bits, _buf, 0, n);
        Blur(_buf, _tmp, bw, bh, 2); Blur(_buf, _tmp, bw, bh, 2); Blur(_buf, _tmp, bw, bh, 1);
        Saturate(_buf, n, 1.35);
        if (_wb == null || _wb.PixelWidth != bw || _wb.PixelHeight != bh)
        {
            _wb = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgr32, null);
            _img.Source = _wb;
        }
        _wb.WritePixels(new Int32Rect(0, 0, bw, bh), _buf, bw * 4, 0);
        Canvas.SetLeft(_img, Math.Floor(a.X * s) / s);
        Canvas.SetTop(_img, Math.Floor(a.Y * s) / s);
        _img.Width = bw * Down / s; _img.Height = bh * Down / s;
    }

    /// <summary>可分离盒式模糊（横一遍、竖一遍），三遍近似高斯。图很小（约 110×140），一遍不到 0.3 ms。</summary>
    private static void Blur(byte[] p, byte[] t, int w, int h, int r)
    {
        var d = 2 * r + 1;
        for (var y = 0; y < h; y++)
            for (var c = 0; c < 3; c++)
            {
                int row = y * w * 4, sum = 0;
                for (var i = -r; i <= r; i++) sum += p[row + Math.Clamp(i, 0, w - 1) * 4 + c];
                for (var x = 0; x < w; x++)
                {
                    t[row + x * 4 + c] = (byte)(sum / d);
                    sum += p[row + Math.Min(x + r + 1, w - 1) * 4 + c] - p[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
        for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++)
            {
                int sum = 0;
                for (var i = -r; i <= r; i++) sum += t[Math.Clamp(i, 0, h - 1) * w * 4 + x * 4 + c];
                for (var y = 0; y < h; y++)
                {
                    p[y * w * 4 + x * 4 + c] = (byte)(sum / d);
                    sum += t[Math.Min(y + r + 1, h - 1) * w * 4 + x * 4 + c] - t[Math.Max(y - r, 0) * w * 4 + x * 4 + c];
                }
            }
    }

    /// <summary>苹果的玻璃会把背后的颜色「提纯」一点，这里按亮度把色彩往外推。</summary>
    private static void Saturate(byte[] p, int n, double k)
    {
        for (var i = 0; i < n; i += 4)
        {
            double b = p[i], g = p[i + 1], r = p[i + 2];
            var l = 0.299 * r + 0.587 * g + 0.114 * b;
            p[i] = (byte)Math.Clamp(l + (b - l) * k, 0, 255);
            p[i + 1] = (byte)Math.Clamp(l + (g - l) * k, 0, 255);
            p[i + 2] = (byte)Math.Clamp(l + (r - l) * k, 0, 255);
            p[i + 3] = 255;
        }
    }

    private void Alloc(int w, int h)
    {
        Free();
        _bw = w; _bh = h;
        var screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        _dib = CreateDIBSection(_memDc, ref bi, 0, out _bits, IntPtr.Zero, 0);
        _old = SelectObject(_memDc, _dib);
        _buf = new byte[w * h * 4]; _tmp = new byte[w * h * 4];
    }

    private void Free()
    {
        if (_memDc != IntPtr.Zero) { SelectObject(_memDc, _old); DeleteDC(_memDc); _memDc = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { Native.DeleteObject(_dib); _dib = IntPtr.Zero; }
        _bw = _bh = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr d, int dx, int dy, int dw, int dh, IntPtr s, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
