using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 组件页。从「为什么要看这一页」出发：
/// · 天气：现在多少度、等会儿下不下雨、几点天黑——天空渐变卡片 + 太阳轨迹 + 逐时 / 详情 / 三天。
/// · 系统：一眼看负载，点一下直接去处理（任务管理器、电源、网络设置）。
/// · 日历：今天是几号、离考试还有几天；能翻月。专注：一键开始，能暂停、加时。
/// 刷新只改值不重建：圆环、文字原地动画，整页建一次后缓存。
/// </summary>
public sealed partial class IslandWindow
{
    private int _wxView;          // 0 逐时 · 1 详情 · 2 三天
    private int _calOffset;       // 日历翻到第几个月（0 = 本月）
    private int _timerPick = 25;  // 专注卡片上选中的时长
    private Border? _wxBody, _calHost, _timerCard;
    private readonly List<Border> _wxTabs = [];
    /// <summary>天气卡片上的图标：翻到组件页 / 鼠标移上卡片时播 6 秒动效，然后停住。</summary>
    private readonly List<WeatherIcon> _wxIcons = [];

    private void CollectIcons(DependencyObject root)
    {
        if (root is WeatherIcon wi) { _wxIcons.Add(wi); return; }
        foreach (var c in LogicalTreeHelper.GetChildren(root)) if (c is DependencyObject d) CollectIcons(d);
    }

    private void PlayWeather()
    {
        if (ReducedMotion || _page != Page.Widgets) return;
        _wxIcons.RemoveAll(i => !i.IsLoaded && i.Parent == null);
        foreach (var i in _wxIcons) i.Play(6);
    }
    private SunArc? _sun;
    private TextBlock? _sunHint;
    private (RingArc ring, TextBlock value, TextBlock sub)? _cpuT, _memT, _batT;
    private (TextBlock down, TextBlock up, Sparkline spark)? _netT;
    private (RingArc ring, TextBlock time, TextBlock label, TextBlock pause)? _timerT;
    private string _timerState = "";
    private static readonly Color TealC = Color.FromRgb(0x40, 0xC8, 0xE0);

