using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 组件页可以自己搭：哪些组件显示、按什么顺序，存在 %APPDATA%\Qisland\widgets.txt（一行一个，前面加 - 表示隐藏）。
/// 半宽组件（日历、专注、待办、剪贴板）相邻时两两并排；一屏放不下时组件页可以上下滚，滚到头再翻页。
/// </summary>
public sealed partial class IslandWindow
{
    private sealed record WidgetDef(string Id, string Name, string Glyph, Brush Tint, bool Half);
    private static readonly WidgetDef[] WidgetDefs =
    [
        new("weather", "天气", "\uE706", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))), false),
        new("school", "课程", "\uE7BE", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xAF, 0x52, 0xDE))), false),
        new("quick", "快捷按钮", "\uE945", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))), false),
        new("todo", "待办", "\uE73A", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))), true),
        new("clip", "剪贴板", "\uE77F", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))), true),
        new("calendar", "日历", "\uE787", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))), true),
        new("timer", "专注", "\uE823", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))), true),
        new("health", "健康", "\uEB51", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x32, 0xAD, 0xE6))), false),
        new("system", "系统", "\uE950", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))), false),
        new("devices", "设备电量", "\uE702", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))), false),
    ];
    private static string WidgetFile => System.IO.Path.Combine(App.DataDir, "widgets.txt");
    private List<(string Id, bool On)>? _widgetList;
    private bool _widgetEdit;
    private const double WidgetViewMax = 440, HalfH = 168;
    private Border? _wView;
    private readonly TranslateTransform _wScrollTT = new();
    private double _wScroll, _wScrollMax;
    private DateTime _wScrolledAt = DateTime.MinValue;
    private readonly List<(FrameworkElement El, double Top, double H)> _wRows = [];
    private bool _wRevealHooked;

    private readonly HashSet<FrameworkElement> _wShown = [];

    /// <summary>
    /// 卡片「跳出来」：从略小、略低、透明，用带过冲的弹簧曲线弹到原位，像 iOS 小组件那样蹦一下再停稳。
    /// 停稳后把变换和透明度动画全部清掉，保证文字是 1:1 清晰的。
    /// </summary>
    private static void PopIn(FrameworkElement el, double delayMs, double fromScale = 0.84, double fromY = 22)
    {
        if (ReducedMotion) { HideForPop(el, 1); return; } // 系统关了动画效果：直接出现
        var st = new ScaleTransform(fromScale, fromScale);
        var tt = new TranslateTransform(0, fromY);
        var tg = new TransformGroup(); tg.Children.Add(st); tg.Children.Add(tt);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = tg;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        var dur = TimeSpan.FromMilliseconds(560);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = begin, FillBehavior = FillBehavior.HoldEnd });
        var sa = new DoubleAnimation(fromScale, 1, dur) { BeginTime = begin, EasingFunction = back };
        sa.Completed += (_, _) =>
        {
            if (el.RenderTransform != tg) return; // 期间又被别的动画接管了
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            el.RenderTransform = null;
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, sa);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, dur) { BeginTime = begin, EasingFunction = back });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, dur) { BeginTime = begin, EasingFunction = back });
        if (delayMs > 0) el.Opacity = 0;
    }

    private static void HideForPop(FrameworkElement el, double opacity)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.RenderTransform = null;
        el.Opacity = opacity;
    }

    /// <summary>翻进组件页：看得见的卡片一张接一张蹦出来。</summary>
    private void PopWidgets()
    {
        _wShown.Clear();
        var s = -_wScrollTT.Y;
        var k = 0;
        foreach (var (el, top, h) in _wRows)
        {
            var vt = top - s;
            if (vt >= WidgetViewMax || vt + h <= 0) { HideForPop(el, 0); continue; }
            _wShown.Add(el);
            PopIn(el, 30 + k * 55);
            k++;
        }
    }

    /// <summary>
    /// 上下滚动时：卡片还没进视口多少时淡淡地露个边；进来够多时「蹦」一下弹出来；完全滚出去后复位，下次进来再蹦。
    /// </summary>
    private void ApplyReveal()
    {
        if (_wRows.Count == 0 || _wScrollMax <= 0) return;
        var s = -_wScrollTT.Y;
        foreach (var (el, top, h) in _wRows)
        {
            var vt = top - s;
            var gone = vt >= WidgetViewMax || vt + h <= 0;
            if (gone) { if (_wShown.Remove(el)) HideForPop(el, 0); continue; }
            if (_wShown.Contains(el)) continue;
            var visible = Math.Min(WidgetViewMax, vt + h) - Math.Max(0, vt);
            if (visible >= Math.Min(56, h * 0.45)) { _wShown.Add(el); PopIn(el, 0); }
            else HideForPop(el, 0.18);
        }
    }

    private List<(string Id, bool On)> Widgets()
    {
        if (_widgetList != null) return _widgetList;
        string[] lines = [];
        try { if (File.Exists(WidgetFile)) lines = File.ReadAllLines(WidgetFile); }
        catch (Exception ex) { App.Log("读取 widgets.txt: " + ex.Message); }
        // 新版本加的组件、文件里漏掉的组件：插在默认顺序里它前一个组件的后面（比如「课程」紧跟天气），默认显示
        return _widgetList = WidgetOrder.Parse(lines, WidgetDefs.Select(d => d.Id).ToList());
    }

    private void SaveWidgets()
    {
        if (SnapshotMode) return;   // 渲染素材时只改内存
        try { File.WriteAllLines(WidgetFile, WidgetOrder.Serialize(Widgets(), WidgetDefs.Select(d => d.Id))); }
        catch (Exception ex) { App.Log("保存 widgets.txt: " + ex.Message); }
    }

    // ───────────── 给设置窗「布局与 DIY」用 ─────────────

    /// <summary>组件页当前的顺序与显隐（设置页的拖拽列表照着画）。</summary>
    internal IReadOnlyList<(string Id, string Name, bool Half, bool On)> WidgetLayout =>
        Widgets().Select(w => { var d = Def(w.Id); return (w.Id, d.Name, d.Half, w.On); }).ToList();

    /// <summary>设置页改了顺序 / 显隐：存盘并立刻刷新展开页（正开着组件页就当场重排）。</summary>
    internal void SetWidgetLayout(IEnumerable<(string Id, bool On)> list)
    {
        var known = WidgetDefs.Select(d => d.Id).ToList();
        _widgetList = WidgetOrder.Parse(list.Select(w => (w.On ? "" : "-") + w.Id), known);
        SaveWidgets();
        _pageCache.Remove(Page.Widgets);
        if (_mode == Mode.Expanded && !_animating) RebuildExpanded(); else _expDirty = true;
    }

    internal void ResetWidgetLayout() => SetWidgetLayout(WidgetDefs.Select(d => (d.Id, true)));

    private static WidgetDef Def(string id) => WidgetDefs.First(d => d.Id == id);

    private void BuildWidgets(StackPanel stack)
    {
        _wxTabs.Clear();
        _wxIcons.Clear();
        _cpuT = _memT = _batT = null; _netT = null; _calHost = null; _timerCard = null; _sun = null; _sunHint = null; _wxBody = null;
        _wView = null; _healthT = null;
        if (_widgetEdit) { BuildWidgetEditor(stack); return; }

        // 内容比视口高时必须顶端对齐：WPF 默认会把超高的子元素居中，上面的天气和下面的系统行会被同时裁掉
        var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        // 设备电量：没有读到电量的已连接设备时整张卡不出现
        // 快捷按钮：一个都没配时也不占位
        var on = Widgets().Where(w => w.On && (w.Id != "devices" || HasDevices) && (w.Id != "quick" || QuickStore.Current.Count > 0)).Select(w => w.Id).ToList();
        for (var i = 0; i < on.Count;)
        {
            FrameworkElement el;
            if (Def(on[i]).Half && i + 1 < on.Count && Def(on[i + 1]).Half)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var a = Widget(on[i]); var b = Widget(on[i + 1]);
                Grid.SetColumn(b, 2);
                row.Children.Add(a); row.Children.Add(b);
                el = row; i += 2;
            }
            else { el = Widget(on[i]); i++; }
            if (inner.Children.Count > 0) el.Margin = new Thickness(0, 10, 0, 0);
            inner.Children.Add(el);
        }
        if (on.Count == 0)
        {
            var empty = Txt(12.5, FontWeights.Normal, Theme.Tertiary); empty.Text = "所有组件都关掉了";
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 24, 0, 12);
            inner.Children.Add(empty);
        }

        // 一屏放不下：裁成一个可滚动的视口
        inner.Measure(new Size(ExpandedW - 32, double.PositiveInfinity));
        var h = inner.DesiredSize.Height;
        _wScrollMax = Math.Ceiling(Math.Max(0, h - WidgetViewMax));
        _wScroll = Math.Clamp(_wScroll, 0, _wScrollMax);
        _wScrollTT.BeginAnimation(TranslateTransform.YProperty, null);
        _wScrollTT.Y = -_wScroll;
        inner.RenderTransform = _wScrollTT;
        _wRows.Clear();
        {
            // 记下每一行在内容里的位置：翻进来时依次弹出，滚动时进视口再弹
            var y = 0.0;
            foreach (FrameworkElement el in inner.Children)
            {
                var top = y + el.Margin.Top;
                y += el.DesiredSize.Height;
                _wRows.Add((el, top, el.DesiredSize.Height - el.Margin.Top));
            }
            // 页面重建（内容刷新）时不重复蹦：当前看得见的直接算已弹出
            _wShown.Clear();
            foreach (var (el, top, rh) in _wRows) if (top + _wScrollTT.Y < WidgetViewMax && top + rh + _wScrollTT.Y > 0) _wShown.Add(el);
        }
        if (_wScrollMax > 0)
        {
            if (!_wRevealHooked) { _wRevealHooked = true; _wScrollTT.Changed += (_, _) => ApplyReveal(); }
            ApplyReveal();
            // Canvas 不约束子元素高度，内容按自己的完整高度排版，视口只负责裁切
            var host = new Canvas { Height = WidgetViewMax, Width = ExpandedW - 32 };
            inner.Width = ExpandedW - 32;
            host.Children.Add(inner);
            _wView = new Border { Height = WidgetViewMax, ClipToBounds = true, Child = host };
            // 底部渐隐，提示下面还有
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.93));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            _wView.OpacityMask = mask;
            stack.Children.Add(_wView);
        }
        else stack.Children.Add(inner);

        var edit = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        edit.Children.Add(Glyph("\uE70F", 10, Theme.Tertiary));
        var et = Txt(11.5, FontWeights.Medium, Theme.Tertiary); et.Text = " 编辑组件"; edit.Children.Add(et);
        edit.MouseEnter += (_, _) => et.Foreground = Theme.Secondary;
        edit.MouseLeave += (_, _) => et.Foreground = Theme.Tertiary;
        edit.MouseLeftButtonDown += (_, e) => e.Handled = true;
        edit.MouseLeftButtonUp += (_, e) => { e.Handled = true; _widgetEdit = true; RebuildExpanded(); };
        stack.Children.Add(edit);

        _timerState = "";
        RefreshWidgets(sample: false);
    }

    private FrameworkElement Widget(string id)
    {
        switch (id)
        {
            case "weather": return WeatherCard();
            case "system": return SystemRow();
            case "school": return SchoolWidget();
            case "calendar":
                _calHost = WCard(CalendarView()); _calHost.Height = HalfH; return _calHost;
            case "timer":
                _timerCard = WCard(null); _timerCard.Height = HalfH; return _timerCard;
            case "todo": return TodoWidget();
            case "health": return HealthWidget();
            case "devices": return DevicesWidget();
            case "quick": return QuickWidget();
            default: return ClipWidget();
        }
    }

    /// <summary>小组件的标题行：图标 + 名字 + 右侧小字，点标题去对应的整页。</summary>
    private FrameworkElement WidgetHead(WidgetDef d, string right, Page? go)
    {
        var g = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        var l = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var tile = WidgetIcon(d.Id, 20);
        tile.VerticalAlignment = VerticalAlignment.Center;
        l.Children.Add(tile);
        var t = Txt(12.5, FontWeights.SemiBold, Theme.Primary); t.Text = d.Name; t.Margin = new Thickness(7, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; l.Children.Add(t);
        g.Children.Add(l);
        var r = Txt(11, FontWeights.Medium, Theme.Tertiary); r.Text = right; r.HorizontalAlignment = HorizontalAlignment.Right; r.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(r);
        if (go is { } p)
        {
            g.Cursor = Cursors.Hand; g.ToolTip = "打开" + d.Name + "页";
            g.MouseLeftButtonDown += (_, e) => e.Handled = true;
            g.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoTo(p, (int)p > (int)_page ? 1 : -1); };
        }
        return g;
    }

    /// <summary>
    /// 组件图标：渐变圆角方块 + 手画的实心矢量图形（24×24 坐标），照 iOS 小组件 / SF Symbols 的填充风格。
    /// 原来用的是 Segoe 线框字形，小尺寸下像占位符，所以改成自己画。日历仿 iOS：白底红色日期数字。
    /// </summary>
    // 图标路径每次翻页都要画一遍：解析一次后冻结复用，省掉重复的字符串解析
    private static readonly Dictionary<string, Geometry> GeoCache = new();
    private static Geometry Geo(string data)
    {
        if (GeoCache.TryGetValue(data, out var g)) return g;
        g = Geometry.Parse(data); g.Freeze();
        return GeoCache[data] = g;
    }

    internal static Border WidgetIcon(string id, double size)
    {
        static Color C(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var (top, bottom) = id switch
        {
            "weather" => (C(0x5AC8FA), C(0x1E7CF2)),
            "todo" => (C(0xFF7A6E), C(0xF5352B)),
            "clip" => (C(0x9C9BFF), C(0x5E5CE6)),
            "calendar" => (C(0xFFFFFF), C(0xE9E9EE)),
            "timer" => (C(0xFFC34D), C(0xFF8A00)),
            "shelf" => (C(0x5FD4DA), C(0x149FB5)),
            "school" => (C(0xD08CFF), C(0x9B3FDB)),
            "health" => (C(0x7FDBFF), C(0x1A9BE0)),
            "devices" => (C(0x6EB6FF), C(0x2F6BFF)),
            "quick" => (C(0xFFC24A), C(0xFF7A00)),
            "layout" => (C(0x8E8CFF), C(0x5856D6)),
            "theme" => (C(0xFF8FB1), C(0xE0457B)),
            _ => (C(0x5EE07A), C(0x24AE45)),
        };
        var bg = new LinearGradientBrush(top, bottom, 90); bg.Freeze();
        var tile = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.29), Background = bg,
            BorderThickness = new Thickness(0.6),
            BorderBrush = Theme.Freeze(new SolidColorBrush(id == "calendar" ? Color.FromArgb(0x30, 0, 0, 0) : Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF))),
            SnapsToDevicePixels = true,
        };
        if (id == "calendar")
        {
            var day = new TextBlock
            {
                Text = DateTime.Today.Day.ToString(CultureInfo.InvariantCulture), FontFamily = Theme.Text, FontWeight = FontWeights.Bold,
                FontSize = size * 0.5, Foreground = Theme.Red, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, size * 0.1, 0, 0),
            };
            var bar = new Border { Height = size * 0.22, VerticalAlignment = VerticalAlignment.Top, Background = Theme.Red, CornerRadius = new CornerRadius(size * 0.29, size * 0.29, 0, 0) };
            var g = new Grid(); g.Children.Add(bar); g.Children.Add(day);
            tile.Child = g;
            return tile;
        }
        var canvas = new Canvas { Width = 24, Height = 24 };
        void Fill(string data, double opacity = 1, Brush? brush = null)
        {
            var path = new System.Windows.Shapes.Path { Data = Geo(data), Fill = brush ?? Brushes.White, Opacity = opacity };
            canvas.Children.Add(path);
        }
        void Stroke(string data, double w)
        {
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = Geo(data), Stroke = Brushes.White, StrokeThickness = w, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        }
        switch (id)
        {
            case "weather": // 太阳 + 云
                Fill("M9 3.2a5.3 5.3 0 1 1 0 10.6a5.3 5.3 0 1 1 0-10.6Z", 1, Theme.Freeze(new SolidColorBrush(C(0xFFD60A))));
                Fill("M7.2 20.5a4.2 4.2 0 0 1-.5-8.37a5.8 5.8 0 0 1 11.1-1.6a5 5 0 0 1-.3 9.97Z");
                break;
            case "todo": // 三行勾选清单
                Fill("M3.3 4.6a2.1 2.1 0 1 1 4.2 0a2.1 2.1 0 1 1-4.2 0Z M3.3 12a2.1 2.1 0 1 1 4.2 0a2.1 2.1 0 1 1-4.2 0Z M3.3 19.4a2.1 2.1 0 1 1 4.2 0a2.1 2.1 0 1 1-4.2 0Z");
                Fill("M10.2 3.4h10.3a1.2 1.2 0 0 1 0 2.4H10.2a1.2 1.2 0 0 1 0-2.4Z M10.2 10.8h10.3a1.2 1.2 0 0 1 0 2.4H10.2a1.2 1.2 0 0 1 0-2.4Z M10.2 18.2h7.3a1.2 1.2 0 0 1 0 2.4h-7.3a1.2 1.2 0 0 1 0-2.4Z");
                break;
            case "clip": // 剪贴板：夹子 + 板身，板身上两行字
                Fill("M9 2h6a1.3 1.3 0 0 1 1.3 1.3V6H7.7V3.3A1.3 1.3 0 0 1 9 2Z");
                Fill("F1 M6 4.2h.6v3.1h10.8V4.2h.6a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-14a2 2 0 0 1 2-2Z M8 11.2v1.8h8v-1.8Z M8 15.4v1.8h5.5v-1.8Z");
                break;
            case "timer": // 秒表：表盘 + 指针 + 顶钮
                Fill("M10 1.6h4a1.1 1.1 0 0 1 0 2.2h-4a1.1 1.1 0 0 1 0-2.2Z");
                Stroke("M12 5.8a7.6 7.6 0 1 1 0 15.2a7.6 7.6 0 1 1 0-15.2Z", 2.3);
                Stroke("M12 13.4V9.4", 2.3);
                break;
            case "shelf": // 文件架：收纳盒（盖 + 盒身 + 把手槽）
                Fill("M3.5 3.8h17a1.5 1.5 0 0 1 1.5 1.5v2.2a1.5 1.5 0 0 1-1.5 1.5h-17A1.5 1.5 0 0 1 2 7.5V5.3a1.5 1.5 0 0 1 1.5-1.5Z");
                Fill("F1 M3.4 10.2h17.2v8.4a2 2 0 0 1-2 2H5.4a2 2 0 0 1-2-2Z M9.5 12.4a1.1 1.1 0 0 0 0 2.2h5a1.1 1.1 0 0 0 0-2.2Z");
                break;
            case "school": // 课程：学士帽
                Fill("M12 3.6 22.4 8.6 12 13.6 1.6 8.6Z");
                Fill("M6 11.4v4.4c0 1.9 2.7 3.6 6 3.6s6-1.7 6-3.6v-4.4l-6 2.9Z", 0.92);
                Fill("M20.2 9.8h1.4v6.4h-1.4Z", 0.85);
                break;
            case "health": // 水滴
                Fill("M12 2.4c3.6 4.5 6.6 8.3 6.6 12a6.6 6.6 0 0 1-13.2 0c0-3.7 3-7.5 6.6-12Z");
                break;
            case "devices": // 横放的电池：外壳 + 电量 + 正极
                Fill("F1 M4.6 7h12.8A2.6 2.6 0 0 1 20 9.6v4.8a2.6 2.6 0 0 1-2.6 2.6H4.6A2.6 2.6 0 0 1 2 14.4V9.6A2.6 2.6 0 0 1 4.6 7Z M4.9 8.9a.9.9 0 0 0-.9.9v4.4a.9.9 0 0 0 .9.9h12.2a.9.9 0 0 0 .9-.9V9.8a.9.9 0 0 0-.9-.9Z");
                Fill("M5.6 10.4h8.2v3.2H5.6Z");
                Fill("M20.8 10.2h.5a.9.9 0 0 1 .9.9v1.8a.9.9 0 0 1-.9.9h-.5Z", 0.85);
                break;
            case "quick": // 快捷按钮：闪电
                Fill("M13.6 1.8 4.4 13.4h6.2l-1.8 8.8 9.8-12.4h-6.4Z");
                break;
            case "layout": // 布局：一宽两窄三块
                Fill("M4.5 3.5h15a1.5 1.5 0 0 1 1.5 1.5v4a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 9V5a1.5 1.5 0 0 1 1.5-1.5Z");
                Fill("M4.5 12.5h5.5a1.5 1.5 0 0 1 1.5 1.5v5a1.5 1.5 0 0 1-1.5 1.5H4.5A1.5 1.5 0 0 1 3 19v-5a1.5 1.5 0 0 1 1.5-1.5Z M14 12.5h5.5A1.5 1.5 0 0 1 21 14v5a1.5 1.5 0 0 1-1.5 1.5H14a1.5 1.5 0 0 1-1.5-1.5v-5a1.5 1.5 0 0 1 1.5-1.5Z", 0.85);
                break;
            case "theme": // 主题：调色盘
                Fill("F1 M12 2.6c5.3 0 9.6 3.8 9.6 8.4c0 2.9-2.3 4.6-4.9 4.6h-1.9c-1 0-1.6.9-1.2 1.8l.4.9c.6 1.4-.4 3.1-2 3.1C6.7 21.4 2.4 17.2 2.4 12S6.7 2.6 12 2.6Z M7.4 9.6a1.6 1.6 0 1 0 0 3.2a1.6 1.6 0 1 0 0-3.2Z M10.6 5.6a1.6 1.6 0 1 0 0 3.2a1.6 1.6 0 1 0 0-3.2Z M15.4 6.2a1.6 1.6 0 1 0 0 3.2a1.6 1.6 0 1 0 0-3.2Z");
                break;
            default: // 系统：芯片
                Fill("F1 M8.5 5h7A3.5 3.5 0 0 1 19 8.5v7a3.5 3.5 0 0 1-3.5 3.5h-7A3.5 3.5 0 0 1 5 15.5v-7A3.5 3.5 0 0 1 8.5 5Z M9.5 9.5v5h5v-5Z");
                Fill("M9 1.8h1.6V4H9Z M13.4 1.8H15V4h-1.6Z M9 20h1.6v2.2H9Z M13.4 20H15v2.2h-1.6Z M1.8 9H4v1.6H1.8Z M1.8 13.4H4V15H1.8Z M20 9h2.2v1.6H20Z M20 13.4h2.2V15H20Z");
                break;
        }
        tile.Child = new Viewbox { Child = canvas, Margin = new Thickness(size * 0.19), Stretch = Stretch.Uniform };
        return tile;
    }

    /// <summary>整页（待办 / 剪贴板 / 文件架）标题左边的图标，和组件页同一套。</summary>
    private static Border PageIcon(string id)
    {
        var t = WidgetIcon(id, 22);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    /// <summary>行悬停：整行一层很淡的圆角底，左右各外扩 6 px，和文字对齐但不贴边。</summary>
    private static void RowHover(Grid row)
    {
        var bg = new Border { CornerRadius = new CornerRadius(8), Margin = new Thickness(-6, 1, -6, 1), Background = Brushes.Transparent, IsHitTestVisible = false };
        Grid.SetColumnSpan(bg, Math.Max(1, row.ColumnDefinitions.Count));
        row.Children.Insert(0, bg);
        row.MouseEnter += (_, _) => bg.Background = Theme.Hover;
        row.MouseLeave += (_, _) => bg.Background = Brushes.Transparent;
    }

    /// <summary>空状态上方的淡色大图标。</summary>
    private static FrameworkElement EmptyIcon(string glyph, double top)
    {
        var b = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Theme.Fill, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14 + top, 0, 0) };
        var g = Glyph(glyph, 15, Theme.Tertiary); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
        b.Child = g;
        return b;
    }

    private Border TodoWidget()
    {
        var open = (Todo?.Items ?? []).Where(i => !i.Done).ToList();
        var card = WCard(null); card.Height = HalfH;
        var sp = new StackPanel();
        sp.Children.Add(WidgetHead(Def("todo"), open.Count > 0 ? $"{open.Count} 件" : "", Page.Todo));
        foreach (var it in open.Take(4))
        {
            var row = new Grid { Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = it.Text };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var circle = new Grid { Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var ring = new Ellipse { StrokeThickness = 1.4, Stroke = Theme.Tertiary, Fill = Brushes.Transparent };
            var check = Glyph("\uE73E", 8.5, Brushes.White); check.HorizontalAlignment = HorizontalAlignment.Center; check.VerticalAlignment = VerticalAlignment.Center; check.Opacity = 0;
            circle.Children.Add(ring); circle.Children.Add(check);
            row.Children.Add(circle);
            var tx = Txt(12.5, FontWeights.Normal, Theme.Primary); tx.Text = it.Text; tx.TextTrimming = TextTrimming.CharacterEllipsis; tx.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tx, 1);
            row.Children.Add(tx);
            RowHover(row);
            row.MouseLeftButtonDown += (_, e) => e.Handled = true;
            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ring.Stroke = Theme.Blue; ring.Fill = Theme.Blue;
                check.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
                tx.Foreground = Theme.Tertiary; tx.TextDecorations = TextDecorations.Strikethrough;
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
                t.Tick += (_, _) => { t.Stop(); Todo?.SetDone(it, true); };
                t.Start();
            };
            sp.Children.Add(row);
        }
        if (open.Count > 4)
        {
            var more = Txt(11, FontWeights.Medium, Theme.Tertiary); more.Text = $"还有 {open.Count - 4} 件"; more.Margin = new Thickness(23, 1, 0, 0);
            sp.Children.Add(more);
        }
        if (open.Count == 0)
        {
            sp.Children.Add(EmptyIcon("\uE73E", 0));
            var empty = Txt(12, FontWeights.Medium, Theme.Tertiary); empty.Text = "都做完了"; empty.Margin = new Thickness(0, 6, 0, 6); empty.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(empty);
            var add = Txt(12, FontWeights.SemiBold, Theme.Blue); add.Text = "＋ 写一件"; add.HorizontalAlignment = HorizontalAlignment.Center; add.Cursor = Cursors.Hand;
            add.MouseLeftButtonDown += (_, e) => e.Handled = true;
            add.MouseLeftButtonUp += (_, e) => { e.Handled = true; GoTo(Page.Todo, 1); BeginEditing(); };
            sp.Children.Add(add);
        }
        SetBody(card, sp);
        return card;
    }

    private Border ClipWidget()
    {
        var card = WCard(null); card.Height = HalfH;
        var sp = new StackPanel();
        var copied = DateTime.Now - _copiedFlash < TimeSpan.FromSeconds(2);
        sp.Children.Add(WidgetHead(Def("clip"), copied ? "已复制" : _clips.Count > 0 ? $"{_clips.Count} 条" : "", Page.Clip));
        foreach (var c in _clips.Take(4))
        {
            var row = new Grid { Height = 24, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = c.Text.Length > 300 ? c.Text[..300] + "…" : c.Text };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            FrameworkElement icon;
            if (c.Image != null)
                icon = new Border { Width = 22, Height = 15, CornerRadius = new CornerRadius(3), Background = new ImageBrush(c.Image) { Stretch = Stretch.UniformToFill }, VerticalAlignment = VerticalAlignment.Center };
            else
            {
                var gl = Glyph(c.Files != null ? "\uE8B7" : LooksLikeUrl(c.Text) ? "\uE71B" : "\uE8C4", 11, Theme.Secondary);
                gl.Width = 22; gl.TextAlignment = TextAlignment.Left; gl.VerticalAlignment = VerticalAlignment.Center;
                icon = gl;
            }
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
            var one = string.Join(" ", c.Text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
            var tx = Txt(12.5, FontWeights.Normal, Theme.Primary); tx.Text = c.Image != null && one.Length == 0 ? "图片" : (one.Length > 80 ? one[..80] : one);
            tx.TextTrimming = TextTrimming.CharacterEllipsis; tx.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(tx, 1);
            row.Children.Add(tx);
            RowHover(row);
            row.MouseLeftButtonDown += (_, e) => e.Handled = true;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyBack(c); };
            sp.Children.Add(row);
        }
        if (_clips.Count == 0)
        {
            var empty = Txt(12, FontWeights.Normal, Theme.Tertiary); empty.Text = "复制点什么\n会出现在这里"; empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(0, 6, 0, 0); empty.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(EmptyIcon("\uE77F", 0));
            sp.Children.Add(empty);
        }
        SetBody(card, sp);
        return card;
    }

    // ───────────── 编辑 ─────────────

    private void BuildWidgetEditor(StackPanel stack)
    {
        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        var tt = Txt(13, FontWeights.SemiBold, Theme.Primary); tt.Text = "编辑组件"; tt.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(tt);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(Pill("恢复默认", Theme.Fill, Theme.Primary, () => { _widgetList = WidgetDefs.Select(d => (d.Id, true)).ToList(); SaveWidgets(); RebuildExpanded(); }));
        btns.Children.Add(Pill("完成", Theme.Blue, Brushes.White, () => { _widgetEdit = false; _wScroll = 0; RebuildExpanded(); }));
        head.Children.Add(btns);
        stack.Children.Add(head);

        var list = Widgets();
        for (var i = 0; i < list.Count; i++)
        {
            var idx = i;
            var (id, on) = list[i];
            var d = Def(id);
            var g = new Grid { Height = 40, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 4, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 开关：实心圆勾 = 显示，空心圈 = 隐藏
            var tog = new Grid { Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = on ? "隐藏" : "显示" };
            tog.Children.Add(new Ellipse { StrokeThickness = 1.5, Stroke = on ? Theme.Blue : Theme.Tertiary, Fill = on ? Theme.Blue : Brushes.Transparent });
            if (on) { var ck = Glyph("\uE73E", 10, Brushes.White); ck.HorizontalAlignment = HorizontalAlignment.Center; ck.VerticalAlignment = VerticalAlignment.Center; ck.FontWeight = FontWeights.Bold; tog.Children.Add(ck); }
            tog.MouseLeftButtonDown += (_, e) => e.Handled = true;
            tog.MouseLeftButtonUp += (_, e) => { e.Handled = true; list[idx] = (id, !on); SaveWidgets(); RebuildExpanded(); };
            g.Children.Add(tog);

            var tile = WidgetIcon(d.Id, 26); tile.Margin = new Thickness(0, 0, 10, 0); tile.VerticalAlignment = VerticalAlignment.Center; tile.Opacity = on ? 1 : 0.45;
            Grid.SetColumn(tile, 1);
            g.Children.Add(tile);

            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nt = Txt(13, FontWeights.Medium, on ? Theme.Primary : Theme.Tertiary); nt.Text = d.Name; name.Children.Add(nt);
            var sub = Txt(10.5, FontWeights.Normal, Theme.Tertiary); sub.Text = d.Half ? "半宽 · 相邻两个半宽会并排" : "整行"; name.Children.Add(sub);
            Grid.SetColumn(name, 2);
            g.Children.Add(name);

            var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var up = RoundIcon("\uE70E", 26, () => Move(idx, -1), Theme.Secondary, "上移");
            var down = RoundIcon("\uE70D", 26, () => Move(idx, 1), Theme.Secondary, "下移");
            if (i == 0) up.Opacity = 0.25;
            if (i == list.Count - 1) down.Opacity = 0.25;
            arrows.Children.Add(up); arrows.Children.Add(down);
            Grid.SetColumn(arrows, 3);
            g.Children.Add(arrows);
            AddHover(g);
            stack.Children.Add(g);
        }
        var hint = Txt(11, FontWeights.Normal, Theme.Tertiary);
        hint.Text = "也可以直接改数据目录里的 widgets.txt";
        hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(hint);
    }

    private void Move(int idx, int dir)
    {
        var list = Widgets();
        var j = idx + dir;
        if (j < 0 || j >= list.Count) return;
        (list[idx], list[j]) = (list[j], list[idx]);
        SaveWidgets();
        RebuildExpanded();
    }

    // ───────────── 滚动 ─────────────

    /// <summary>组件页内容超出一屏时先滚内容；滚到头的那一下手势不翻页，下一次手势才翻。返回 true 表示这次滚动被吃掉了。</summary>
    private bool ScrollWidgets(int delta)
    {
        if (_page != Page.Widgets || _widgetEdit || _wView == null || _wScrollMax <= 0) return false;
        var now = DateTime.Now;
        var step = Math.Abs(delta) >= 120 && delta % 120 == 0 ? delta * 0.9 : delta * 0.8;
        var target = Math.Round(Math.Clamp(_wScroll + step, 0, _wScrollMax));
        if (Math.Abs(target - _wScroll) < 0.5)
            return (now - _wScrolledAt).TotalMilliseconds < 350; // 刚滚到头：这一串滚动不翻页
        _wScroll = target;
        _wScrolledAt = now;
        if (Math.Abs(delta) >= 120 && delta % 120 == 0)
        {
            var anim = new DoubleAnimation(-_wScroll, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _wScrollTT.BeginAnimation(TranslateTransform.YProperty, anim);
        }
        else
        {
            // 触控板本身就是连续的小增量，直接跟手；每个事件再起一段动画会互相打断，看起来一顿一顿的
            _wScrollTT.BeginAnimation(TranslateTransform.YProperty, null);
            _wScrollTT.Y = -_wScroll;
        }
        var mask = (LinearGradientBrush)_wView.OpacityMask;
        mask.GradientStops[2].Color = _wScroll >= _wScrollMax - 1 ? Colors.Black : Colors.Transparent;
        return true;
    }
}

public sealed partial class IslandWindow
{
    /// <summary>课程组件：今天剩下的课 + 待交作业（原来堆在首页，现在挪到组件页）。</summary>
    private FrameworkElement SchoolWidget()
    {
        var d = WidgetDefs.First(x => x.Id == "school");
        var sp = new StackPanel();
        var tc = TodayClasses();
        var hw = HomeworkCard();
        var ag = AgendaCard();
        sp.Children.Add(WidgetHead(d, DateTime.Now.ToString("ddd", new CultureInfo("zh-CN")), null));
        if (tc != null) { tc.Margin = new Thickness(0, 0, 0, hw != null || ag != null ? 6 : 0); sp.Children.Add(tc); }
        if (ag != null) { ag.Margin = new Thickness(0, 0, 0, hw != null ? 6 : 0); sp.Children.Add(ag); }
        if (hw != null) { hw.Margin = new Thickness(0); sp.Children.Add(hw); }
        if (tc == null && hw == null && ag == null)
        {
            var e = Txt(12.5, FontWeights.Normal, Theme.Tertiary);
            e.Text = "今天没课了，也没有待交作业";
            e.Margin = new Thickness(2, 2, 0, 2);
            sp.Children.Add(e);
        }
        return WCard(sp);
    }
}
