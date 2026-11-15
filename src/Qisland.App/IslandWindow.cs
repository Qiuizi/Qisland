using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 灵动岛窗口。窗口本身固定尺寸、永不移动，所有形变只发生在里面的胶囊上：
/// 用 GPU 合成（DWM 玻璃 + 透明背景），不走 AllowsTransparency 的软件分层窗口。
/// </summary>
public sealed partial class IslandWindow : Window
{
    private enum Mode { Idle, Compact, Expanded }

    private const double WinW = 680, WinH = 600, TopGap = 10;
    private const double IdleW = 176, IdleH = 34, CompactH = 38, ExpandedW = 452, RowH = 56;
    private const int MaxRows = 4;

    private readonly ActivityStore _store;
    private readonly bool _layered;

    private readonly Canvas _root = new() { Width = WinW, Height = WinH };
    // 胶囊、阴影都是 Path + RectangleGeometry：每帧只改几何参数，只触发重绘，不触发布局
    private readonly RectangleGeometry _pillGeo = new();
    private readonly RectangleGeometry _rimGeo = new();
    private readonly Path _pill = new() { Fill = Theme.Pill };
    private readonly Path _rim = new() { StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Path _sheen = new() { IsHitTestVisible = false };
    private readonly List<Path> _shadowPaths = [];
    private readonly Canvas _content = new() { Width = WinW, Height = WinH };
    private readonly ScaleTransform _bubbleScale = new(1, 1);
    private readonly TranslateTransform _bubbleShift = new();
    private bool _expDirty = true;
    private int _frames, _slowFrames;
    private double _maxDt;
    private readonly RectangleGeometry _clip = new();
    private readonly Grid _idle = new(), _compact = new(), _expanded = new();
    private readonly Border _bubble = new() { Background = Theme.Pill, Width = CompactH, Height = CompactH, CornerRadius = new CornerRadius(CompactH / 2) };
    private readonly TranslateTransform _rootShift = new();
    private static readonly (double k, byte a)[] ShadowLayers = [(14, 0x07), (9, 0x0C), (5, 0x14), (2, 0x22)];
    private readonly List<RectangleGeometry> _shadows = [];

    private readonly Spring _w = new(IdleW), _h = new(IdleH), _r = new(IdleH / 2);
    private readonly Spring _idleOp = new(1) { Response = 0.30, DampingRatio = 1, Epsilon = 0.004 };
    private readonly Spring _compactOp = new(0) { Response = 0.30, DampingRatio = 1, Epsilon = 0.004 };
    private readonly Spring _expOp = new(0) { Response = 0.34, DampingRatio = 1, Epsilon = 0.004 };
    private readonly Spring _bubbleOp = new(0) { Response = 0.36, DampingRatio = 0.9, Epsilon = 0.004 };
    private readonly Spring _show = new(1) { Response = 0.40, DampingRatio = 1, Epsilon = 0.004 };
    private Spring[] Springs => [_w, _h, _r, _idleOp, _compactOp, _expOp, _bubbleOp, _show, _pageX];

    private Mode _mode = Mode.Idle;
    private bool _animating, _clickThrough = true, _pinned, _hidden;
    private TimeSpan _lastRender;
    private DateTime _hoverSince = DateTime.MaxValue, _outsideSince = DateTime.MinValue;
    private DateTime _peekUntil = DateTime.MinValue;
    private string? _lastTopId;
    private DateTime _lastTopUpdated;
    private IntPtr _hwnd;
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _clockText = "";
    private WeatherSnapshot? _weather;
    private WeatherDetail? _wx;
    public MediaWatcher? Media;
    private Border? _mediaFill;
    private TextBlock? _mediaTime;
    private bool MusicPlaying => Media?.Current is { Playing: true };

    private readonly TextBlock _idleClock = Txt(13.5, FontWeights.SemiBold, Theme.Primary);
    private readonly TextBlock _idleWeatherGlyph = WxGlyph("", 13, Theme.Secondary);
    private readonly TextBlock _idleTemp = Txt(13, FontWeights.Medium, Theme.Secondary);
    private double _compactW = 260, _expandedH = 140;

    public IslandWindow(ActivityStore store)
    {
        _store = store;
        _layered = Environment.GetEnvironmentVariable("QISLAND_LAYERED") == "1";
        Title = "Qisland";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Width = WinW;
        Height = WinH;
        AllowsTransparency = _layered;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        BuildVisuals();
        Content = _root;

        _store.Changed += OnStoreChanged;
        _poll.Tick += (_, _) => Poll();
        _tick.Tick += (_, _) => OnSecond();
        SourceInitialized += (_, _) => SetupHwnd();
        SetupPages();
        Loaded += (_, _) => { App.Log($"render tier {RenderCapability.Tier >> 16}, layered={_layered}"); PlaceOnScreen(); OnSecond(); RebuildIdle(); ApplyFrame(); _poll.Start(); _tick.Start(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(PlaceOnScreen);
    }

    // ───────────────────────────── 外壳 ─────────────────────────────

    private void BuildVisuals()
    {
        _root.RenderTransform = _rootShift;
        // 阴影不用 DropShadowEffect（每帧都要重新模糊整块像素），用几层半透明圆角矩形叠出柔和的投影
        foreach (var (k, a) in ShadowLayers)
        {
            var g = new RectangleGeometry();
            _shadows.Add(g);
            var sp = new Path { Data = g, Fill = Theme.Freeze(new SolidColorBrush(Color.FromArgb(a, 0, 0, 0))), IsHitTestVisible = false };
            _shadowPaths.Add(sp);
            _root.Children.Add(sp);
        }
        _glass = new GlassBackdrop(this, GlassArea, WinW, WinH) { ShiftY = () => _rootShift.Y };
        _glass.Host.Clip = _pillGeo;
        _root.Children.Add(_glass.Host);
        _pill.Data = _pillGeo;
        _root.Children.Add(_pill);
        _sheen.Data = _pillGeo;
        _root.Children.Add(_sheen);

        _content.Clip = _clip;
        foreach (var layer in new[] { _idle, _compact, _expanded })
        {
            Canvas.SetTop(layer, TopGap);
            layer.RenderTransformOrigin = new Point(0.5, 0);
            layer.RenderTransform = new ScaleTransform(1, 1);
            _content.Children.Add(layer);
        }
        _root.Children.Add(_content);

        _bubble.Opacity = 0;
        _bubble.RenderTransformOrigin = new Point(0, 0.5);
        _bubble.RenderTransform = new TransformGroup { Children = { _bubbleScale, _bubbleShift } };
        Canvas.SetTop(_bubble, TopGap);
        _root.Children.Add(_bubble);
        _rim.Data = _rimGeo;
        _root.Children.Add(_rim);

        // 空闲：左时钟，右天气
        _idle.Height = IdleH;
        _idle.Width = IdleW;
        Place(_idle, IdleW);
        _idle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _idle.ColumnDefinitions.Add(new ColumnDefinition());
        _idle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _idleClock.Margin = new Thickness(16, 0, 0, 0);
        _idleClock.VerticalAlignment = VerticalAlignment.Center;
        _idle.Children.Add(_idleClock);
        var wx = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 15, 0), VerticalAlignment = VerticalAlignment.Center };
        _idleWeatherGlyph.Margin = new Thickness(0, 1, 6, 0);
        wx.Children.Add(_idleWeatherGlyph);
        wx.Children.Add(_idleTemp);
        Grid.SetColumn(wx, 2);
        _idle.Children.Add(wx);

        _pill.MouseLeftButtonUp += (_, e) => { _pinned = !_pinned; SetMode(_pinned ? Mode.Expanded : DesiredRestMode()); e.Handled = true; };
        _bubble.MouseLeftButtonUp += (_, e) => { _pinned = true; SetMode(Mode.Expanded); e.Handled = true; };
        _content.IsHitTestVisible = false;
    }

    private void SetupHwnd()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT, true);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        BrightnessWatcher.Register(_hwnd);
        SetupScreen();
        SetupClipboard();
        if (_layered) return;

