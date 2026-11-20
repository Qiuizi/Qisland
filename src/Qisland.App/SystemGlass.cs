using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace Qisland.App;

/// <summary>
/// 液态玻璃的系统模糊底（思路参考岛仔 composition.rs，C# 自行实现）：
/// 单独一个「底板」窗口——无重定向位图（WS_EX_NOREDIRECTIONBITMAP）、不激活、置顶、点不到——
/// 和岛窗口同位置同大小、z 序紧贴在岛下面；里面只有一块 Windows.UI.Composition 视觉，
/// 用系统亚克力那支宿主背景画刷（实时模糊身后的桌面），按岛的圆角胶囊裁剪，跟着弹簧逐帧改形状。
/// 模糊由 DWM 自己做：不截屏、不在本进程里算模糊、不往 WPF 里贴图；停稳后什么都不跑。
/// 岛的着色、高光、描边和文字仍由上层 WPF 画。
/// </summary>
internal sealed class SystemGlass : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct DQOptions { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(DQOptions o, out IntPtr controller);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowExW(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private delegate int CreateTargetFn(IntPtr self, IntPtr hwnd, int topmost, out IntPtr target);

    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
    private static IntPtr _dq;

    private readonly IntPtr _island;
    private IntPtr _hwnd;
    private Compositor? _comp;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private SpriteVisual? _pill;
    private CompositionRoundedRectangleGeometry? _geo;
    private bool _shown, _solid, _dark;
    private (float x, float y, float w, float h, float r, float o, float sx, float sy) _last = (-1, -1, -1, -1, -1, -1, -1, -1);
    public bool Ok { get; private set; }

    public SystemGlass(IntPtr island) => _island = island;

    /// <summary>建底板窗口和合成视觉；失败（老系统、合成不可用）返回 false，调用方退回截屏模糊。</summary>
    public bool Create()
    {
        if (Ok) return true;
        try
        {
            if (_dq == IntPtr.Zero) Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new DQOptions { dwSize = 12, threadType = 2 /*当前线程*/, apartmentType = 0 }, out _dq));
            _comp = new Compositor();
            // NOREDIRECTIONBITMAP | TOOLWINDOW | NOACTIVATE | TOPMOST | TRANSPARENT；STATIC 类对命中测试回 HTTRANSPARENT
            _hwnd = CreateWindowExW(0x00200000 | 0x80 | 0x08000000 | 0x8 | 0x20, "Static", "", 0x80000000 /*WS_POPUP*/, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return false;
            var on = 1;
            DwmSetWindowAttribute(_hwnd, 17 /*DWMWA_USE_HOSTBACKDROPBRUSH*/, ref on, 4);
            var noRound = 1; DwmSetWindowAttribute(_hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref noRound, 4);
            var unk = ((WinRT.IWinRTObject)_comp).NativeObject.ThisPtr;
            var iid = new Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"); // ICompositorDesktopInterop
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var interop));
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<CreateTargetFn>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(interop), 3 * IntPtr.Size));
                Marshal.ThrowExceptionForHR(create(interop, _hwnd, 0, out var t));
                _target = WinRT.MarshalInspectable<DesktopWindowTarget>.FromAbi(t);
                Marshal.Release(t);
            }
            finally { Marshal.Release(interop); }
            _root = _comp.CreateContainerVisual();
            _target.Root = _root;
            _pill = _comp.CreateSpriteVisual();
            _geo = _comp.CreateRoundedRectangleGeometry();
            _pill.Clip = _comp.CreateGeometricClip(_geo);
            _root.Children.InsertAtTop(_pill);
            ApplyBrush();
            Ok = true;
            Follow();
            return true;
        }
        catch (Exception ex)
        {
            App.Log("系统模糊不可用，退回截屏模糊: " + ex.Message);
            Dispose();
            return false;
        }
    }

    /// <summary>关了「透明效果」：画实色（和系统亚克力一样的退化），颜色按深浅色。</summary>
    public void SetSolid(bool solid, bool dark)
    {
        if (solid == _solid && dark == _dark && _pill?.Brush != null) return;
        _solid = solid; _dark = dark;
        ApplyBrush();
    }

    private void ApplyBrush()
    {
        if (_comp == null || _pill == null) return;
        _pill.Brush = _solid
            ? _comp.CreateColorBrush(_dark ? Windows.UI.Color.FromArgb(255, 0x2A, 0x2A, 0x2E) : Windows.UI.Color.FromArgb(255, 0xEE, 0xEF, 0xF3))
            : _comp.CreateHostBackdropBrush();
    }

    /// <summary>
    /// 每帧（ApplyFrame）调：胶囊在岛窗口里的位置大小（物理像素）、整体透明度、藏起时的平移。
    /// 和上一帧一样就不碰合成；看不见时把底板窗口藏掉，DWM 不再为它合成模糊。
    /// </summary>
    public void Update(double x, double y, double w, double h, double r, double opacity, double shiftX, double shiftY)
    {
        if (!Ok || _geo == null || _pill == null || _root == null) return;
        var visible = opacity > 0.01 && w > 1 && h > 1;
        Show(visible);
        if (!visible) return;
        var k = ((float)x, (float)y, (float)w, (float)h, (float)r, (float)Math.Round(opacity, 3), (float)shiftX, (float)shiftY);
        if (k == _last) return;
        _last = k;
        _geo.Offset = new Vector2(k.Item1, k.Item2);
        _geo.Size = new Vector2(k.Item3, k.Item4);
        _geo.CornerRadius = new Vector2(k.Item5, k.Item5);
        _pill.Size = new Vector2(k.Item1 + k.Item3 + 2, k.Item2 + k.Item4 + 2);
        _root.Opacity = k.Item6;
        _root.Offset = new Vector3(k.Item7, k.Item8, 0);
    }

    public void Show(bool on)
    {
        if (!Ok || on == _shown) return;
        _shown = on;
        if (on) { Follow(); ShowWindow(_hwnd, 4 /*SW_SHOWNOACTIVATE*/); Below(); }
        else ShowWindow(_hwnd, 0);
    }

    /// <summary>和岛窗口对齐（位置、大小）并压在它正下方。</summary>
    public void Follow()
    {
        if (!Ok || !GetWindowRect(_island, out var r)) return;
        SetWindowPos(_hwnd, _island, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>z 序：紧贴岛下面（岛被顶到最上层、置顶被别人摘掉补回之后都要再压一次）。</summary>
    public void Below()
    {
        if (!Ok || !_shown) return;
        if (GetWindow(_island, 2 /*GW_HWNDNEXT*/) == _hwnd) return;
        SetWindowPos(_hwnd, _island, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>窗口形状和岛一致（调用方传进来的是一份副本，系统接管）。底板本来就点不到，这只是让 DWM 少合成一块。</summary>
    public void SetRegion(IntPtr rgn)
    {
        if (!Ok) { Native.DeleteObject(rgn); return; }
        SetWindowRgn(_hwnd, rgn, false);
    }

    public void Dispose()
    {
        Ok = false;
        try { if (_target != null) { _target.Root = null; _target.Dispose(); } } catch { }
        _target = null; _pill = null; _geo = null; _root = null;
        try { _comp?.Dispose(); } catch { }
        _comp = null;
        if (_hwnd != IntPtr.Zero) { DestroyWindow(_hwnd); _hwnd = IntPtr.Zero; }
        _shown = false;
    }
}
