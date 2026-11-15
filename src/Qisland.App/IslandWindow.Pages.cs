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
    private enum Page { Home, Widgets, Shelf }
    private static readonly string[] PageNames = ["主页", "组件", "文件架"];

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

    private double _scrollAcc;
    private DateTime _scrollLast = DateTime.MinValue, _scrollSwitched = DateTime.MinValue;
    private bool _scrollLocked;

    /// <summary>
    /// 滚轮和触控板统一走这里，delta 为正表示往下一页。
    /// 触控板一次滑动会连发几十个小增量，还带惯性：累计到阈值翻一页，然后锁住直到这次手势停下，
    /// 保证"滑一下 = 翻一页"；鼠标滚轮每格 120，直接翻。
    /// </summary>
    private void OnScroll(int delta)
    {
        var now = DateTime.Now;
        var quiet = (now - _scrollLast).TotalMilliseconds > 200;
        _scrollLast = now;
        if (quiet) { _scrollLocked = false; _scrollAcc = 0; }
        if (_mode != Mode.Expanded) { SetMode(Mode.Expanded); _scrollLocked = true; return; }
        if (_scrollLocked)
        {
            // 鼠标滚轮一格一格拨时，隔开一点就允许连续翻
            if (Math.Abs(delta) >= 120 && (now - _scrollSwitched).TotalMilliseconds > 420) { _scrollLocked = false; _scrollAcc = 0; }
            else return;
        }
        _scrollAcc += delta;
        if (Math.Abs(_scrollAcc) < 40) return;
        var dir = _scrollAcc > 0 ? 1 : -1;
        _scrollAcc = 0;
        _scrollLocked = true;
        _scrollSwitched = now;
        Step(dir);
    }

    private void Step(int dir)
    {
        var next = (int)_page + dir;
        if (next < 0 || next > 2) { Nudge(-dir); return; }
        GoTo((Page)next, dir);
    }

    // 水平滚动（触控板左右滑、鼠标侧滚轮）WPF 不发事件，从窗口消息里接
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x020E) // WM_MOUSEHWHEEL
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            OnScroll(delta);
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    // ───────────── 鼠标按住左右拖动翻页 ─────────────

    private Point? _swipeStart;
    private double _swipeDx;
    private bool _swiping;

    private void OnSwipeMove(object sender, MouseEventArgs e)
    {
        if (_swipeStart is not Point s0 || e.LeftButton != MouseButtonState.Pressed || _mode != Mode.Expanded) { if (!_swiping) _swipeStart = null; return; }
        var p = e.GetPosition(this);
        var dx = p.X - s0.X;
        if (!_swiping)
        {
            if (Math.Abs(dx) < 10 || Math.Abs(dx) < Math.Abs(p.Y - s0.Y) * 1.5) return;
            _swiping = true;
            _root.CaptureMouse();
        }
        _swipeDx = dx;
        if (_expStack != null)
        {
            // 到头时阻尼更大，像拉橡皮筋
            var edge = ((int)_page == 0 && dx > 0) || ((int)_page == 2 && dx < 0);
            var tt = _expStack.RenderTransform as TranslateTransform ?? new TranslateTransform();
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = dx * (edge ? 0.2 : 0.55);
            _expStack.RenderTransform = tt;
        }
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
        _swiping = false;
        _swipeStart = null;
        _swipeDx = 0;
        if (_root.IsMouseCaptured) _root.ReleaseMouseCapture();
        var dir = dx < 0 ? 1 : -1;
        var next = (int)_page + dir;
        if (Math.Abs(dx) > 50 && next >= 0 && next <= 2) { GoTo((Page)next, dir); return; }
        if (_expStack?.RenderTransform is TranslateTransform tt)
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 } });
    }

    private void GoTo(Page p, int dir)
    {
        if (p == _page) return;
        _page = p;
        RebuildExpanded();
        if (p == Page.Widgets) RefreshWidgets();
        SlideIn(dir);
        SetMode(Mode.Expanded);
    }

    private void SlideIn(int dir)
    {
        if (_expStack == null) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var tt = new TranslateTransform(34 * dir, 0);
        _expStack.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(34 * dir, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
        _expStack.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    // 到头了就轻轻顶一下再弹回来
    private void Nudge(int dir)
    {
        if (_expStack == null) return;
        var tt = new TranslateTransform();
        _expStack.RenderTransform = tt;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(10 * dir, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    /// <summary>展开时内容一行行浮上来（NotchNook 那种），而不是整块一起出现。</summary>
    private void StaggerIn()
    {
        if (_expStack == null) return;
        _expStack.RenderTransform = null;
        _expStack.BeginAnimation(OpacityProperty, null);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var i = 0;
        foreach (UIElement c in _expStack.Children)
        {
            var begin = TimeSpan.FromMilliseconds(50 + Math.Min(i, 6) * 38);
            var tt = new TranslateTransform(0, 10);
            c.RenderTransform = tt;
            c.Opacity = 0;
            c.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin, EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = ease });
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
            default: BuildHome(stack); break;
        }
        stack.Children.Add(PageDots());
    }

    private FrameworkElement PageDots()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent };
        for (var i = 0; i < 3; i++)
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
        if (_mode == Mode.Expanded && _page == Page.Widgets) RefreshWidgets();
    }

    private void OnCollapsed()
    {
        // 收起后回到主页，下次悬停先看到消息
        if (_page != Page.Home && !_dragging) { _page = Page.Home; _expDirty = true; }
    }

    // ───────────── 组件页 ─────────────

    private void BuildWidgets(StackPanel stack)
    {
        _gaugeHost = new Border { Height = 104 };
        stack.Children.Add(_gaugeHost);

        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var cal = Card(Calendar());
        row.Children.Add(cal);
        _timerHost = Card(null);
        Grid.SetColumn(_timerHost, 2);
        row.Children.Add(_timerHost);
        stack.Children.Add(row);
        RefreshWidgets(sample: false);
    }

    private static Border Card(UIElement? child) => new()
    {
        Background = Theme.Fill, CornerRadius = new CornerRadius(18), Padding = new Thickness(14, 12, 14, 12), Child = child,
    };

    private void RefreshWidgets(bool sample = true)
    {
        if (sample) _stats.Sample();
        if (_gaugeHost != null) _gaugeHost.Child = Gauges();
        // 计时卡片只在计时中或状态变化时重建，免得按钮悬停色每秒被冲掉
        var active = Timer?.Active == true;
        if (_timerHost != null && (!sample || active || active != _timerShown)) { _timerHost.Child = TimerWidget(); _timerShown = active; }
    }
    private bool _timerShown;

    private FrameworkElement Gauges()
    {
        var g = new UniformGrid { Columns = 4, Rows = 1 };
        g.Children.Add(Gauge(_stats.Cpu, $"{_stats.Cpu * 100:0}%", "CPU", "处理器", Theme.Blue));
        g.Children.Add(Gauge(_stats.MemFraction, $"{_stats.MemFraction * 100:0}%", "内存", $"{_stats.MemUsedGb:0.0}/{_stats.MemTotalGb:0} GB", Purple));
        if (_stats.Battery is int b)
            g.Children.Add(Gauge(b / 100.0, $"{b}", "电池", _stats.Charging ? "充电中" : "使用电池", b <= 20 ? Theme.Red : Theme.Green));
        else
            g.Children.Add(Gauge(1, "AC", "电源", "外接电源", Theme.Green));
        g.Children.Add(NetGauge());
        return g;
    }

    private FrameworkElement Gauge(double p, string value, string label, string sub, Brush color)
    {
        var card = new Border { Background = Theme.Fill, CornerRadius = new CornerRadius(18), Margin = new Thickness(0, 0, 8, 0) };
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Grid { Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(new Ellipse { Stroke = Theme.Fill, StrokeThickness = 5 });
        if (p > 0.005) ring.Children.Add(ArcPath(50, Math.Clamp(p, 0.01, 0.999), color, 5));
        ring.Children.Add(new TextBlock { Text = value, FontFamily = Theme.Display, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(ring);
        var l = Txt(12, FontWeights.SemiBold, Theme.Primary); l.Text = label; l.HorizontalAlignment = HorizontalAlignment.Center; l.Margin = new Thickness(0, 6, 0, 0);
        var s = Txt(10.5, FontWeights.Normal, Theme.Tertiary); s.Text = sub; s.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(l); sp.Children.Add(s);
        card.Child = sp;
        return card;
    }

    private FrameworkElement NetGauge()
    {
        var card = new Border { Background = Theme.Fill, CornerRadius = new CornerRadius(18) };
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement Line(string glyph, double bps, Brush c)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
            r.Children.Add(Glyph(glyph, 11, c));
            var t = new TextBlock { Text = SystemStats.Speed(bps), FontFamily = Theme.Display, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary, Margin = new Thickness(5, 0, 0, 0) };
            r.Children.Add(t);
            return r;
        }
        sp.Children.Add(Line("\uE74B", _stats.DownBps, Teal));
        sp.Children.Add(Line("\uE74A", _stats.UpBps, Theme.Orange));
        var l = Txt(12, FontWeights.SemiBold, Theme.Primary); l.Text = "网络"; l.HorizontalAlignment = HorizontalAlignment.Center; l.Margin = new Thickness(0, 8, 0, 0);
        var s = Txt(10.5, FontWeights.Normal, Theme.Tertiary); s.Text = "下载 / 上传"; s.HorizontalAlignment = HorizontalAlignment.Center;
        sp.Children.Add(l); sp.Children.Add(s);
        card.Child = sp;
        return card;
    }

    private FrameworkElement Calendar()
    {
        var today = DateTime.Today;
        var sp = new StackPanel();
        var head = new Grid();
        var m = Txt(13, FontWeights.SemiBold, Theme.Primary); m.Text = $"{today.Month}月";
        var y = Txt(11.5, FontWeights.Normal, Theme.Tertiary); y.Text = $"{today:yyyy} · 周{"日一二三四五六"[(int)today.DayOfWeek]}"; y.HorizontalAlignment = HorizontalAlignment.Right; y.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(m); head.Children.Add(y);
        sp.Children.Add(head);

        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var d in "一二三四五六日")
        {
            var t = Txt(10, FontWeights.Medium, Theme.Tertiary); t.Text = d.ToString(); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 0, 0, 2);
            grid.Children.Add(t);
        }
        var first = new DateTime(today.Year, today.Month, 1);
        var lead = ((int)first.DayOfWeek + 6) % 7;
        for (var i = 0; i < lead; i++) grid.Children.Add(new Border());
        var days = DateTime.DaysInMonth(today.Year, today.Month);
        for (var d = 1; d <= days; d++)
        {
            var isToday = d == today.Day;
            var weekend = ((lead + d - 1) % 7) >= 5;
            var cell = new Grid { Height = 21 };
            if (isToday) cell.Children.Add(new Ellipse { Width = 21, Height = 21, Fill = Theme.Red });
            cell.Children.Add(new TextBlock
            {
                Text = d.ToString(CultureInfo.InvariantCulture), FontFamily = Theme.Text, FontSize = 11,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isToday ? Brushes.White : weekend ? Theme.Secondary : Theme.Primary,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            grid.Children.Add(cell);
        }
        sp.Children.Add(grid);
        return sp;
    }

    private FrameworkElement TimerWidget()
    {
        var sp = new StackPanel();
        var t = Timer;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Glyph("\uE916", 13, Theme.Orange));
        var title = Txt(13, FontWeights.SemiBold, Theme.Primary); title.Margin = new Thickness(6, 0, 0, 0);
        head.Children.Add(title);
        sp.Children.Add(head);

        if (t is { Active: true })
        {
            title.Text = t.Label + "中";
            var left = t.Remaining;
            var total = Math.Max(1, t.Total.TotalSeconds);
            var big = new TextBlock
            {
                Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}", FontFamily = Theme.Display, FontSize = 34, FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Primary, Margin = new Thickness(0, 10, 0, 6),
            };
            sp.Children.Add(big);
            var bar = new Grid { Height = 5, Margin = new Thickness(0, 0, 0, 12) };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = Theme.Fill });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2.5), Background = Theme.Orange, HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(5, 160 * (1 - left.TotalSeconds / total)),
            });
            sp.Children.Add(bar);
            sp.Children.Add(PillButton("结束计时", Theme.Red, () => { Timer?.Cancel(); RefreshWidgets(false); }));
        }
        else
        {
            title.Text = "专注计时";
            var sub = Txt(11, FontWeights.Normal, Theme.Tertiary); sub.Text = "结束时岛上会提醒你"; sub.Margin = new Thickness(0, 2, 0, 8);
            sp.Children.Add(sub);
            foreach (var (min, label, kind) in new[] { (25, "专注 25 分钟", "专注"), (50, "专注 50 分钟", "专注"), (5, "休息 5 分钟", "休息") })
                sp.Children.Add(PillButton(label, kind == "休息" ? Theme.Green : Theme.Blue, () => { Timer?.Start(min, kind); RefreshWidgets(false); }));
        }
        return sp;
    }

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
        title.Children.Add(Glyph("\uE7B8", 14, Theme.Blue));
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
            if (wasOpen) SlideIn(1);
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