        var src = HwndSource.FromHwnd(_hwnd);
        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref m);
        var corner = 1; // DWMWCP_DONOTROUND
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        var backdrop = 1; // DWMSBT_NONE
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        SetGlass(Theme.IsGlass);
    }


    // ───────────────────────────── 状态 ─────────────────────────────

    private Mode DesiredRestMode() => OsdActive ? Mode.Compact : _store.Ranked().Count > 0 || MusicPlaying ? Mode.Compact : Mode.Idle;

    public void OnMediaChanged()
    {
        RebuildCompact();
        if (_mode == Mode.Expanded) { if (!_animating) RebuildExpanded(); else _expDirty = true; SetMode(Mode.Expanded); }
        else { _expDirty = true; SetMode(DesiredRestMode()); }
    }

    private void SetMode(Mode m)
    {
        var opening = m == Mode.Expanded && _mode != Mode.Expanded;
        if (m != Mode.Expanded && _mode == Mode.Expanded) OnCollapsed();
        if (m == Mode.Expanded && _expDirty) RebuildExpanded();
        _mode = m;
        if (opening) StaggerIn();
        var top = _store.Ranked();
        (_w.Target, _h.Target, _r.Target) = m switch
        {
            Mode.Expanded => (ExpandedW, _expandedH, 30d),
            Mode.Compact => (_compactW, CompactH, CompactH / 2),
            _ => (IdleW, IdleH, IdleH / 2),
        };
        // 展开时更有弹性，收起时更干脆
        var resp = m == Mode.Expanded ? 0.42 : 0.36;
        var damp = m == Mode.Expanded ? 0.86 : 0.96;
        foreach (var s in new[] { _w, _h, _r }) { s.Response = resp; s.DampingRatio = damp; }
        _idleOp.Target = m == Mode.Idle ? 1 : 0;
        _compactOp.Target = m == Mode.Compact ? 1 : 0;
        _expOp.Target = m == Mode.Expanded ? 1 : 0;
        _bubbleOp.Target = m == Mode.Compact && top.Count > 1 ? 1 : 0;
        _idle.IsHitTestVisible = false;
        _compact.IsHitTestVisible = false;
        _expanded.IsHitTestVisible = m == Mode.Expanded;
        _content.IsHitTestVisible = m == Mode.Expanded;
        Kick();
    }

    private void OnStoreChanged()
    {
        var ranked = _store.Ranked();
        var top = ranked.Count > 0 ? ranked[0] : null;
        // 新的头条活动：像 iOS 一样先展开“偷看”几秒
        var fresh = top != null && (top.Id != _lastTopId || top.Updated > _lastTopUpdated)
                    && (DateTime.Now - top.Updated).TotalSeconds < 2
                    && (top.Id != _lastTopId || top.Kind is ActivityKind.Done or ActivityKind.Error or ActivityKind.WaitingApproval);
        _lastTopId = top?.Id;
        _lastTopUpdated = top?.Updated ?? DateTime.MinValue;
        RebuildCompact();
        RebuildBubble(ranked);
        RebuildExpanded(); // 数据变化时就预先建好展开视图，悬停展开那一帧不再临时造控件
        if (_mode == Mode.Expanded) SetMode(Mode.Expanded);
        else if (!(fresh && Attend(top!))) SetMode(DesiredRestMode());
    }

    private void OnSecond()
    {
        var now = DateTime.Now;
        var text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (text != _clockText) { _clockText = text; _idleClock.Text = text; _expDirty = true; }
        if (_expDirty && !_animating) RebuildExpanded();
        UpdateMediaProgress();
        OnPageSecond();
        CheckFocusDigest();
        if (now.Second % 5 == 0) _store.Expire();
        if (_peekUntil != DateTime.MinValue && now > _peekUntil && !_pinned && _hoverSince == DateTime.MaxValue)
        {
            _peekUntil = DateTime.MinValue;
            SetMode(DesiredRestMode());
        }
        UpdateVisibility();
    }

    /// <summary>点一行：返回 true 表示已经打开了对应软件。</summary>
    public Func<LiveActivity, bool>? OpenRequested;
    private IReadOnlyList<string> _todos = Array.Empty<string>();

    public void SetTodos(IReadOnlyList<string> todos)
    {
        _todos = todos;
        _expDirty = true;
        if (_mode == Mode.Expanded && (!_animating || _page == Page.Todo)) RebuildExpanded();
    }

    public void SetWeather(WeatherSnapshot w, WeatherDetail? detail)
    {
        _weather = w;
        if (detail != null) _wx = detail;
        RebuildIdle();
        _expDirty = true;
    }

    // ───────────────────────────── 鼠标 ─────────────────────────────

    private void Poll()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = (p.X / dpi.DpiScaleX) - Left;
        var y = (p.Y / dpi.DpiScaleY) - Top;
        var pillLeft = (WinW - _w.Value) / 2;
        const double slop = 6;
        var inPill = x >= pillLeft - slop && x <= pillLeft + _w.Value + slop && y >= 0 && y <= TopGap + _h.Value + slop;
        var bx = pillLeft + _w.Value + 8;
        var inBubble = _bubble.Opacity > 0.5 && x >= bx && x <= bx + CompactH && y >= TopGap && y <= TopGap + CompactH;
        var inside = !_hidden && (inPill || inBubble);

        if (inside == _clickThrough)
        {
            _clickThrough = !inside;
            Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, _clickThrough);
        }

        var now = DateTime.Now;
        if (inside)
        {
            if (_hoverSince == DateTime.MaxValue) _hoverSince = now;
            _outsideSince = DateTime.MinValue;
            if (_mode != Mode.Expanded && (now - _hoverSince).TotalMilliseconds > 90) SetMode(Mode.Expanded);
        }
        else
        {
            _hoverSince = DateTime.MaxValue;
            if (_outsideSince == DateTime.MinValue) _outsideSince = now;
            if (_mode == Mode.Expanded && !_pinned && !_swiping && !_editing && now > _peekUntil && (now - _outsideSince).TotalMilliseconds > 420)
                SetMode(DesiredRestMode());
        }

        // 鼠标离岛很远时降低轮询频率，空转几乎不耗 CPU
        var far = y > WinH + 160 || x < -300 || x > WinW + 300;
        var want = far && !_animating ? 50 : 16;
        if ((int)_poll.Interval.TotalMilliseconds != want) _poll.Interval = TimeSpan.FromMilliseconds(want);
    }

    // ───────────────────────────── 动画 ─────────────────────────────

    private TimeSpan _animStart;

    // 弹簧是在 CompositionTarget.Rendering 里手动推的，WPF 只有在有动画要跑时才会按高帧率出帧。
    // 动画期间挂一个看不见的「节拍器」动画，DesiredFrameRate = 屏幕刷新率，让渲染线程跟上 120 Hz。
    private readonly System.Windows.Shapes.Rectangle _pacer = new() { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
    private void StartPacer()
    {
        if (_pacer.Parent == null) _root.Children.Add(_pacer);
        var a = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(a, App.Hz);
        _pacer.BeginAnimation(WidthProperty, a);
    }
    private void StopPacer() => _pacer.BeginAnimation(WidthProperty, null);

    private void Kick()
    {
        if (_animating) return;
        _animating = true;
        _lastRender = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        StartPacer();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _lastRender) return;
        var dt = _lastRender == TimeSpan.Zero ? 1.0 / 60 : (t - _lastRender).TotalSeconds;
        if (_frames == 0) _animStart = t;
        _lastRender = t;
        _frames++; _maxDt = Math.Max(_maxDt, dt); if (dt > Math.Max(1.6 / App.Hz, 0.0125)) _slowFrames++;
        var settled = true;
        foreach (var s in Springs) { s.Step(dt); settled &= s.IsSettled; }
        ApplyFrame();
        if (settled)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            StopPacer();
            ApplyFrame(); // 停稳后把窗口区域收回到最终大小
            App.Log($"anim → {_mode}: {_frames} 帧 / {(_lastRender - _animStart).TotalSeconds:0.00}s, 掉帧 {_slowFrames}, 最长 {_maxDt * 1000:0} ms");
            _frames = 0; _slowFrames = 0; _maxDt = 0;
            if (_mode != Mode.Expanded) App.TrimSoon();
        }
    }

    private void ApplyFrame()
    {
        ApplyPageOffset();
        var w = Math.Max(20, _w.Value);
        var h = Math.Max(20, _h.Value);
        var r = Math.Clamp(_r.Value, 4, h / 2);
        var left = (WinW - w) / 2;

        _pillGeo.Rect = new Rect(left, TopGap, w, h);
        _pillGeo.RadiusX = _pillGeo.RadiusY = r;
        for (var i = 0; i < _shadows.Count; i++)
        {
            var k = ShadowLayers[i].k;
            var g = _shadows[i];
            g.Rect = new Rect(left - k, TopGap - k + 5, w + 2 * k, h + 2 * k);
            g.RadiusX = g.RadiusY = r + k;
        }
        _clip.Rect = new Rect(left, TopGap, w, h);
        _clip.RadiusX = _clip.RadiusY = r;
        _rimGeo.Rect = new Rect(left + 0.5, TopGap + 0.5, w - 1, h - 1);
        _rimGeo.RadiusX = _rimGeo.RadiusY = Math.Max(0, r - 0.5);

        Fade(_idle, _idleOp.Value);
        Fade(_compact, _compactOp.Value);
        Fade(_expanded, _expOp.Value);

        var bo = Math.Clamp(_bubbleOp.Value, 0, 1);
        _bubble.Opacity = bo;
        var bScale = 0.6 + 0.4 * Math.Clamp(_bubbleOp.Value, 0, 1.2);
        _bubbleScale.ScaleX = _bubbleScale.ScaleY = bScale;
        _bubbleShift.X = left + w + 8 - (1 - _bubbleOp.Value) * 22;

        var show = Math.Clamp(_show.Value, 0, 1);
        _root.Opacity = show;
        _rootShift.Y = (1 - show) * -(TopGap + h);
        UpdateHitRegion(left, w, h, r, bo, show);
    }

    // ───────────────────────────── 外观 ─────────────────────────────

    internal void ApplyLook(Theme.Look look)
    {
        Theme.Apply(look);
        _pill.Fill = Theme.Pill;
        _bubble.Background = Theme.Pill;
        _rim.Stroke = Theme.Rim;
        _rim.Visibility = Theme.Rim == null ? Visibility.Collapsed : Visibility.Visible;
        _sheen.Fill = Theme.Sheen;
        _sheen.Visibility = Theme.Sheen == null ? Visibility.Collapsed : Visibility.Visible;
        for (var i = 0; i < _shadowPaths.Count; i++)
        {
            var a = (byte)(ShadowLayers[i].a * Theme.ShadowAlpha / 0x22);
            _shadowPaths[i].Fill = Theme.Freeze(new SolidColorBrush(Color.FromArgb(a, 0, 0, 0)));
            _shadowPaths[i].Visibility = Visibility.Visible;
        }
        _idleClock.Foreground = Theme.Primary;
        _idleTemp.Foreground = Theme.Secondary;
        _idleWeatherGlyph.Foreground = Theme.Secondary;
        if (_hwnd != IntPtr.Zero) SetGlass(Theme.IsGlass);
        RebuildIdle();
        RebuildCompact();
        RebuildBubble(_store.Ranked());
        RebuildExpanded();
        ApplyFrame();
    }

    
    // 液态玻璃：系统亚克力模糊铺满窗口，再用窗口区域把它裁成胶囊的形状，跟着弹簧每帧更新
    // 系统模糊（亚克力/背景模糊）在这台 Win11 24H2 上不吃窗口区域裁剪：要么糊满整块窗口，要么关掉透明扩展后露出黑块。
    // 所以玻璃外观不用系统模糊，只用半透明白 + 高光描边 + 顶部反光，窗口始终保持透明扩展。
    private GlassBackdrop _glass = null!;
    private void SetGlass(bool on) { if (!_layered && !SnapshotMode) _glass.Enable(_hwnd, on); }

    /// <summary>要截的那块：当前尺寸和弹簧目标尺寸里更大的那个，再外扩一点，动画途中边缘也有底。</summary>
    private Rect? GlassArea()
    {
        if (_show.Value < 0.02 && _show.Target < 0.5) return null;
        var w = Math.Max(_w.Value, _w.Target) + 16;
        var h = Math.Max(_h.Value, _h.Target) + 12;
        return new Rect((WinW - w) / 2, TopGap - 4, w, h);
    }

    private double _stickW, _stickH, _stickR = 999;
    private (int, int, int, int, int, int) _lastRgn = (-1, -1, -1, -1, -1, -1);

    // 窗口形状 = 岛 + 阴影的范围。WS_EX_TRANSPARENT 对非分层窗口不会把点击让给别的程序，
    // 所以用窗口区域把 680×600 的窗口裁到岛本身：区域外的点击、悬停直接落到下面的窗口。
    //
    // 关键：每改一次窗口区域，DWM 都要重新合成整块窗口，逐帧改会掉帧、闪烁。
    // 所以动画期间直接取「当前尺寸」和「目标尺寸」里更大的那个，并按 24 px 取整：
    // 一次变形只在开始时扩一次、结束后收一次，中间几十帧都不碰区域。
    private void UpdateHitRegion(double left, double w, double h, double r, double bubbleOp, double show)
    {
        if (_hwnd == IntPtr.Zero) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        int P(double v) => (int)Math.Round(v * dpi.DpiScaleX);
        static double Up(double v) => Math.Ceiling(v / 24) * 24;
        const double side = 16, below = 20;
        var visible = show >= 0.02 || _show.Target > 0.5;
        var y0 = TopGap + (_show.Target > 0.5 ? 0 : _rootShift.Y);
        var bw = Up(Math.Max(w, _w.Target));
        var bh = Up(Math.Max(h, _h.Target));
        var bl = (WinW - bw) / 2;
        var rr = Math.Min(Math.Min(r, _r.Target), bh / 2);
        if (_animating)
        {
            // 动画中区域只扩不缩（半径只减不增），收的那一下留到弹簧停稳之后
            bw = _stickW = Math.Max(bw, _stickW);
            bh = _stickH = Math.Max(bh, _stickH);
            rr = _stickR = Math.Min(rr, _stickR);
            bl = (WinW - bw) / 2;
        }
        else { _stickW = 0; _stickH = 0; _stickR = 999; }
        var bubble = bubbleOp > 0.05 || _bubbleOp.Target > 0.05;
        var bubbleX = bl + bw + 8;
        var key = !visible ? (0, 0, 0, 0, 0, 0)
            : (P(bl - side), P(Math.Max(0, y0 - side)), P(bl + bw + side), P(y0 + bh + below), P((rr + side) * 2), bubble ? P(bubbleX) : -1);
        if (key == _lastRgn) return;
        _lastRgn = key;
        IntPtr rgn;
        if (key.Item3 == 0) rgn = Native.CreateRoundRectRgn(0, 0, 0, 0, 0, 0);
        else
        {
            rgn = Native.CreateRoundRectRgn(key.Item1, key.Item2, key.Item3 + 1, key.Item4 + 1, key.Item5, key.Item5);
            if (key.Item6 >= 0)
            {
                // 气泡贴着岛右侧滑进来：从岛中线到气泡最终位置一次性包住，滑动途中不再改区域
                var b = Native.CreateRectRgn(P(WinW / 2), key.Item2, key.Item6 + P(CompactH + 8) + 1, P(y0 + CompactH + below) + 1);
                Native.CombineRgn(rgn, rgn, b, 2);
                Native.DeleteObject(b);
            }
        }
        Native.SetWindowRgn(_hwnd, rgn, false); // 系统接管 rgn，不用 DeleteObject
    }

    private static void Place(FrameworkElement layer, double width) => Canvas.SetLeft(layer, Math.Round((WinW - width) / 2));

    private static void Fade(FrameworkElement layer, double v)
    {
        var o = Math.Clamp(v, 0, 1);
        layer.Opacity = o;
        var vis = o < 0.01 ? Visibility.Hidden : Visibility.Visible;
        if (layer.Visibility != vis) layer.Visibility = vis;
        var st = (ScaleTransform)layer.RenderTransform;
        st.ScaleX = st.ScaleY = 0.94 + 0.06 * o;
    }

    // ───────────────────────────── 内容 ─────────────────────────────

    private void RebuildIdle()
    {
        _idleClock.Text = _clockText;
        if (_weather is { } w)
        {
            _idleWeatherGlyph.Text = Theme.WeatherGlyph(w.Code, Theme.IsNight(DateTime.Now));
            _idleTemp.Text = $"{w.Temperature:0}°";
        }
        else { _idleWeatherGlyph.Text = ""; _idleTemp.Text = DateTime.Now.ToString("ddd", new CultureInfo("zh-CN")); }
    }

    private void RebuildCompact()
    {
        _compactLyric = null;
        _compact.Children.Clear();
        _compact.ColumnDefinitions.Clear();
        if (OsdActive) { BuildOsd(); return; }
        var ranked = _store.Ranked();
        if (ranked.Count == 0) { if (MusicPlaying) CompactMusic(Media!.Current!); return; }
        var a = ranked[0];

        _compact.Height = CompactH;
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = Tile(a.Source, 24, 7);
        tile.Margin = new Thickness(7, 0, 0, 0);
        _compact.Children.Add(tile);

        var src = Theme.SourceName(a.Source);
        var title = new TextBlock
        {
            FontFamily = Theme.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 1),
        };
        title.Inlines.Add(new Run(src) { Foreground = Theme.Secondary, FontWeight = FontWeights.Medium });
        title.Inlines.Add(new Run("  " + a.Title) { Foreground = Theme.Primary, FontWeight = FontWeights.SemiBold });
        Grid.SetColumn(title, 1);
        _compact.Children.Add(title);

        var status = StatusView(a, compact: true);
        status.Margin = new Thickness(0, 0, 13, 0);
        status.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(status, 2);
        _compact.Children.Add(status);

        status.Measure(new Size(double.PositiveInfinity, CompactH));
        var textW = MeasureText(src, 13, FontWeights.Medium) + MeasureText("  " + a.Title, 13, FontWeights.SemiBold);
        _compactW = Math.Clamp(7 + 24 + 10 + Math.Min(textW, 270) + 10 + status.DesiredSize.Width + 13, 230, 420);
        _compact.Width = _compactW;
        Place(_compact, _compactW);
        if (_mode == Mode.Compact) _w.Target = _compactW;
    }

    private void RebuildBubble(IReadOnlyList<LiveActivity> ranked)
    {
        if (ranked.Count < 2) { _bubble.Child = null; return; }
        if (ranked.Count == 2)
        {
            var t = Tile(ranked[1].Source, 24, 7);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            _bubble.Child = t;
        }
        else
        {
            var tb = Txt(12.5, FontWeights.SemiBold, Theme.Primary);
            tb.Text = $"+{ranked.Count - 1}";
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
            _bubble.Child = tb;
        }
    }

    private void RebuildExpanded()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _expanded.Children.Clear();
        _expanded.Width = ExpandedW;
        Place(_expanded, ExpandedW);
        _expDirty = false;
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        _mediaFill = null; _mediaTime = null;
        BuildPage(stack);
        _pageCache.Clear(); // 数据变了：其他页的缓存一律作废
        _expStack = stack;
        ApplyPageOffset();
        _expanded.Children.Add(stack);
        stack.Measure(new Size(ExpandedW, double.PositiveInfinity));
        _expandedH = Math.Ceiling(stack.DesiredSize.Height + 14 + 10);
        _expanded.Height = _expandedH;
        if (_mode == Mode.Expanded) _h.Target = _expandedH;
        _pageCache[_page] = (stack, _expandedH, _mediaFill, _mediaTime);
        if (sw.Elapsed.TotalMilliseconds > 6) App.Log($"rebuild {_page}: {sw.Elapsed.TotalMilliseconds:0.0} ms");
    }

    // 翻页时优先复用已经建好的页面（组件页每秒刷新，始终现建）
    private readonly Dictionary<Page, (StackPanel stack, double h, Border? fill, TextBlock? time)> _pageCache = [];
    private void ShowPage()
    {
        if (_expDirty || !_pageCache.TryGetValue(_page, out var c)) { RebuildExpanded(); return; }
        (c.stack.Parent as Panel)?.Children.Remove(c.stack);
        _expanded.Children.Clear();
        _mediaFill = c.fill; _mediaTime = c.time; _gaugeHost = null; _timerHost = null;
        _expStack = c.stack;
        ApplyPageOffset();
        _expanded.Children.Add(c.stack);
        _expandedH = c.h;
        _expanded.Height = c.h;
        if (_mode == Mode.Expanded) _h.Target = c.h;
        UpdateMediaProgress();
        if (_page == Page.Todo && _todoBox != null && _editing) Dispatcher.BeginInvoke(() => _todoBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void BuildHome(StackPanel stack)
    {
        var tc = TodayClasses();
        var ranked = _store.Ranked();
        // 有「今天的课」卡片时，上课中/下一节这两条就不再重复列成行
        if (tc != null) ranked = ranked.Where(a => a.Id is not ("class:now" or "class:next")).ToList();
        var hw = HomeworkCard();
        if (hw != null) ranked = ranked.Where(a => !a.Id.StartsWith("hw:")).ToList();
        var music = Media?.Current;

        var cd = CountdownStrip();
        if (FocusDoneCard() is { } fd) stack.Children.Add(fd);
        if (ranked.Count == 0)
        {
            stack.Children.Add(HeroClock());
            if (cd != null) stack.Children.Add(cd);
            if (tc != null) stack.Children.Add(tc);
            if (hw != null) stack.Children.Add(hw);
            if (RainHint() is { } rh) stack.Children.Add(rh);
            if (music != null) stack.Children.Add(Glassy(MusicWithLyrics(music)));
            if (_todos.Count > 0) stack.Children.Add(Glassy(TodoBlock()));
        }
        else
        {
            if (music != null) stack.Children.Add(Glassy(MusicWithLyrics(music)));
            var rows = new StackPanel();
            foreach (var a in ranked.Take(MaxRows)) rows.Children.Add(Row(a));
            if (ranked.Count > MaxRows)
            {
                var more = Txt(12, FontWeights.Medium, Theme.Tertiary);
                more.Text = $"还有 {ranked.Count - MaxRows} 条";
                more.Margin = new Thickness(46, 0, 0, 2);
                rows.Children.Add(more);
            }
            if (rows.Children.Count > 0) stack.Children.Add(Glassy(rows));
            if (tc != null) stack.Children.Add(tc);
            if (hw != null) stack.Children.Add(hw);
            if (_todos.Count > 0) stack.Children.Add(Glassy(TodoBlock()));
            if (cd != null) { cd.Margin = new Thickness(0, 8, 0, 2); stack.Children.Add(cd); }
            stack.Children.Add(Glassy(Footer(), footer: true));
        }
    }

    // 玻璃主题下把一组内容装进半透明卡片；其它主题原样返回，保持原来的无卡片布局
    private static FrameworkElement Glassy(FrameworkElement child, bool footer = false)
    {
        if (Theme.CardRim == null) return child;
        child.Margin = new Thickness(0);
        return new Border
        {
            Background = Theme.Card, BorderBrush = Theme.CardRim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20),
            Padding = footer ? new Thickness(14, 0, 14, 0) : new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 4, 0, 4), Child = child,
        };
    }

    // 首页天气只留一句有用的话：接下来 12 小时第一次可能下雨的时间
    private string? RainText()
    {
        if (_wx is not { } x) return null;
        var h = x.Hours.Take(12).FirstOrDefault(h => h.Rain >= 40);
        if (h == null) return null;
        var when = h.At.Date == DateTime.Today ? h.At.ToString("H时") : "明天" + h.At.ToString("H时");
        return $"{when}起可能有雨";
    }

    private FrameworkElement? RainHint()
    {
        if (RainText() is not { } t) return null;
        var tb = Txt(12, FontWeights.Medium, Theme.Blue);
        tb.Text = "☂ " + t;
        tb.Margin = new Thickness(6, 0, 0, 8);
        return tb;
    }

    private FrameworkElement HeroClock()
    {
        var g = new Grid { Height = 76 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var big = new TextBlock { Text = _clockText, FontFamily = Theme.Display, FontSize = 38, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary };
        var date = Txt(13, FontWeights.Medium, Theme.Secondary);
        date.Text = DateTime.Now.ToString("M月d日 dddd", new CultureInfo("zh-CN"));
        date.Margin = new Thickness(2, -2, 0, 0);
        left.Children.Add(big);
        left.Children.Add(date);
        g.Children.Add(left);
        if (_weather is { } w)
        {
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
            var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var gl = WxGlyph(Theme.WeatherGlyph(w.Code, Theme.IsNight(DateTime.Now)), 22, Theme.Primary);
            gl.Margin = new Thickness(0, 4, 8, 0);
            line.Children.Add(gl);
            line.Children.Add(new TextBlock { Text = $"{w.Temperature:0}°", FontFamily = Theme.Display, FontSize = 30, FontWeight = FontWeights.Medium, Foreground = Theme.Primary });
            right.Children.Add(line);
            var d = Txt(12.5, FontWeights.Medium, Theme.Secondary);
            d.Text = _wx is { } x
                ? $"成都 · {WeatherParser.Describe(w.Code)} · 体感 {w.FeelsLike:0}°"
                : $"成都 · {WeatherParser.Describe(w.Code)}  {w.Low:0}° / {w.High:0}°";
            d.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(d);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }
        return g;
    }

    private FrameworkElement Row(LiveActivity a)
    {
        var g = new Grid { Height = RowH, Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = Tile(a.Source, 34, 10);
        tile.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(tile);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var t = Txt(14, FontWeights.SemiBold, Theme.Primary);
        t.Text = a.Title;
        mid.Children.Add(t);
        var sub = Txt(12.5, FontWeights.Normal, Theme.Secondary);
        sub.Text = string.IsNullOrWhiteSpace(a.Body) ? Theme.SourceName(a.Source) : $"{Theme.SourceName(a.Source)} · {a.Body}";
        sub.Margin = new Thickness(0, 2, 0, 0);
        mid.Children.Add(sub);
        if (a.Progress is { } p)
        {
            var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Fill, Margin = new Thickness(0, 6, 0, 0) };
            var fill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Blue, HorizontalAlignment = HorizontalAlignment.Left };
            track.Child = fill;
            track.SizeChanged += (_, e) => fill.Width = Math.Max(3, e.NewSize.Width * Math.Clamp(p, 0, 1));
            mid.Children.Add(track);
        }
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var st = StatusView(a, compact: false);
        st.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(st);
        var ago = Txt(11, FontWeights.Normal, Theme.Tertiary);
        ago.Text = Theme.Ago(a.Updated);
        ago.HorizontalAlignment = HorizontalAlignment.Right;
        ago.Margin = new Thickness(0, 3, 0, 0);
        right.Children.Add(ago);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);

        var canOpen = Launcher.CanOpen(a.Source) || a.Source is "update" or "system" or "battery" or "device" or "copy" or "download";
        if (canOpen) g.Cursor = Cursors.Hand;
        AddHover(g);
        g.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            App.Log($"点击 {a.Id}");
            if (!canOpen || OpenRequested?.Invoke(a) != true)
            {
                // 没有对应软件（计时、通知等）：点一下当作「已读」，已完成或非常驻的直接收掉
                if (!a.Sticky || a.Kind == ActivityKind.Done) _store.Remove(a.Id);
                return;
            }
            // 消息类点开即已读；Agent 运行中的保留，完成的收掉
            if (!a.Sticky && (a.Kind != ActivityKind.Running && a.Kind != ActivityKind.WaitingApproval || a.Source is "wechat" or "qq"))
                _store.Remove(a.Id);
            if (a.Source is "wechat" or "qq") MessengerFlash.ClearFlash(a.Source, _store);
            _pinned = false;
            _peekUntil = DateTime.MinValue;
            SetMode(DesiredRestMode());
        };
        if (!a.Sticky)
            g.MouseRightButtonUp += (_, e) => { _store.Remove(a.Id); e.Handled = true; };
        return g;
    }

    // 主页上的待办：点圆圈直接完成，点文字或"还有 N 件"跳到待办页
    private FrameworkElement TodoBlock()
    {
        var sp = new StackPanel { Margin = new Thickness(2, 6, 0, 0) };
        var open = Todo?.Items.Where(i => !i.Done).ToList();
        for (var k = 0; k < Math.Min(3, _todos.Count); k++)
        {
            var it = open != null && k < open.Count ? open[k] : null;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var dot = new Border
            {
                Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), BorderThickness = new Thickness(1.4),
                BorderBrush = Theme.Tertiary, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
            };
            line.Children.Add(dot);
            var tx = Txt(13, FontWeights.Normal, Theme.Primary);
            tx.Text = _todos[k];
            tx.VerticalAlignment = VerticalAlignment.Center;
            tx.MaxWidth = ExpandedW - 70;
            line.Children.Add(tx);
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (it == null) return;
                dot.Background = Theme.Blue; dot.BorderBrush = Theme.Blue;
                tx.Foreground = Theme.Tertiary; tx.TextDecorations = TextDecorations.Strikethrough;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
                t.Tick += (_, _) => { t.Stop(); Todo?.SetDone(it, true); };
                t.Start();
            };
            line.MouseLeftButtonUp += (_, e) => { if (!e.Handled) { e.Handled = true; GoTo(Page.Todo, 1); } };
            sp.Children.Add(line);
        }
        if (_todos.Count > 3)
        {
            var more = Txt(11.5, FontWeights.Medium, Theme.Tertiary);
            more.Text = $"还有 {_todos.Count - 3} 件待办";
            more.Margin = new Thickness(23, 0, 0, 0);
            more.Cursor = Cursors.Hand;
            more.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoTo(Page.Todo, 1); };
            sp.Children.Add(more);
        }
        return sp;
    }

    private FrameworkElement Footer()
    {
        var g = new Grid { Height = 40, Margin = new Thickness(0, 2, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
        if (_weather is { } w)
        {
            var gl = WxGlyph(Theme.WeatherGlyph(w.Code, Theme.IsNight(DateTime.Now)), 13, Theme.Secondary);
            gl.Margin = new Thickness(0, 1, 7, 0);
            left.Children.Add(gl);
            var tx = Txt(12.5, FontWeights.Medium, Theme.Secondary);
            tx.Text = $"{w.Temperature:0}°  {WeatherParser.Describe(w.Code)}  {w.Low:0}° / {w.High:0}°";
            left.Children.Add(tx);
            if (RainText() is { } rt)
            {
                var rb = Txt(12.5, FontWeights.Medium, Theme.Blue);
                rb.Text = "   " + rt;
                left.Children.Add(rb);
            }
        }
        g.Children.Add(left);
        var clock = Txt(12.5, FontWeights.Medium, Theme.Secondary);
        clock.Text = DateTime.Now.ToString("M月d日 ddd  HH:mm", new CultureInfo("zh-CN"));
        clock.VerticalAlignment = VerticalAlignment.Center;
        clock.Margin = new Thickness(0, 0, 2, 0);
        Grid.SetColumn(clock, 1);
        g.Children.Add(clock);
        return g;
    }

    // ───────────────────────────── 音乐 ─────────────────────────────

    private static Brush HoverFill => Theme.Hover;
    private static Brush PressFill => Theme.Press;

    // 行悬停给一层很淡的底色（圆角，不画任何分隔线），让「能点」这件事看得见
    private static void AddHover(Grid g)
    {
        var bg = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(-8, 2, -8, 2), Background = Brushes.Transparent, IsHitTestVisible = false };
        Grid.SetColumnSpan(bg, Math.Max(1, g.ColumnDefinitions.Count));
        g.Children.Insert(0, bg);
        g.MouseEnter += (_, _) => bg.Background = HoverFill;
        g.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
        g.PreviewMouseLeftButtonDown += (_, _) => bg.Background = PressFill;
    }

    // 视频用 16:9 缩略图，音乐用方形封面；没有封面时用来源软件的图标
    private static FrameworkElement ArtTile(MediaState m, double size, double radius)
    {
        if (m.Art == null) return Tile(m.AppSource, size, radius);
        var w = m.Video ? Math.Round(size * 16 / 9) : size;
        return new Border
        {
            Width = w, Height = size, CornerRadius = new CornerRadius(radius),
            Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill },
        };
    }

    private void CompactMusic(MediaState m)
    {
        _compact.Height = CompactH;
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _compact.ColumnDefinitions.Add(new ColumnDefinition());
        _compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = ArtTile(m, 24, 6);
        var tileW = m.Art != null && m.Video ? 43 : 24;
        tile.Margin = new Thickness(7, 0, 0, 0);
        tile.VerticalAlignment = VerticalAlignment.Center;
        _compact.Children.Add(tile);
        var title = new TextBlock
        {
            FontFamily = Theme.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 1),
        };
        var lyr = LiveLyrics;
        _compactLyric = null;
        if (lyr != null)
        {
            title.Foreground = Theme.Primary; title.FontWeight = FontWeights.SemiBold;
            title.Text = LyricAt(Lrc.IndexAt(lyr, (Media?.LivePosition() ?? TimeSpan.Zero) + TimeSpan.FromMilliseconds(250)), lyr).cur;
            title.ToolTip = $"{m.Title}  {m.Artist}";
            _compactLyric = title;
        }
        else
        {
            title.Inlines.Add(new Run(m.Title) { Foreground = Theme.Primary, FontWeight = FontWeights.SemiBold });
            if (m.Artist.Length > 0) title.Inlines.Add(new Run("  " + m.Artist) { Foreground = Theme.Secondary, FontWeight = FontWeights.Medium });
        }
        Grid.SetColumn(title, 1);
        _compact.Children.Add(title);
        var bars = Bars(14);
        bars.Margin = new Thickness(0, 0, 14, 0);
        bars.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bars, 2);
        _compact.Children.Add(bars);
        var textW = MeasureText(m.Title, 13, FontWeights.SemiBold) + (m.Artist.Length > 0 ? MeasureText("  " + m.Artist, 13, FontWeights.Medium) : 0);
        _compactW = lyr != null ? LyricCompactW : Math.Clamp(7 + tileW + 10 + Math.Min(textW, 260) + 10 + 14 + 14, 200, 400);
        _compact.Width = _compactW;
        Place(_compact, _compactW);
        if (_mode == Mode.Compact) _w.Target = _compactW;
    }

    // 三根跳动的频谱条：逐格动画、10 fps，和转圈指示器一样几乎不耗渲染
    private static FrameworkElement Bars(double h)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Height = h };
        double[][] seq = [[0.35, 0.9, 0.55, 1, 0.4, 0.75], [0.8, 0.4, 1, 0.5, 0.9, 0.3], [0.5, 1, 0.3, 0.7, 0.45, 0.95]];
        for (var i = 0; i < 3; i++)
        {
            var st = new ScaleTransform(1, seq[i][0]);
            var r = new Rectangle { Width = 3, Height = h, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.Red, Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0), RenderTransform = st, RenderTransformOrigin = new Point(0.5, 1) };
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.2), RepeatBehavior = RepeatBehavior.Forever };
            for (var k = 0; k < 6; k++) a.KeyFrames.Add(new LinearDoubleKeyFrame(seq[i][k], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(k * 0.2))));
            Timeline.SetDesiredFrameRate(a, 12);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
            sp.Children.Add(r);
        }
        return sp;
    }

    private FrameworkElement MusicWithLyrics(MediaState m)
    {
        var card = MusicCard(m);
        if (CardLyrics() is not { } ly) return card;
        var sp = new StackPanel();
        sp.Children.Add(card);
        sp.Children.Add(ly);
        return sp;
    }

    private FrameworkElement MusicCard(MediaState m)
    {
        var g = new Grid { Height = 74, Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var art = ArtTile(m, 48, 11);
        art.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(art);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        var t = Txt(14, FontWeights.SemiBold, Theme.Primary);
        t.Text = m.Title;
        t.ToolTip = m.Title;
        mid.Children.Add(t);
        var subRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var sub = Txt(12.5, FontWeights.Normal, Theme.Secondary);
        sub.Text = m.Artist.Length > 0 ? $"{m.Artist} · {m.App}" : m.App;
        sub.MaxWidth = ExpandedW - 230;
        subRow.Children.Add(sub);
        if (m.Sessions > 1)
        {
            // 同时有多个来源在放：点这个小胶囊在它们之间切换
            var sw = new Border { CornerRadius = new CornerRadius(7), Background = Theme.Fill, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(7, 0, 0, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var swp = new StackPanel { Orientation = Orientation.Horizontal };
            swp.Children.Add(Glyph("\uE8AB", 9, Theme.Secondary));
            var swt = Txt(10.5, FontWeights.Medium, Theme.Secondary); swt.Text = $" {m.Sessions}"; swp.Children.Add(swt);
            sw.Child = swp;
            sw.ToolTip = "切换播放来源";
            sw.MouseEnter += (_, _) => sw.Background = HoverFill;
            sw.MouseLeave += (_, _) => sw.Background = Theme.Fill;
            sw.MouseLeftButtonDown += (_, e) => e.Handled = true;
            sw.MouseLeftButtonUp += (_, e) => { e.Handled = true; Media?.Cycle(); };
            subRow.Children.Add(sw);
        }
        mid.Children.Add(subRow);
        if (m.Duration > TimeSpan.FromSeconds(1))
        {
            var line = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Fill, VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Left, Width = 3 };
            track.Child = fill;
            track.SizeChanged += (_, _) => UpdateMediaProgress();
            // 进度条本身只有 3 px，外面包一层 14 px 高的透明热区，悬停时变粗，点击或拖动跳转
            var hit = new Grid { Height = 14, Background = Brushes.Transparent };
            hit.Children.Add(track);
            if (m.CanSeek)
            {
                hit.Cursor = Cursors.Hand;
                hit.MouseEnter += (_, _) => { track.Height = 5; fill.Height = 5; track.CornerRadius = fill.CornerRadius = new CornerRadius(2.5); };
                hit.MouseLeave += (_, _) => { if (!hit.IsMouseCaptured) { track.Height = 3; fill.Height = 3; track.CornerRadius = fill.CornerRadius = new CornerRadius(1.5); } };
                double Frac(MouseEventArgs e) => track.ActualWidth > 0 ? Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1) : 0;
                hit.MouseLeftButtonDown += (_, e) => { e.Handled = true; hit.CaptureMouse(); _seeking = Frac(e); UpdateMediaProgress(); };
                hit.MouseMove += (_, e) => { if (hit.IsMouseCaptured) { _seeking = Frac(e); UpdateMediaProgress(); } };
                hit.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (!hit.IsMouseCaptured) return;
                    hit.ReleaseMouseCapture();
                    var f = Frac(e);
                    _seeking = null;
                    Media?.Seek(f);
                    UpdateMediaProgress();
                };
            }
            line.Children.Add(hit);
            var time = Txt(10.5, FontWeights.Medium, Theme.Tertiary);
            time.Margin = new Thickness(8, 0, 0, 0);
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 1);
            line.Children.Add(time);
            mid.Children.Add(line);
            _mediaFill = fill; _mediaTime = time;
        }
        Grid.SetColumn(mid, 1);
        g.Children.Add(mid);

        var ctl = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ctl.Children.Add(MediaButton("\uE892", 15, () => Media?.Previous()));
        ctl.Children.Add(MediaButton(m.Playing ? "\uE769" : "\uE768", 19, () => Media?.Toggle()));
        ctl.Children.Add(MediaButton("\uE893", 15, () => Media?.Next()));
        Grid.SetColumn(ctl, 2);
        g.Children.Add(ctl);

        g.Cursor = Cursors.Hand;
        AddHover(g);
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; Launcher.Open(m.AppSource); };
        UpdateMediaProgress();
        return g;
    }

    private double? _seeking;

    private static FrameworkElement MediaButton(string glyph, double size, Action act)
    {
        var b = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var tb = Glyph(glyph, size, Theme.Primary);
        tb.HorizontalAlignment = HorizontalAlignment.Center;
        b.Child = tb;
        b.MouseEnter += (_, _) => b.Background = HoverFill;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => { b.Background = PressFill; e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; b.Background = HoverFill; act(); };
        return b;
    }

    private void UpdateMediaProgress()
    {
        if (_mediaFill == null || Media?.Current is not { } m || m.Duration <= TimeSpan.Zero) return;
        var pos = _seeking is { } sk ? TimeSpan.FromSeconds(m.Duration.TotalSeconds * sk) : Media.LivePosition();
        if (_mediaFill.Parent is FrameworkElement track && track.ActualWidth > 0)
            _mediaFill.Width = Math.Max(3, track.ActualWidth * Math.Clamp(pos.TotalSeconds / m.Duration.TotalSeconds, 0, 1));
        if (_mediaTime != null) _mediaTime.Text = $"{Fmt(pos)} / {Fmt(m.Duration)}";
    }

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // ───────────────────────────── 天气详情 ─────────────────────────────

    private FrameworkElement WeatherStrip()
    {
        var x = _wx!;
        var w = x.Now;
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 10) };

        var facts = Txt(12, FontWeights.Medium, Theme.Secondary);
        facts.Text = $"湿度 {w.Humidity:0}%   {x.WindDir} {w.WindSpeed:0} km/h   降水概率 {x.RainChance}%   紫外线 {x.Uv:0}   日出 {x.Sunrise}  日落 {x.Sunset}";
        facts.Margin = new Thickness(2, 0, 0, 10);
        sp.Children.Add(facts);

        var cells = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1 };
        foreach (var h in x.Hours.Take(5))
            cells.Children.Add(WeatherCell(h.At.Date == DateTime.Today ? h.At.ToString("H时") : "明" + h.At.ToString("H时"), h.Code, $"{h.Temp:0}°", h.Rain >= 30 ? $"{h.Rain}%" : null, Theme.IsNight(h.At)));
        if (x.TomorrowHigh != 0 || x.TomorrowLow != 0)
            cells.Children.Add(WeatherCell("明天", x.TomorrowCode, $"{x.TomorrowLow:0}°/{x.TomorrowHigh:0}°", x.TomorrowRain >= 30 ? $"{x.TomorrowRain}%" : null));
        sp.Children.Add(cells);
        return sp;
    }

    private static FrameworkElement WeatherCell(string label, int code, string temp, string? rain, bool night = false)
    {
        var c = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var l = Txt(11, FontWeights.Medium, Theme.Tertiary);
        l.Text = label;
        l.HorizontalAlignment = HorizontalAlignment.Center;
        c.Children.Add(l);
        var g = WxGlyph(Theme.WeatherGlyph(code, night), 16, Theme.Primary);
        g.HorizontalAlignment = HorizontalAlignment.Center;
        g.Margin = new Thickness(0, 5, 0, 4);
        c.Children.Add(g);
        var t = Txt(12.5, FontWeights.SemiBold, Theme.Primary);
        t.Text = temp;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        c.Children.Add(t);
        var r = Txt(10.5, FontWeights.Medium, Theme.Blue);
        r.Text = rain ?? " ";
        r.HorizontalAlignment = HorizontalAlignment.Center;
        c.Children.Add(r);
        return c;
    }

    // ───────────────────────────── 小部件 ─────────────────────────────

    private static FrameworkElement StatusView(LiveActivity a, bool compact)
    {
        switch (a.Kind)
        {
            case ActivityKind.Running when a.Progress is { } p:
                return Ring(p, compact ? 16 : 18);
            case ActivityKind.Running:
                return Spinner(compact ? 15 : 17);
            case ActivityKind.Done:
                return Glyph("\uE73E", compact ? 13 : 14, Theme.Green);
            case ActivityKind.WaitingApproval:
                return Chip("待确认", Theme.Orange);
            case ActivityKind.Error:
                return Chip("出错", Theme.Red);
            case ActivityKind.Warning:
                return Glyph("\uE7BA", compact ? 13 : 14, Theme.Orange);
            default:
                return compact ? new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = Theme.Blue }
                               : new Border { Width = 0 };
        }
    }

    private static FrameworkElement Chip(string text, Brush color)
    {
        var tb = new TextBlock { Text = text, FontFamily = Theme.Text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = color };
        var c = ((SolidColorBrush)color).Color;
        return new Border
        {
            Child = tb, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(10),
            Background = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B))),
        };
    }

    // 苹果式 8 辐条活动指示器：本来就是逐格跳动的，10 fps 就够，渲染开销只有连续旋转的几分之一
    private static readonly DoubleAnimationUsingKeyFrames SpinAnim = MakeSpin();
    private static DoubleAnimationUsingKeyFrames MakeSpin()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(0.8), RepeatBehavior = RepeatBehavior.Forever };
        for (var i = 0; i < 8; i++) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(i * 45, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(i * 0.1))));
        Timeline.SetDesiredFrameRate(a, 10);
        a.Freeze();
        return a;
    }

    private static FrameworkElement Spinner(double size)
    {
        var c = new Canvas { Width = size, Height = size };
        var sw = Math.Max(1.6, size * 0.12);
        var sh = size * 0.28;
        for (var i = 0; i < 8; i++)
        {
            var spoke = new Rectangle
            {
                Width = sw, Height = sh, RadiusX = sw / 2, RadiusY = sw / 2,
                Fill = Theme.Primary, Opacity = 0.18 + 0.82 * (i / 7.0),
                RenderTransform = new RotateTransform(i * 45, sw / 2, size / 2),
            };
            Canvas.SetLeft(spoke, (size - sw) / 2);
            c.Children.Add(spoke);
        }
        var rot = new RotateTransform(0, size / 2, size / 2);
        c.RenderTransform = rot;
        rot.BeginAnimation(RotateTransform.AngleProperty, SpinAnim);
        return c;
    }

    private static FrameworkElement Ring(double p, double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Ellipse { Stroke = Theme.Fill, StrokeThickness = 2.2 });
        g.Children.Add(ArcPath(size, Math.Clamp(p, 0.02, 0.999), Theme.Blue, 2.2));
        return g;
    }

    private static Path ArcPath(double size, double fraction, Brush stroke, double thick)
    {
        var r = (size - thick) / 2;
        var c = size / 2;
        var angle = fraction * 2 * Math.PI;
        var start = new Point(c, c - r);
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geo = new PathGeometry([fig]);
        geo.Freeze();
        return new Path { Data = geo, Stroke = stroke, StrokeThickness = thick, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    }

    private static FrameworkElement Tile(string source, double size, double radius)
    {
        var (color, glyph, text) = Theme.Mark(source);
        var top = Color.FromRgb((byte)Math.Min(255, color.R + 22), (byte)Math.Min(255, color.G + 22), (byte)Math.Min(255, color.B + 22));
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = Theme.Freeze(new LinearGradientBrush(top, color, 90)),
        };
        var tb = text != null
            ? new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = size * 0.62, Foreground = Brushes.White, FontWeight = FontWeights.Bold }
            : new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = size * 0.5, Foreground = Brushes.White };
        tb.HorizontalAlignment = HorizontalAlignment.Center;
        tb.VerticalAlignment = VerticalAlignment.Center;
        b.Child = tb;
        return b;
    }

    private static TextBlock Txt(double size, FontWeight weight, Brush fg) => new()
    {
        FontFamily = Theme.Text, FontSize = size, FontWeight = weight, Foreground = fg,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
    };

    private static TextBlock WxGlyph(string g, double size, Brush fg) => new()
    {
        Text = g, FontFamily = Theme.Symbols, FontSize = size, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Glyph(string g, double size, Brush fg) => new()
    {
        Text = g, FontFamily = Theme.Icons, FontSize = size, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
    };

    private double MeasureText(string s, double size, FontWeight w)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Theme.Text, FontStyles.Normal, w, FontStretches.Normal), size, Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }
}
