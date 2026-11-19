using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Qisland.App;

/// <summary>展开后的多页：滚轮切换 主页 / 组件 / 文件架，拖文件进岛自动跳到文件架。</summary>
public sealed partial class IslandWindow
{
    private enum Page { Home, Widgets, Todo, Clip, Shelf, Agents }
    private static readonly string[] PageNames = ["主页", "组件", "待办", "剪贴板", "文件架", "AI"];
    private const int LastPage = 5;

    private Page _page = Page.Home;
    private StackPanel? _expStack;
    private DateTime _lastWheel = DateTime.MinValue;
    private bool _dragging;
    private Border? _dropZone, _gaugeHost, _timerHost;
    private readonly SystemStats _stats = new();
    private readonly Shelf _shelf = new();

    public FocusTimer? Timer { get; set; }

    private static readonly Brush Purple = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xBF, 0x5A, 0xF2)));
    private static readonly Brush Teal = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x40, 0xC8, 0xE0)));

    private void SetupPages()
    {
        // 启动 3 秒后在后台把每一页各建一次，记下耗时（也顺便把字体、图标缓存预热好，第一次翻页不再卡）
        var warm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        warm.Tick += (_, _) =>
        {
            // 正在动画 / 展开 / 鼠标在岛上时不预热（预热会占住 UI 线程半秒，正在跑的动画就会卡一下），一秒后再看
            if (_mode == Mode.Expanded || _animating || _hoverSince != DateTime.MaxValue) { warm.Interval = TimeSpan.FromSeconds(1); return; }
            warm.Stop();
            var keep = _page;
            foreach (var p in Enum.GetValues<Page>())
            {
                _page = p;
                var t = System.Diagnostics.Stopwatch.StartNew();
                RebuildExpanded();
                _expanded.UpdateLayout();
                App.Log($"warm {p}: {t.Elapsed.TotalMilliseconds:0.0} ms");
            }
            // 预热完把每页都放进缓存（RebuildExpanded 会清缓存，所以逐页建完再按顺序留存）
            var built = new Dictionary<Page, (StackPanel, double, Border?, TextBlock?)>();
            foreach (var p in Enum.GetValues<Page>()) { _page = p; RebuildExpanded(); built[p] = (_expStack!, _expandedH, _mediaFill, _mediaTime); }
            _page = keep;
            RebuildExpanded();
            foreach (var kv in built) if (kv.Key != keep) _pageCache[kv.Key] = kv.Value;
        };
        warm.Start();
        Deactivated += (_, _) => EndEditing();
        AllowDrop = true;
        PreviewMouseWheel += (_, e) => { OnScroll(-e.Delta); e.Handled = true; };
        _root.PreviewMouseLeftButtonDown += (_, e) => { _swipeStart = e.GetPosition(this); _swipeDx = 0; };
        _root.PreviewMouseMove += OnSwipeMove;
        _root.PreviewMouseLeftButtonUp += OnSwipeUp;
        _root.LostMouseCapture += (_, _) => { if (_swiping) EndSwipe(); };
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => { _dragging = false; HighlightDrop(false); };
        Drop += OnDrop;
        _shelf.Changed += () => { if (_page == Page.Shelf) { RebuildExpanded(); } else _expDirty = true; };
    }

    // ───────────── 页面切换 ─────────────

    // 翻页偏移由一根弹簧驱动：手指拖动时直接跟手（带橡皮筋阻尼），松手后弹簧带轻微回弹归位
    private readonly Spring _pageX = new(0) { Response = 0.46, DampingRatio = 0.74, Epsilon = 0.2 };
    private readonly TranslateTransform _pageTT = new();
    private double _scrollAcc;
    private DateTime _scrollLast = DateTime.MinValue, _scrollSwitched = DateTime.MinValue, _pageFlippedAt = DateTime.MinValue;
    private int _scrollAxis; // 0 未定，1 竖，2 横
    private bool _scrollLocked;
    private System.Windows.Threading.DispatcherTimer? _gestureEnd;

    private void ApplyPageOffset()
    {
        if (_expStack == null) return;
        if (_expStack.RenderTransform != _pageTT) _expStack.RenderTransform = _pageTT;
        var x = _pageX.Value;
        if (Math.Abs(x) < 0.5) x = 0; // 停在小数像素上文字会糊
        _pageTT.X = x;
        _expStack.Opacity = 1 - Math.Min(0.55, Math.Abs(x) / 260);
        // 翻页滑动期间把整页栅格化成一张位图，GPU 只做平移和淡入；否则每帧都要按 300% 缩放把整页重画一遍
        // （组件页的渐变卡片、半透明天空最重，实测从 ~62 帧提到 ~90 帧）。停稳后撤掉，避免文字发糊。
        {
            var want = Math.Abs(x) > 0.5;
            if (want && _expStack.CacheMode == null) _expStack.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
            else if (!want && _expStack.CacheMode != null) _expStack.CacheMode = null;
        }
    }

    /// <summary>橡皮筋：拉得越远阻力越大，永远拉不过 limit。</summary>
    private static double Rubber(double x, double limit) => Math.Sign(x) * limit * (1 - 1 / (Math.Abs(x) / limit + 1));

    private bool AtEdge(int dir) => (int)_page + dir < 0 || (int)_page + dir > LastPage;

    /// <summary>
    /// 滚轮和触控板统一走这里，delta 为正表示往下一页。
    /// 鼠标滚轮（每格 120）直接翻页；触控板是连续的小增量：内容先跟着手指被"拉"过去，
    /// 拉过阈值才翻，没过阈值松手就弹回原位；一次手势（含惯性）只翻一页。
    /// </summary>
    private void OnScroll(int delta, bool horizontal = false)
    {
        var now = DateTime.Now;
        var quiet = (now - _scrollLast).TotalMilliseconds > 200;
        _scrollLast = now;
        // 轴锁：一串手势的第一个事件定方向。触控板横滑时会夹带少量竖向增量（反之亦然），
        // 不锁的话横滑会被当成竖滚去滚组件页，两边来回抢就是"往下滑还卡"。
        if (quiet || _scrollAxis == 0) _scrollAxis = horizontal ? 2 : 1;
        else if (_scrollAxis != (horizontal ? 2 : 1)) return;
        RestartGestureEnd();
        if (_mode != Mode.Expanded) { SetMode(Mode.Expanded); _scrollLocked = true; return; }
        // 翻到组件页的那一下滑动（含触控板惯性）不能接着拿去滚组件，否则一翻过来就自己往下滑。
        // 要等这串手势停下（200 ms 没事件）再滚；鼠标滚轮翻页后也空 400 ms。
        if (quiet) { _scrollLocked = false; _scrollAcc = 0; }
        var flipping = _scrollLocked || (now - _pageFlippedAt).TotalMilliseconds < 400;
        if (!horizontal && !flipping && ScrollWidgets(delta)) { _scrollAcc = 0; _scrollSwitched = now; return; }
        // 触控板竖滑只用来滚内容，永远不翻页；翻页交给横滑（鼠标滚轮没有横向，仍然可以竖滚翻页）
        if (!horizontal && !(Math.Abs(delta) >= 120 && delta % 120 == 0)) { _scrollAcc = 0; return; }

        var discrete = Math.Abs(delta) >= 120 && delta % 120 == 0;
        if (discrete)
        {
            if ((now - _scrollSwitched).TotalMilliseconds < 380) return;
            _scrollSwitched = now;
            _scrollAcc = 0;
            Step(delta > 0 ? 1 : -1);
            return;
        }

        if (quiet) { _scrollLocked = false; _scrollAcc = 0; }
        if (_scrollLocked) return;
        _scrollAcc += delta;
        var dir = _scrollAcc > 0 ? 1 : -1;
        var edge = AtEdge(dir);
        _pageX.Snap(Rubber(-_scrollAcc * 0.55, edge ? 46 : 150));
        ApplyPageOffset();
        if (!edge && Math.Abs(_scrollAcc) >= 190)
        {
            _scrollLocked = true;
            _scrollAcc = 0;
            _scrollSwitched = now;
            GoTo((Page)((int)_page + dir), dir);
        }
    }

    private void RestartGestureEnd()
    {
        if (_gestureEnd == null)
        {
            _gestureEnd = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
            _gestureEnd.Tick += (_, _) =>
            {
                _gestureEnd!.Stop();
                // 手势结束还没翻页：弹回原位
                if (!_scrollLocked && Math.Abs(_pageX.Value) > 0.5) { _pageX.Target = 0; Kick(); }
                _scrollLocked = false;
                _scrollAcc = 0;
            };
        }
        _gestureEnd.Stop();
        _gestureEnd.Start();
    }

    private void Step(int dir)
    {
        if (AtEdge(dir)) { Nudge(dir); return; }
        GoTo((Page)((int)_page + dir), dir);
    }

    // 水平滚动（触控板左右滑、鼠标侧滚轮）WPF 不发事件，从窗口消息里接
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x020E) // WM_MOUSEHWHEEL
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            OnScroll(delta, horizontal: true);
            handled = true;
            return new IntPtr(1);
        }
        // WM_MOUSEACTIVATE：点岛（呼出、展开、点按钮）都不激活窗口，不抢前台程序（全屏游戏、视频）的焦点；
        // 只有在待办输入框里打字时才允许激活（BeginEditing 自己会 Activate）
        if (msg == 0x0021 && !_editing) { handled = true; return new IntPtr(3); }   // MA_NOACTIVATE
        if (msg == 0x031D) { OnClipboardUpdate(); return IntPtr.Zero; }   // WM_CLIPBOARDUPDATE
        if (msg == 0x0312 && wParam.ToInt32() == HotkeyId) { OnHotkey(); handled = true; return IntPtr.Zero; }
        if (msg == 0x0312 && wParam.ToInt32() is TranslateHotkeyId or AskHotkeyId) { OnAiHotkey(wParam.ToInt32() == AskHotkeyId); handled = true; return IntPtr.Zero; }
        // 任务栏挪了位置 / 开关了自动隐藏：工作区变了，重新摆（DisplaySettingsChanged 不管这个）
        if (msg == 0x001A && wParam.ToInt64() == 0x2F /*SPI_SETWORKAREA*/) Dispatcher.BeginInvoke(PlaceOnScreen);
        if (msg == 0x0218) // WM_POWERBROADCAST
        {
            var b = BrightnessWatcher.Parse(wParam, lParam);
            if (b >= 0) OnBrightness(b);
        }
        return IntPtr.Zero;
    }

    private void GoTo(Page p, int dir)
    {
        _pageFlippedAt = DateTime.Now;
        if (p == _page) return;
        if (p == Page.Widgets) { _wScroll = 0; _wScrollTT.BeginAnimation(TranslateTransform.YProperty, null); _wScrollTT.Y = 0; } // 每次翻进来都从顶部开始
        if (_page == Page.Widgets && _widgetEdit) { _widgetEdit = false; _pageCache.Remove(Page.Widgets); }
        _page = p;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ShowPage();
        var t1 = sw.Elapsed.TotalMilliseconds;
        if (p == Page.Widgets) { EnterWidgets(); PopWidgets(); }
        else PopChildren();
        var t2 = sw.Elapsed.TotalMilliseconds;
        if (t2 > 8) App.Log($"翻页到{PageNames[(int)p]}：换页 {t1:0} ms，进入 {t2 - t1:0} ms");
        // 新页从运动方向那一侧滑进来，弹簧收尾时会轻轻过冲一下再停住
        _pageX.Value = dir * 110;
        _pageX.Velocity = 0;
        _pageX.Target = 0;
        ApplyPageOffset();
        SetMode(Mode.Expanded);
        Kick();
    }

    // 到头了：给弹簧一个冲量，顶一下再弹回来
    private void Nudge(int dir)
    {
        _pageX.Target = 0;
        _pageX.Velocity = -dir * 520;
        Kick();
    }

    // ───────────── 鼠标按住左右拖动翻页 ─────────────

    private Point? _swipeStart;
    private double _swipeDx, _swipeV;
    private DateTime _swipeT;
    private bool _swiping;

    private void OnSwipeMove(object sender, MouseEventArgs e)
    {
        if (_todoBox?.IsMouseCaptureWithin == true) return;
        if (_swipeStart is not Point s0 || e.LeftButton != MouseButtonState.Pressed || _mode != Mode.Expanded) { if (!_swiping) _swipeStart = null; return; }
        var p = e.GetPosition(this);
        var dx = p.X - s0.X;
        if (!_swiping)
        {
            if (Math.Abs(dx) < 10 || Math.Abs(dx) < Math.Abs(p.Y - s0.Y) * 1.5) return;
            _swiping = true;
            _swipeT = DateTime.Now;
            _root.CaptureMouse();
        }
        var now = DateTime.Now;
        var dt = Math.Max(0.004, (now - _swipeT).TotalSeconds);
        _swipeV = 0.7 * _swipeV + 0.3 * ((dx - _swipeDx) / dt);
        _swipeT = now;
        _swipeDx = dx;
        var edge = AtEdge(dx < 0 ? 1 : -1);
        _pageX.Snap(Rubber(dx, edge ? 46 : 170));
        ApplyPageOffset();
        e.Handled = true;
    }

    private void OnSwipeUp(object sender, MouseButtonEventArgs e)
    {
        if (!_swiping) { _swipeStart = null; return; }
        e.Handled = true; // 拖动松手不算点击
        EndSwipe();
    }

    private void EndSwipe()
    {
        var dx = _swipeDx;
        var v = _swipeV;
        _swiping = false;
        _swipeStart = null;
        _swipeDx = 0; _swipeV = 0;
        if (_root.IsMouseCaptured) _root.ReleaseMouseCapture();
        var dir = dx < 0 ? 1 : -1;
        // 拖得够远，或者甩得够快，都算翻页
        if (!AtEdge(dir) && (Math.Abs(dx) > 70 || (Math.Abs(v) > 600 && Math.Sign(v) == -dir && Math.Abs(dx) > 20))) { GoTo((Page)((int)_page + dir), dir); return; }
        _pageX.Velocity = AtEdge(dir) ? 0 : v * 0.3;
        _pageX.Target = 0;
        Kick();
    }

    /// <summary>展开时内容一行行浮上来（NotchNook 那种），而不是整块一起出现。</summary>
    private void StaggerIn()
    {
        if (_expStack == null) return;
        _pageX.Snap(0);
        ApplyPageOffset();
        PopChildren();
    }

    /// <summary>当前页的每一块依次「蹦」出来（带一点过冲），组件页里的卡片另外按行弹。</summary>
    private void PopChildren()
    {
        if (_expStack == null) return;
        var i = 0;
        foreach (FrameworkElement c in _expStack.Children)
        {
            PopIn(c, 40 + Math.Min(i, 6) * 50, 0.9, 14);
            i++;
        }
    }

    private void BuildPage(StackPanel stack)
    {
        _gaugeHost = null; _timerHost = null; _dropZone = null;
        switch (_page)
        {
            case Page.Widgets: BuildWidgets(stack); break;
            case Page.Shelf: BuildShelf(stack); break;
            case Page.Todo: BuildTodo(stack); break;
            case Page.Clip: BuildClip(stack); break;
            case Page.Agents: BuildAgents(stack); break;
            default: BuildHome(stack); break;
        }
        stack.Children.Add(PageDots());
    }

    private FrameworkElement PageDots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent };
        for (var i = 0; i <= LastPage; i++)
        {
            var on = i == (int)_page;
            var dot = new Border
            {
                Width = on ? 16 : 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0),
                Background = on ? Theme.Secondary : Theme.Fill, ToolTip = PageNames[i],
            };
            var target = (Page)i;
            var hit = new Border { Padding = new Thickness(2, 6, 2, 6), Background = Brushes.Transparent, Child = dot, Cursor = Cursors.Hand };
            hit.MouseLeftButtonUp += (_, e) => { GoTo(target, target > _page ? 1 : -1); e.Handled = true; };
            row.Children.Add(hit);
        }
        return row;
    }

    private void OnPageSecond()
    {
        if (_mode == Mode.Expanded && _page == Page.Widgets) { RefreshWidgets(); if (!_animating) RefitExpanded(); }
    }

    private void OnCollapsed()
    {
        // 收起后回到主页，下次悬停先看到消息
        EndEditing();
        if (_page != Page.Home && !_dragging) { _page = Page.Home; _expDirty = true; }
    }

    private static Border Card(UIElement? child) => new()
    {
        Background = Theme.Card, BorderBrush = Theme.CardRim, BorderThickness = new Thickness(Theme.CardRim == null ? 0 : 1), CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 12, 14, 12), Child = child,
    };

    private static FrameworkElement PillButton(string text, Brush accent, Action click)
    {
        var c = ((SolidColorBrush)accent).Color;
        var normal = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x26, c.R, c.G, c.B)));
        var hover = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
        var b = new Border { CornerRadius = new CornerRadius(11), Background = normal, Height = 30, Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand };
        b.Child = new TextBlock { Text = text, FontFamily = Theme.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = normal;
        b.MouseLeftButtonUp += (_, e) => { click(); e.Handled = true; };
        return b;
    }

    // ───────────── 文件架 ─────────────

    private void BuildShelf(StackPanel stack)
    {
        _shelf.Prune();
        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(PageIcon("shelf"));
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "文件架"; tt.Margin = new Thickness(7, 0, 0, 0);
        title.Children.Add(tt);
        if (_shelf.Items.Count > 0)
        {
            var n = Txt(12, FontWeights.Normal, Theme.Tertiary); n.Text = $"  {_shelf.Items.Count} 项"; title.Children.Add(n);
        }
        head.Children.Add(title);
        if (_shelf.Items.Count > 0)
        {
            var clear = Txt(12, FontWeights.Medium, Theme.Secondary); clear.Text = "清空"; clear.Cursor = Cursors.Hand;
            clear.HorizontalAlignment = HorizontalAlignment.Right; clear.VerticalAlignment = VerticalAlignment.Center;
            clear.MouseLeftButtonUp += (_, e) => { _shelf.Clear(); e.Handled = true; };
            head.Children.Add(clear);
        }
        stack.Children.Add(head);

        _dropZone = new Border { CornerRadius = new CornerRadius(18), Background = _shelf.Items.Count == 0 || _dragging ? Theme.Fill : Brushes.Transparent, MinHeight = 112 };
        if (_shelf.Items.Count == 0)
        {
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var g = Glyph("\uE896", 26, Theme.Secondary); g.HorizontalAlignment = HorizontalAlignment.Center;
            var a = Txt(13, FontWeights.SemiBold, Theme.Primary); a.Text = "把文件拖到这里暂存"; a.HorizontalAlignment = HorizontalAlignment.Center; a.Margin = new Thickness(0, 8, 0, 2);
            var b = Txt(11, FontWeights.Normal, Theme.Tertiary); b.Text = "只记位置不复制，用时再拖出去"; b.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(g); sp.Children.Add(a); sp.Children.Add(b);
            _dropZone.Child = sp;
        }
        else
        {
            var wrap = new WrapPanel { ItemWidth = 105, Margin = new Thickness(0, 4, 0, 4) };
            foreach (var p in _shelf.Items) wrap.Children.Add(ShelfTile(p));
            _dropZone.Child = wrap;
        }
        stack.Children.Add(_dropZone);
    }

    private FrameworkElement ShelfTile(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (name.Length == 0) name = path;
        var g = new Grid { Height = 96, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = path };
        var bg = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(3), Background = Brushes.Transparent };
        g.Children.Add(bg);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var src = ShellIcon.Get(path);
        FrameworkElement icon = src != null
            ? new Image { Source = src, Width = 48, Height = 48, Stretch = Stretch.Uniform }
            : Glyph("\uE8A5", 36, Theme.Secondary);
        if (src is System.Windows.Media.Imaging.BitmapImage) // 图片缩略图加圆角
            icon = new Border { Width = 52, Height = 48, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new ImageBrush(src) { Stretch = Stretch.UniformToFill } };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(icon);
        var t = new TextBlock
        {
            Text = name, FontFamily = Theme.Text, FontSize = 11, Foreground = Theme.Primary, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 30, Margin = new Thickness(6, 5, 6, 0),
        };
        sp.Children.Add(t);
        g.Children.Add(sp);

        Point? down = null;
        g.MouseEnter += (_, _) => bg.Background = Theme.Hover;
        g.MouseLeave += (_, _) => { bg.Background = Brushes.Transparent; };
        g.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(g); bg.Background = Theme.Press; e.Handled = true; };
        g.MouseMove += (_, e) =>
        {
            if (down is not Point d || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(g);
            if (Math.Abs(p.X - d.X) + Math.Abs(p.Y - d.Y) < 6) return;
            down = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            var fx = DragDrop.DoDragDrop(g, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (fx.HasFlag(DragDropEffects.Move) && !File.Exists(path) && !Directory.Exists(path)) _shelf.Remove(path);
        };
        g.MouseLeftButtonUp += (_, e) =>
        {
            if (down == null) return;
            down = null;
            e.Handled = true;
            try
            {
                Launcher.UnlockForeground();
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                _pinned = false;
                SetMode(DesiredRestMode());
            }
            catch (Exception ex) { App.Log("打开文件: " + ex.Message); }
        };
        g.MouseRightButtonUp += (_, e) => { _shelf.Remove(path); e.Handled = true; };
        return g;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy | DragDropEffects.Link;
        e.Handled = true;
        if (_dragging) return;
        _dragging = true;
        if (_page != Page.Shelf || _mode != Mode.Expanded)
        {
            var wasOpen = _mode == Mode.Expanded;
            _page = Page.Shelf;
            RebuildExpanded();
            if (wasOpen) { _pageX.Value = 110; _pageX.Velocity = 0; _pageX.Target = 0; ApplyPageOffset(); Kick(); }
            SetMode(Mode.Expanded);
        }
        HighlightDrop(true);
    }

    private void HighlightDrop(bool on)
    {
        if (_dropZone == null) return;
        _dropZone.Background = on ? Theme.Hover : _shelf.Items.Count == 0 ? Theme.Fill : Brushes.Transparent;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dragging = false;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _page = Page.Shelf;
            _shelf.Add(files);
            App.Log($"文件架 +{files.Length}");
        }
        HighlightDrop(false);
    }
}