    // 天空卡片上的文字：白色分三档
    private static readonly Brush W1 = Theme.Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush W2 = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush W3 = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush WFill = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush WFillHi = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush WRim = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush RainBlue = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x9F, 0xDB, 0xFF)));

    private static TextBlock T(string text, double size, FontWeight weight, Brush fg, bool display = false) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Foreground = fg,
        FontFamily = display ? Theme.Display : Theme.Text, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>组件卡片：圆角 22，底色上叠一层自上而下的柔光，取代平平的灰块。</summary>
    private static Border WCard(UIElement? child, Brush? bg = null)
    {
        var sheen = new LinearGradientBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90);
        sheen.Freeze();
        var inner = new Border { CornerRadius = new CornerRadius(Theme.CardRadius), Background = sheen, Padding = new Thickness(14, 12, 14, 12), Child = child };
        var outer = new Border { CornerRadius = new CornerRadius(Theme.CardRadius), Child = inner };
        Theme.Surface(outer);
        if (bg != null) outer.Background = bg;
        if (Theme.CardRim != null) inner.CornerRadius = new CornerRadius(Theme.CardRadius - 1);
        if (Theme.CardInner != null) { inner.BorderBrush = Theme.CardInner; inner.BorderThickness = new Thickness(1); inner.Background = null; }
        return outer;
    }

    private static void SetBody(Border card, UIElement? child) => ((Border)card.Child).Child = child;

    /// <summary>系统一行：CPU / 内存 / 电池 / 网络。</summary>
    private FrameworkElement SystemRow()
    {
        var sys = new Grid();
        for (var i = 0; i < 4; i++) sys.ColumnDefinitions.Add(new ColumnDefinition());
        var cpu = StatTile("CPU", "\uE950", (Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x0A, 0x84, 0xFF)), "taskmgr.exe", "打开任务管理器", out var c);
        var mem = StatTile("内存", "\uE964", (Color.FromRgb(0xFF, 0x7A, 0xC8), Color.FromRgb(0xBF, 0x5A, 0xF2)), "taskmgr.exe", "打开任务管理器", out var m);
        var bat = StatTile("电池", "\uE83F", (Color.FromRgb(0xB6, 0xF0, 0x6A), Color.FromRgb(0x30, 0xD1, 0x58)), "ms-settings:batterysaver", "电源与电池设置", out var b);
        _cpuT = c; _memT = m; _batT = b;
        var net = NetTile();
        Grid.SetColumn(mem, 1); Grid.SetColumn(bat, 2); Grid.SetColumn(net, 3);
        sys.Children.Add(cpu); sys.Children.Add(mem); sys.Children.Add(bat); sys.Children.Add(net);
        return sys;
    }

    /// <summary>每秒一次（只在组件页可见时）：改值，不重建。</summary>
    private void RefreshWidgets(bool sample = true)
    {
        if (sample) _stats.Sample();
        if (_cpuT is { } c) { c.ring.AnimateTo(_stats.Cpu); c.value.Text = $"{_stats.Cpu * 100:0}%"; }
        if (_memT is { } m) { m.ring.AnimateTo(_stats.MemFraction); m.value.Text = $"{_stats.MemFraction * 100:0}%"; m.sub.Text = $"{_stats.MemUsedGb:0}G 已用"; }
        if (_batT is { } b)
        {
            if (_stats.Battery is int pct)
            {
                b.ring.Gradient = pct <= 20 && !_stats.Charging ? (Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x45, 0x3A)) : (Color.FromRgb(0xB6, 0xF0, 0x6A), Color.FromRgb(0x30, 0xD1, 0x58));
                b.ring.AnimateTo(pct / 100.0); b.value.Text = $"{pct}%"; b.sub.Text = _stats.Charging ? "充电中" : "电池";
            }
            else { b.ring.AnimateTo(1); b.value.Text = "AC"; b.sub.Text = "外接电源"; }
        }
        if (_netT is { } n)
        {
            n.down.Text = SystemStats.Speed(_stats.DownBps); n.up.Text = SystemStats.Speed(_stats.UpBps);
            if (sample) n.spark.Push(_stats.DownBps + _stats.UpBps);
        }
        UpdateTimerCard();
        if (DateTime.Now.Second == 0 || !sample) UpdateSun();
    }

    /// <summary>翻到组件页时：圆环从 0 长到当前值。</summary>
    private void EnterWidgets()
    {
        Devices?.Soon();
        _stats.Sample();
        void Rings()
        {
            if (_cpuT is { } c) c.ring.AnimateTo(_stats.Cpu, 700, 0);
            if (_memT is { } m) m.ring.AnimateTo(_stats.MemFraction, 760, 0);
            if (_batT is { } b) b.ring.AnimateTo(_stats.Battery is int p ? p / 100.0 : 1, 820, 0);
        }
        // 圆环等页面滑稳了再长：和翻页同时动会让整页位图缓存每帧失效
        foreach (var r in new[] { _cpuT?.ring, _memT?.ring, _batT?.ring }) r?.AnimateTo(0, 1, 0);
        _afterSettle = () => { if (_page == Page.Widgets) { Rings(); PlayWeather(); } };
        Kick(); // 保证会有一次“停稳”来触发它
        RefreshWidgets(sample: false);
    }

    // ───────────── 天气 ─────────────

    private FrameworkElement WeatherCard()
    {
        var w = _weather;
        var x = _wx;
        var now = DateTime.Now;
        var night = Theme.IsNight(now);
        var rise = x != null ? HhMm.Parse(x.Sunrise, new(6, 30, 0)) : new(6, 30, 0);
        var set = x != null ? HhMm.Parse(x.Sunset, new(18, 30, 0)) : new(18, 30, 0);
        var golden = Math.Abs((now.TimeOfDay - set).TotalMinutes) < 50 || Math.Abs((now.TimeOfDay - rise).TotalMinutes) < 40;
        Brush sky = w == null ? Theme.Card : Sky.For(w.Code, night, golden);
        // 玻璃主题：天空色也变成半透明的染色玻璃，底下的模糊能透上来
        if (Theme.IsGlass && w != null) { sky = sky.CloneCurrentValue(); sky.Opacity = Theme.IsLiquid ? 0.5 : 0.74; sky.Freeze(); }
        var card = WCard(null, sky);
        ((Border)card.Child).Padding = new Thickness(18, 14, 18, 12);
        if (w != null) { card.BorderBrush = Theme.Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF))); card.BorderThickness = new Thickness(1); ((Border)card.Child).CornerRadius = new CornerRadius(21); }
        var sp = new StackPanel();
        SetBody(card, sp);
        if (w == null)
        {
            var ic = new WeatherIcon { Code = 2, Width = 34, Height = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 6), Opacity = 0.55 };
            sp.Children.Add(ic);
            var t = T("正在获取天气", 12.5, FontWeights.Medium, Theme.Secondary); t.Margin = new Thickness(0, 0, 0, 20); t.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(t);
            return card;
        }

        // 上半：地点 + 大温度 + 图标 · 太阳轨迹
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var place = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        var cities = App.WeatherCityList;
        place.Children.Add(T(App.WeatherLabel.Length > 0 ? App.WeatherLabel : "本地", 14, FontWeights.SemiBold, W1));
        var loc = Glyph(cities.Current == null ? "\uE707" : "\uE76C", 10, W2); loc.Margin = new Thickness(4, 2, 0, 0);
        place.Children.Add(loc);
        if (cities.Saved.Count > 0)
        {
            var dots = T($"{cities.Active + 2}/{cities.Saved.Count + 1}", 9.5, FontWeights.SemiBold, W3); dots.Margin = new Thickness(5, 3, 0, 0);
            place.Children.Add(dots);
        }
        place.ToolTip = cities.Saved.Count > 0 ? "点一下换下一个城市，右键管理城市" : "右键添加城市";
        // 和岛仔一样能在岛上直接切城市：左键轮换（自动定位 → 已存城市…），右键打开城市管理
        place.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var list = App.WeatherCityList;
            if (list.Saved.Count == 0) { (Application.Current as App)?.OpenWeatherCities(); return; }
            var next = list.Active + 1 >= list.Saved.Count ? -1 : list.Active + 1;
            (Application.Current as App)?.SwitchWeatherCity(next);
        };
        place.MouseRightButtonUp += (_, e) => { e.Handled = true; (Application.Current as App)?.OpenWeatherCities(); };
        left.Children.Add(place);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-2, -4, 0, 0) };
        line.Children.Add(T($"{w.Temperature:0}°", 46, FontWeights.Light, W1, display: true));
        line.Children.Add(new WeatherIcon { Code = w.Code, Night = night, Width = 40, Height = 40, Margin = new Thickness(8, 8, 0, 0) });
        left.Children.Add(line);
        var feels = Math.Abs(w.FeelsLike - w.Temperature) >= 2 ? $" · 体感 {w.FeelsLike:0}°" : "";
        var d1 = T($"{WeatherParser.Describe(w.Code)}{feels}", 12.5, FontWeights.SemiBold, W1); d1.Margin = new Thickness(1, -4, 0, 0);
        var d2 = T($"最高 {w.High:0}°  最低 {w.Low:0}°", 11.5, FontWeights.Medium, W2); d2.Margin = new Thickness(1, 1, 0, 0);
        left.Children.Add(d1); left.Children.Add(d2);
        top.Children.Add(left);

        if (x != null)
        {
            var right = new Grid { Margin = new Thickness(22, 4, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _sun = new SunArc { Rise = rise, Set = set, Dim = W3 };
            right.Children.Add(_sun);
            var labels = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            var r1 = T($"日出 {x.Sunrise}", 10.5, FontWeights.Medium, W3);
            var r2 = T($"日落 {x.Sunset}", 10.5, FontWeights.Medium, W3); r2.HorizontalAlignment = HorizontalAlignment.Right;
            _sunHint = T("", 11, FontWeights.SemiBold, W1); _sunHint.HorizontalAlignment = HorizontalAlignment.Center;
            labels.Children.Add(r1); labels.Children.Add(r2); labels.Children.Add(_sunHint);
            Grid.SetRow(labels, 1);
            right.Children.Add(labels);
            Grid.SetColumn(right, 1);
            top.Children.Add(right);
            UpdateSun();
        }
        sp.Children.Add(top);
        card.MouseEnter += (_, _) => PlayWeather();
        if (x == null) { CollectIcons(sp); return card; }

        // 天气提醒横条（本地按预报推算，最多两条）
        foreach (var a in x.Alerts.Take(2))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ig = Glyph(a.Severe ? "\uE7BA" : "\uE946", 11, a.Severe ? Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40))) : W1); ig.Margin = new Thickness(0, 1, 6, 0);
            row.Children.Add(ig);
            row.Children.Add(T(a.Title, 11.5, FontWeights.SemiBold, W1));
            var body = T("  " + a.Body, 11, FontWeights.Medium, W2); body.TextTrimming = TextTrimming.CharacterEllipsis; body.MaxWidth = 250;
            row.Children.Add(body);
            sp.Children.Add(new Border { Background = WFill, BorderBrush = WRim, BorderThickness = new Thickness(0.6), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 10, 0, 0), Child = row });
        }

        // 视图切换：分段控件
        var bar = new Grid { Margin = new Thickness(0, 12, 0, 8) };
        // 连着两次没刷新上（超过 40 分钟）就标出来，免得把旧数据当现在（岛仔是变灰）
        var stale = (DateTime.Now - x.Fetched).TotalMinutes > 40;
        var upd = T(stale ? $"{x.Fetched:HH:mm} 的数据 · 暂时刷新不了" : $"{x.Fetched:HH:mm} 更新 · {x.Source}", 10.5, FontWeights.Medium, W3);
        if (stale) upd.Opacity = 0.7; upd.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(upd);
        var seg = new Border { Background = WFill, BorderBrush = WRim, BorderThickness = new Thickness(0.6), CornerRadius = new CornerRadius(10), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Right };
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        seg.Child = tabs;
        string[] names = ["逐时", "详情", x.Days.Count > 3 ? $"{Math.Min(7, x.Days.Count)} 天" : "三天"];
        for (var i = 0; i < names.Length; i++)
        {
            var idx = i;
            var hit = new Border { Padding = new Thickness(11, 3, 11, 3), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = T(names[i], 11.5, FontWeights.SemiBold, W2) };
            hit.MouseLeftButtonUp += (_, e) => { SetWxView(idx); e.Handled = true; };
            _wxTabs.Add(hit);
            tabs.Children.Add(hit);
        }
        bar.Children.Add(seg);
        sp.Children.Add(bar);

        _wxBody = new Border { Height = 78, Child = WxView(_wxView) };
        sp.Children.Add(_wxBody);
        PaintTabs();
        CollectIcons(sp);
        return card;
    }

    private void PaintTabs()
    {
        for (var i = 0; i < _wxTabs.Count; i++)
        {
            _wxTabs[i].Background = i == _wxView ? WFillHi : Brushes.Transparent;
            if (_wxTabs[i].Child is TextBlock t) t.Foreground = i == _wxView ? W1 : W3;
        }
    }

    private void SetWxView(int v)
    {
        if (v == _wxView || _wxBody == null) return;
        var dir = v > _wxView ? 1 : -1;
        _wxView = v;
        PaintTabs();
        var next = WxView(v);
        var tt = new TranslateTransform(dir * 18, 0);
        next.RenderTransform = tt;
        next.Opacity = 0;
        _wxBody.Child = next;
        CollectIcons(next);
        if (_wxIcons.Any(i => i.Playing)) PlayWeather();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * 18, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
    }

    private static FrameworkElement SkyCell(string label, int code, string temp, int rain, bool night, bool now)
    {
        var c = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 3) };
        if (now) return new Border { Background = WFill, BorderBrush = WRim, BorderThickness = new Thickness(0.6), CornerRadius = new CornerRadius(12), Margin = new Thickness(1, 0, 3, 0), Child = SkyBody(c, label, code, temp, rain, night, now) };
        return SkyBody(c, label, code, temp, rain, night, now);
    }

    private static FrameworkElement SkyBody(StackPanel c, string label, int code, string temp, int rain, bool night, bool now)
    {
        var l = T(label, 11, now ? FontWeights.Bold : FontWeights.Medium, now ? W1 : W2); l.HorizontalAlignment = HorizontalAlignment.Center;
        c.Children.Add(l);
        c.Children.Add(new WeatherIcon { Code = code, Night = night, Width = 24, Height = 24, Margin = new Thickness(0, 4, 0, 2) });
        var r = T(rain >= 30 ? $"{rain}%" : " ", 9.5, FontWeights.Bold, RainBlue); r.HorizontalAlignment = HorizontalAlignment.Center; r.Margin = new Thickness(0, -2, 0, 0);
        c.Children.Add(r);
        var t = T(temp, 13, FontWeights.SemiBold, W1); t.HorizontalAlignment = HorizontalAlignment.Center;
        c.Children.Add(t);
        return c;
    }

    private FrameworkElement WxView(int v)
    {
        var x = _wx!;
        var w = x.Now;
        switch (v)
        {
            case 1:
            {
                var g = new UniformGrid { Columns = 4, Rows = 2 };
                string Uv(double u) => u switch { < 3 => "弱", < 6 => "中等", < 8 => "强", _ => "很强" };
                void Add(string glyph, string label, string value)
                {
                    var b = new Border { Background = WFill, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3, 6, 3), BorderBrush = WRim, BorderThickness = new Thickness(0.6) };
                    var c = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var head = new StackPanel { Orientation = Orientation.Horizontal };
                    var ig = Glyph(glyph, 8.5, W3); ig.Margin = new Thickness(0, 1, 3, 0);
                    head.Children.Add(ig);
                    var l = T(label, 9, FontWeights.SemiBold, W3);
                    head.Children.Add(l);
                    c.Children.Add(head);
                    var vt = T(value, 12, FontWeights.SemiBold, W1); vt.Margin = new Thickness(0, -1, 0, 0); c.Children.Add(vt);
                    b.Child = c;
                    g.Children.Add(b);
                }
                Add("\uE945", "湿度", $"{w.Humidity:0}%");
                Add("\uE9F7", "风", $"{x.WindDir} {w.WindSpeed:0}km/h");
                Add("\uE9E9", "降水概率", $"{x.RainChance}%");
                Add("\uE706", "紫外线", $"{x.Uv:0} {Uv(x.Uv)}");
                Add("\uE7B3", "能见度", $"{x.Visibility:0} km");
                Add("\uEC48", "气压", x.Pressure > 0 ? $"{x.Pressure:0} hPa" : "—");
                Add("\uE9CA", "降水量", $"{w.Precipitation:0.#} mm");
                Add("\uE708", "月相", x.MoonPhase.Length > 0 ? x.MoonPhase : "—");
                return g;
            }
            case 2:
            {
                if (x.Days.Count > 3)
                {
                    // 一周：竖排小格，和逐时一样高（星期 · 图标 · 降水 · 最高 / 最低）
                    var week = new UniformGrid { Rows = 1 };
                    var wd = x.Days.Take(7).ToList();
                    for (var i = 0; i < wd.Count; i++)
                    {
                        var d = wd[i];
                        var c = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1) };
                        var l = T(i == 0 ? "今天" : "周" + "日一二三四五六"[(int)d.Date.DayOfWeek], 11, i == 0 ? FontWeights.Bold : FontWeights.Medium, i == 0 ? W1 : W2); l.HorizontalAlignment = HorizontalAlignment.Center;
                        c.Children.Add(l);
                        c.Children.Add(new WeatherIcon { Code = d.Code, Width = 20, Height = 20, Margin = new Thickness(0, 2, 0, 0) });
                        var r = T(d.Rain >= 30 ? $"{d.Rain}%" : " ", 9, FontWeights.Bold, RainBlue); r.HorizontalAlignment = HorizontalAlignment.Center; r.Margin = new Thickness(0, -1, 0, -1);
                        c.Children.Add(r);
                        var hi = T($"{d.High:0}°", 12, FontWeights.SemiBold, W1); hi.HorizontalAlignment = HorizontalAlignment.Center;
                        var lo = T($"{d.Low:0}°", 10.5, FontWeights.Medium, W3); lo.HorizontalAlignment = HorizontalAlignment.Center; lo.Margin = new Thickness(0, -2, 0, 0);
                        c.Children.Add(hi); c.Children.Add(lo);
                        week.Children.Add(c);
                    }
                    return week;
                }
                var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
                var days = x.Days.Take(3).ToList();
                if (days.Count == 0) return sp;
                var min = days.Min(d => d.Low); var max = days.Max(d => d.High);
                for (var i = 0; i < days.Count; i++)
                {
                    var d = days[i];
                    var r = new Grid { Height = 25 };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    r.ColumnDefinitions.Add(new ColumnDefinition());
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                    var n = T(i == 0 ? "今天" : i == 1 ? "明天" : "周" + "日一二三四五六"[(int)d.Date.DayOfWeek], 12.5, FontWeights.SemiBold, W1);
                    n.VerticalAlignment = VerticalAlignment.Center;
                    var ic = new WeatherIcon { Code = d.Code, Width = 20, Height = 20 }; Grid.SetColumn(ic, 1);
                    var rain = T(d.Rain >= 30 ? $"{d.Rain}%" : "", 10.5, FontWeights.Bold, RainBlue); rain.VerticalAlignment = VerticalAlignment.Center; rain.Margin = new Thickness(4, 0, 0, 0); Grid.SetColumn(rain, 2);
                    var lo = T($"{d.Low:0}°", 12.5, FontWeights.Medium, W3); lo.VerticalAlignment = VerticalAlignment.Center; lo.HorizontalAlignment = HorizontalAlignment.Right; lo.Margin = new Thickness(0, 0, 9, 0); Grid.SetColumn(lo, 3);
                    var tb = new TempBar { Min = min, Max = max, Low = d.Low, High = d.High, Track = WFill, Height = 5, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(tb, 4);
                    var hi = T($"{d.High:0}°", 12.5, FontWeights.SemiBold, W1); hi.VerticalAlignment = VerticalAlignment.Center; hi.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(hi, 5);
                    r.Children.Add(n); r.Children.Add(ic); r.Children.Add(rain); r.Children.Add(lo); r.Children.Add(tb); r.Children.Add(hi);
                    sp.Children.Add(r);
                }
                return sp;
            }
            default:
            {
                var cells = new UniformGrid { Rows = 1 };
                cells.Children.Add(SkyCell("现在", w.Code, $"{w.Temperature:0}°", x.RainChance, Theme.IsNight(DateTime.Now), true));
                // 逐小时数据（Open-Meteo）隔一小时取一格，7 格看到 14 小时；wttr.in 本来就是 3 小时一格
                var step = x.Hours.Count > 1 && (x.Hours[1].At - x.Hours[0].At).TotalMinutes <= 60 ? 2 : 1;
                foreach (var h in x.Hours.Where((_, i) => i % step == 0).Take(7))
                    cells.Children.Add(SkyCell(h.At.Hour == 0 ? "明天" : h.At.ToString("H时"), h.Code, $"{h.Temp:0}°", h.Rain, Theme.IsNight(h.At), false));
                return cells;
            }
        }
    }

    private void UpdateSun()
    {
        if (_sun == null || _sunHint == null) return;
        _sun.InvalidateVisual();
        var now = DateTime.Now.TimeOfDay;
        string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}小时{t.Minutes}分" : $"{Math.Max(1, t.Minutes)}分钟";
        if (now < _sun.Rise) _sunHint.Text = $"{Dur(_sun.Rise - now)}后日出";
        else if (now < _sun.Set) _sunHint.Text = $"{Dur(_sun.Set - now)}后日落";
        else _sunHint.Text = $"{Dur(_sun.Rise + TimeSpan.FromDays(1) - now)}后日出";
    }

    // ───────────── 系统 ─────────────

    private Border Tappable(string target, string tip)
    {
        var card = WCard(null);
        ((Border)card.Child).Padding = new Thickness(0);
        ((Border)card.Child).CornerRadius = new CornerRadius(18);
        card.CornerRadius = new CornerRadius(18);
        card.Height = 64; card.Margin = new Thickness(0, 0, 8, 0); card.Cursor = Cursors.Hand; card.ToolTip = tip; card.ClipToBounds = true;
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale; card.RenderTransformOrigin = new Point(0.5, 0.5);
        void Press(double to) { var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 90 : 280)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } }; scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a); }
        card.MouseEnter += (_, _) => card.Background = Theme.CardHover;
        card.MouseLeave += (_, _) => { card.Background = Theme.Card; Press(1); };
        card.MouseLeftButtonDown += (_, e) => { Press(0.94); e.Handled = true; };
        card.MouseLeftButtonUp += (_, e) =>
        {
            Press(1);
            e.Handled = true;
            try { Launcher.UnlockForeground(); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); _pinned = false; SetMode(DesiredRestMode()); }
            catch (Exception ex) { App.Log("打开 " + target + ": " + ex.Message); }
        };
        return card;
    }

    private Border StatTile(string label, string glyph, (Color, Color) grad, string target, string tip, out (RingArc, TextBlock, TextBlock) refs)
    {
        var card = Tappable(target, tip);
        var g = new Grid { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ringBox = new Grid { Width = 38, Height = 38 };
        var ring = new RingArc { Thickness = 4.5, Gradient = grad, Track = Theme.Fill };
        ringBox.Children.Add(ring);
        var ic = Glyph(glyph, 13, new SolidColorBrush(grad.Item2)); ic.HorizontalAlignment = HorizontalAlignment.Center; ic.VerticalAlignment = VerticalAlignment.Center;
        ringBox.Children.Add(ic);
        g.Children.Add(ringBox);
        var sp = new StackPanel { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var v = T("", 15, FontWeights.SemiBold, Theme.Primary, display: true);
        var s = T(label, 10.5, FontWeights.Medium, Theme.Tertiary);
        sp.Children.Add(v); sp.Children.Add(s);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        SetBody(card, g);
        refs = (ring, v, s);
        return card;
    }

    private Border NetTile()
    {
        var card = Tappable("ms-settings:network-status", "网络设置");
        card.Margin = new Thickness(0);
        var g = new Grid();
        var spark = new Sparkline { Color = TealC, Capacity = 40, VerticalAlignment = VerticalAlignment.Bottom, Height = 40 };
        g.Children.Add(spark);
        var sp = new StackPanel { Margin = new Thickness(12, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock Line(string glyph, Brush c)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            r.Children.Add(Glyph(glyph, 10, c));
            var t = T("", 13, FontWeights.SemiBold, Theme.Primary, display: true); t.Margin = new Thickness(5, 0, 0, 0);
            r.Children.Add(t);
            sp.Children.Add(r);
            return t;
        }
        var down = Line("\uE74B", Teal);
        var up = Line("\uE74A", Theme.Orange);
        g.Children.Add(sp);
        SetBody(card, g);
        _netT = (down, up, spark);
        return card;
    }

    // ───────────── 日历 ─────────────

    /// <summary>
    /// 半宽日历，照 macOS 小号日历组件：上面一行「星期 + 农历 / 最近的倒计时」，下面整块月历。
    /// 原来左边还塞了一列大号日期，半宽里只剩 100 px 给月历，数字挤、最后一行被裁，所以去掉。
    /// </summary>
    private FrameworkElement CalendarView()
    {
        var right = new Border { Child = MonthGrid() };
        _calMonth = right;
        return right;
    }

    private Border? _calMonth;

    private FrameworkElement MonthGrid()
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1).AddMonths(_calOffset);
        var sp = new StackPanel();

        // 标题行：月份（红，本月）+ 副标题（今天的星期和农历，或最近的倒计时）+ 翻月
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 4) };
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var m = T(month.Year == today.Year ? $"{month.Month}月" : $"{month.Year}.{month.Month}", 13, FontWeights.Bold, _calOffset == 0 ? Theme.Red : Theme.Orange);
        if (_calOffset != 0) { m.Cursor = Cursors.Hand; m.ToolTip = "回到本月"; m.MouseLeftButtonUp += (_, e) => { FlipMonth(-_calOffset); e.Handled = true; }; }
        title.Children.Add(m);
        if (_calOffset == 0)
        {
            var next = (Countdown?.Items ?? []).Where(c => c.At.Date >= today).OrderBy(c => c.At).FirstOrDefault();
            TextBlock sub;
            if (next != null)
            {
                var days = (next.At.Date - today).Days;
                sub = T($"{(next.Short.Length > 0 ? next.Short : next.Name)} {(days == 0 ? "今天" : days + " 天")}", 11, FontWeights.SemiBold, Theme.Orange);
                sub.ToolTip = next.Name;
            }
            else
            {
                var lunar = Lunar.Festival(today) ?? Lunar.Text(today);
                sub = T("周" + "日一二三四五六"[(int)today.DayOfWeek] + (lunar.Length > 0 ? " · " + lunar : ""), 11, FontWeights.Medium, Theme.Tertiary);
            }
            sub.Margin = new Thickness(7, 1, 0, 0); sub.VerticalAlignment = VerticalAlignment.Center; sub.MaxWidth = 100;
            title.Children.Add(sub);
        }
        head.Children.Add(title);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (Calendar != null) nav.Children.Add(RoundIcon("\uE710", 20, () => new CalendarEventWindow(Calendar).Show(), tip: "新建日程（也可以双击某一天）"));
        nav.Children.Add(RoundIcon("\uE76B", 20, () => FlipMonth(-1)));
        nav.Children.Add(RoundIcon("\uE76C", 20, () => FlipMonth(1)));
        head.Children.Add(nav);
        sp.Children.Add(head);

        var lead = ((int)month.DayOfWeek + 6) % 7;
        var days0 = DateTime.DaysInMonth(month.Year, month.Month);
        var rows = (lead + days0 + 6) / 7;              // 只画需要的周数（4~6 行），不留空行
        const double RowH = 16.5;
        var grid = new UniformGrid { Columns = 7, Rows = rows + 1 };
        foreach (var d in "一二三四五六日")
        {
            var t = T(d.ToString(), 9.5, FontWeights.SemiBold, Theme.Tertiary); t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            t.Height = 14;
            grid.Children.Add(t);
        }
        var events = (Countdown?.Items ?? []).Where(c => c.At.Year == month.Year && c.At.Month == month.Month).GroupBy(c => c.At.Day).ToDictionary(g => g.Key, g => string.Join("、", g.Select(c => c.Name)));
        for (var i = 0; i < rows * 7; i++)
        {
            var d = i - lead + 1;
            if (d < 1 || d > days0) { grid.Children.Add(new Border { Height = RowH }); continue; }
            var date = new DateTime(month.Year, month.Month, d);
            var isToday = date == today;
            var weekend = i % 7 >= 5;
            var cell = new Grid { Height = RowH };
            if (isToday) cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 17, Height = 17, Fill = Theme.Red });
            cell.Children.Add(new TextBlock
            {
                Text = d.ToString(CultureInfo.InvariantCulture), FontFamily = Theme.Text, FontSize = 10.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium,
                Foreground = isToday ? Brushes.White : date < today ? Theme.Tertiary : weekend ? Theme.Secondary : Theme.Primary,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            var fest = Lunar.Festival(date);
            var lunarTip = (fest != null ? fest + " · " : "") + "农历" + Lunar.Text(date);
            cell.ToolTip = lunarTip; cell.Background = Brushes.Transparent;
            if (fest != null && !isToday)
                cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3, Height = 3, Fill = Theme.Red, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 4, 0) });
            if (events.TryGetValue(d, out var name))
            {
                cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Fill = isToday ? Brushes.White : Theme.Orange, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -1) });
                var left = (date - today).Days;
                cell.ToolTip = (left > 0 ? $"{name} · 还有 {left} 天" : left == 0 ? $"{name} · 今天" : name) + "\n" + lunarTip;
            }
            if (Calendar?.Events is { Count: > 0 } ce)
            {
                var dayEv = ce.Where(e => e.Start.Date == date).ToList();
                if (dayEv.Count > 0)
                {
                    var hasCd = events.ContainsKey(d);
                    cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 3.5, Height = 3.5, Fill = isToday ? Brushes.White : Theme.Blue, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(hasCd ? 9 : 0, 0, 0, -1) });
                    cell.ToolTip = string.Join("\n", dayEv.Take(5).Select(e => e.AllDay ? "全天  " + e.Title : $"{e.Start:HH:mm}  {e.Title}")) + (dayEv.Count > 5 ? $"\n…共 {dayEv.Count} 个" : "") + "\n" + cell.ToolTip;
                }
            }
            // 双击某一天：在这天新建本机日程
            cell.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.ClickCount != 2 || Calendar == null) return;
                ev.Handled = true;
                new CalendarEventWindow(Calendar, day: date).Show();
            };
            cell.ToolTip = (cell.ToolTip as string ?? lunarTip) + "\n双击添加日程";
            grid.Children.Add(cell);
        }
        sp.Children.Add(grid);
        return sp;
    }

    private void FlipMonth(int delta)
    {
        if (_calMonth == null || delta == 0) return;
        _calOffset += delta;
        var next = MonthGrid();
        var tt = new TranslateTransform(0, delta > 0 ? 12 : -12);
        next.RenderTransform = tt; next.Opacity = 0;
        _calMonth.Child = next;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private static Border RoundIcon(string glyph, double size, Action click, Brush? fg = null, string? tip = null, Brush? bg = null)
    {
        var rest = bg ?? Brushes.Transparent;
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = rest, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        var g = Glyph(glyph, size * 0.42, fg ?? Theme.Secondary); g.HorizontalAlignment = HorizontalAlignment.Center; g.VerticalAlignment = VerticalAlignment.Center;
        b.Child = g;
        b.MouseEnter += (_, _) => b.Background = Theme.Hover;
        b.MouseLeave += (_, _) => b.Background = rest;
        b.MouseLeftButtonDown += (_, e) => { b.Background = Theme.Press; e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) => { b.Background = Theme.Hover; click(); e.Handled = true; };
        return Pressable(b, 0.88);
    }

    // ───────────── 专注 ─────────────

    private static readonly (Color, Color) FocusGrad = (Color.FromRgb(0xFF, 0xC8, 0x57), Color.FromRgb(0xFF, 0x5E, 0x7A));
    private static readonly (Color, Color) RestGrad = (Color.FromRgb(0x7D, 0xF0, 0xC8), Color.FromRgb(0x30, 0xD1, 0x58));

    private void UpdateTimerCard()
    {
        if (_timerCard == null) return;
        var t = Timer;
        var state = t is not { Active: true } ? "idle" + _timerPick : t.Paused ? "paused" : "run";
        if (state != _timerState)
        {
            var first = _timerState.Length == 0;
            var crossKind = first || _timerState.StartsWith("idle") != state.StartsWith("idle");
            _timerState = state;
            var next = state.StartsWith("idle") ? TimerIdle() : TimerActive();
            if (crossKind && !first)
            {
                next.Opacity = 0;
                next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            SetBody(_timerCard, next);
        }
        if (!state.StartsWith("idle") && _timerT is { } r && t != null)
        {
            var left = t.Remaining;
            var total = Math.Max(1, t.Total.TotalSeconds);
            r.ring.AnimateTo(left.TotalSeconds / total, 900);
            r.time.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
            r.label.Text = t.Paused ? "已暂停" : (t.Task ?? t.Label + "中");
            r.ring.Gradient = t.Label == "休息" ? RestGrad : FocusGrad;
            r.ring.Opacity = t.Paused ? 0.45 : 1;
            r.pause.Text = t.Paused ? "\uE768" : "\uE769";
        }
    }

    /// <summary>空闲：一个完整的圆环当「开始」按钮，下面一排时长分段。</summary>
    private FrameworkElement TimerIdle()
    {
        _timerT = null;
        var rest = _timerPick == 5;
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var ringBox = new Grid { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "开始" };
        var ring = new RingArc { Thickness = 7, Gradient = rest ? RestGrad : FocusGrad, Track = Theme.Fill, Progress = 1, Opacity = 0.9 };
        ringBox.Children.Add(ring);
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var num = T(_timerPick.ToString(CultureInfo.InvariantCulture), 28, FontWeights.SemiBold, Theme.Primary, display: true); num.HorizontalAlignment = HorizontalAlignment.Center; num.Margin = new Thickness(0, -4, 0, -4);
        mid.Children.Add(num);
        var play = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        play.Children.Add(Glyph("\uE768", 9, Theme.Secondary));
        var pl = T(rest ? "休息" : "专注", 10.5, FontWeights.SemiBold, Theme.Secondary); pl.Margin = new Thickness(3, 0, 0, 0);
        play.Children.Add(pl);
        mid.Children.Add(play);
        ringBox.Children.Add(mid);
        var scale = new ScaleTransform(1, 1);
        ringBox.RenderTransform = scale; ringBox.RenderTransformOrigin = new Point(0.5, 0.5);
        void Press(double to) { var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 90 : 300)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } }; scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a); }
        ringBox.MouseEnter += (_, _) => Press(1.04);
        ringBox.MouseLeave += (_, _) => Press(1);
        ringBox.MouseLeftButtonDown += (_, e) => { Press(0.94); e.Handled = true; };
        ringBox.MouseLeftButtonUp += (_, e) => { e.Handled = true; Timer?.Start(_timerPick, rest ? "休息" : "专注"); UpdateTimerCard(); };
        g.Children.Add(ringBox);

        var seg = new Border { Background = Theme.Fill, CornerRadius = new CornerRadius(10), Padding = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (min, label) in new[] { (25, "25′"), (50, "50′"), (5, "休息") })
        {
            var on = min == _timerPick;
            var b = new Border { Padding = new Thickness(10, 3, 10, 3), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand, Background = on ? Theme.Hover : Brushes.Transparent, Child = T(label, 11, FontWeights.SemiBold, on ? Theme.Primary : Theme.Tertiary) };
            if (on) b.Background = Theme.SegOn;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; _timerPick = min; UpdateTimerCard(); };
            row.Children.Add(b);
        }
        seg.Child = row;
        Grid.SetRow(seg, 1);
        g.Children.Add(seg);
        return g;
    }

    private FrameworkElement TimerActive()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ringBox = new Grid { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new RingArc { Thickness = 7, Gradient = FocusGrad, Track = Theme.Fill, Progress = 1 };
        ringBox.Children.Add(ring);
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var time = T("", 22, FontWeights.SemiBold, Theme.Primary, display: true); time.HorizontalAlignment = HorizontalAlignment.Center;
        var label = T("", 10, FontWeights.SemiBold, Theme.Tertiary); label.HorizontalAlignment = HorizontalAlignment.Center; label.MaxWidth = 72;
        mid.Children.Add(time); mid.Children.Add(label);
        ringBox.Children.Add(mid);
        g.Children.Add(ringBox);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        var pause = RoundIcon("\uE769", 28, () => { if (Timer?.Paused == true) Timer.Resume(); else Timer?.Pause(); UpdateTimerCard(); }, Theme.Primary, "暂停 / 继续", Theme.Fill);
        var plus = RoundIcon("\uE710", 28, () => { Timer?.Extend(5); UpdateTimerCard(); }, Theme.Primary, "再加 5 分钟", Theme.Fill);
        var stop = RoundIcon("\uE711", 28, () => { Timer?.Cancel(); UpdateTimerCard(); }, Theme.Red, "结束", Theme.Fill);
        foreach (var b in new[] { pause, plus, stop }) b.Margin = new Thickness(5, 0, 5, 0);
        btns.Children.Add(pause); btns.Children.Add(plus); btns.Children.Add(stop);
        Grid.SetRow(btns, 1);
        g.Children.Add(btns);
        _timerT = (ring, time, label, (TextBlock)pause.Child);
        return g;
    }
}
