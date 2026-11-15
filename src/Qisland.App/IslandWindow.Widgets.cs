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
/// 组件页（重新设计）。从「为什么要看这一页」出发：
/// · 天气：出门前要知道的——现在多少度、等会儿下不下雨、几点天黑。所以是大温度 + 太阳轨迹 + 逐时 / 详情 / 三天三个视图。
/// · 系统：一眼看负载，点一下直接去处理（任务管理器、电源、网络设置）。
/// · 日历 + 专注：能翻月、能看到考试；专注可以暂停、加时，不用离开岛。
/// 刷新只改值不重建：圆环、文字原地动画，整页建一次后缓存。
/// </summary>
public sealed partial class IslandWindow
{
    private int _wxView;          // 0 逐时 · 1 详情 · 2 三天
    private int _calOffset;       // 日历翻到第几个月（0 = 本月）
    private Border? _wxBody, _calHost, _timerCard;
    private readonly List<TextBlock> _wxTabs = [];
    private SunArc? _sun;
    private TextBlock? _sunHint;
    private (RingArc ring, TextBlock value, TextBlock sub)? _cpuT, _memT, _batT;
    private (TextBlock down, TextBlock up, Sparkline spark)? _netT;
    private (RingArc ring, TextBlock time, TextBlock label, TextBlock pause)? _timerT;
    private string _timerState = "";
    private static readonly Color TealC = Color.FromRgb(0x40, 0xC8, 0xE0);

    private void BuildWidgets(StackPanel stack)
    {
        _wxTabs.Clear();
        stack.Children.Add(WeatherCard());

        var sys = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        for (var i = 0; i < 4; i++) sys.ColumnDefinitions.Add(new ColumnDefinition());
        var cpu = StatTile("CPU", Theme.Blue, "taskmgr.exe", "打开任务管理器", out var c);
        var mem = StatTile("内存", Purple, "taskmgr.exe", "打开任务管理器", out var m);
        var bat = StatTile("电池", Theme.Green, "ms-settings:batterysaver", "电源与电池设置", out var b);
        _cpuT = c; _memT = m; _batT = b;
        var net = NetTile();
        Grid.SetColumn(mem, 1); Grid.SetColumn(bat, 2); Grid.SetColumn(net, 3);
        sys.Children.Add(cpu); sys.Children.Add(mem); sys.Children.Add(bat); sys.Children.Add(net);
        stack.Children.Add(sys);

        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.12, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        _calHost = Card(CalendarView());
        _calHost.Height = 178;
        row.Children.Add(_calHost);
        _timerCard = Card(null);
        _timerCard.Height = 178;
        Grid.SetColumn(_timerCard, 2);
        row.Children.Add(_timerCard);
        stack.Children.Add(row);
        _timerState = "";
        RefreshWidgets(sample: false);
    }

    /// <summary>每秒一次（只在组件页可见时）：改值，不重建。</summary>
    private void RefreshWidgets(bool sample = true)
    {
        if (sample) _stats.Sample();
        if (_cpuT is { } c) { c.ring.AnimateTo(_stats.Cpu); c.value.Text = $"{_stats.Cpu * 100:0}%"; }
        if (_memT is { } m) { m.ring.AnimateTo(_stats.MemFraction); m.value.Text = $"{_stats.MemFraction * 100:0}%"; m.sub.Text = $"{_stats.MemUsedGb:0}/{_stats.MemTotalGb:0} GB"; }
        if (_batT is { } b)
        {
            if (_stats.Battery is int pct)
            {
                b.ring.Stroke = pct <= 20 && !_stats.Charging ? Theme.Red : Theme.Green;
                b.ring.AnimateTo(pct / 100.0); b.value.Text = $"{pct}%"; b.sub.Text = _stats.Charging ? "充电中" : "电池";
            }
            else { b.ring.AnimateTo(1); b.value.Text = "AC"; b.sub.Text = "外接电源"; }
        }
        if (_netT is { } n && sample) { n.down.Text = SystemStats.Speed(_stats.DownBps); n.up.Text = SystemStats.Speed(_stats.UpBps); n.spark.Push(_stats.DownBps + _stats.UpBps); }
        else if (_netT is { } n0) { n0.down.Text = SystemStats.Speed(_stats.DownBps); n0.up.Text = SystemStats.Speed(_stats.UpBps); }
        UpdateTimerCard();
        if (DateTime.Now.Second == 0 || !sample) UpdateSun();
    }

    /// <summary>翻到组件页时：圆环从 0 长到当前值，一眼看出"活的"。</summary>
    private void EnterWidgets()
    {
        _stats.Sample();
        if (_cpuT is { } c) c.ring.AnimateTo(_stats.Cpu, 700, 0);
        if (_memT is { } m) m.ring.AnimateTo(_stats.MemFraction, 760, 0);
        if (_batT is { } b) b.ring.AnimateTo(_stats.Battery is int p ? p / 100.0 : 1, 820, 0);
        RefreshWidgets(sample: false);
    }

    // ───────────── 天气 ─────────────

    private FrameworkElement WeatherCard()
    {
        var card = Card(null);
        card.Padding = new Thickness(16, 12, 16, 10);
        var sp = new StackPanel();
        card.Child = sp;
        var w = _weather;
        var x = _wx;
        if (w == null)
        {
            var t = Txt(13, FontWeights.Medium, Theme.Secondary); t.Text = "天气加载中…"; t.Margin = new Thickness(0, 30, 0, 30); t.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(t);
            return card;
        }
        var night = Theme.IsNight(DateTime.Now);

        // 上半：大温度 · 太阳轨迹
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var gl = WxGlyph(Theme.WeatherGlyph(w.Code, night), 26, Theme.Primary); gl.Margin = new Thickness(0, 6, 8, 0);
        line.Children.Add(gl);
        line.Children.Add(new TextBlock { Text = $"{w.Temperature:0}°", FontFamily = Theme.Display, FontSize = 38, FontWeight = FontWeights.Medium, Foreground = Theme.Primary });
        left.Children.Add(line);
        var d1 = Txt(12.5, FontWeights.Medium, Theme.Secondary); d1.Text = $"成都 · {WeatherParser.Describe(w.Code)} · 体感 {w.FeelsLike:0}°"; d1.Margin = new Thickness(1, -3, 0, 0);
        var d2 = Txt(11.5, FontWeights.Normal, Theme.Tertiary); d2.Text = $"最高 {w.High:0}°  最低 {w.Low:0}°"; d2.Margin = new Thickness(1, 1, 0, 0);
        left.Children.Add(d1); left.Children.Add(d2);
        top.Children.Add(left);

        if (x != null)
        {
            var right = new Grid { Margin = new Thickness(18, 0, 0, 0) };
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _sun = new SunArc { Rise = HhMm.Parse(x.Sunrise, new(6, 30, 0)), Set = HhMm.Parse(x.Sunset, new(18, 30, 0)), Dim = Theme.Tertiary };
            right.Children.Add(_sun);
            var labels = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            var rise = Txt(10.5, FontWeights.Medium, Theme.Tertiary); rise.Text = $"日出 {x.Sunrise}";
            var set = Txt(10.5, FontWeights.Medium, Theme.Tertiary); set.Text = $"日落 {x.Sunset}"; set.HorizontalAlignment = HorizontalAlignment.Right;
            _sunHint = Txt(10.5, FontWeights.SemiBold, Theme.Orange); _sunHint.HorizontalAlignment = HorizontalAlignment.Center;
            labels.Children.Add(rise); labels.Children.Add(set); labels.Children.Add(_sunHint);
            Grid.SetRow(labels, 1);
            right.Children.Add(labels);
            Grid.SetColumn(right, 1);
            top.Children.Add(right);
            UpdateSun();
        }
        sp.Children.Add(top);
        if (x == null) return card;

        // 视图切换：逐时 · 详情 · 三天
        var bar = new Grid { Margin = new Thickness(0, 10, 0, 6) };
        var upd = Txt(10.5, FontWeights.Normal, Theme.Tertiary); upd.Text = $"{x.Fetched:HH:mm} 更新 · wttr.in"; upd.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(upd);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string[] names = ["逐时", "详情", "三天"];
        for (var i = 0; i < names.Length; i++)
        {
            var idx = i;
            var t = Txt(11.5, FontWeights.SemiBold, Theme.Tertiary);
            t.Text = names[i];
            var hit = new Border { Padding = new Thickness(8, 2, 8, 2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = t };
            hit.MouseLeftButtonUp += (_, e) => { SetWxView(idx); e.Handled = true; };
            hit.MouseEnter += (_, _) => { if (_wxView != idx) hit.Background = Theme.Hover; };
            hit.MouseLeave += (_, _) => hit.Background = _wxView == idx ? Theme.Fill : Brushes.Transparent;
            _wxTabs.Add(t);
            tabs.Children.Add(hit);
        }
        bar.Children.Add(tabs);
        sp.Children.Add(bar);

        _wxBody = new Border { Height = 76, Child = WxView(_wxView) };
        sp.Children.Add(_wxBody);
        PaintTabs();
        return card;
    }

    private void PaintTabs()
    {
        for (var i = 0; i < _wxTabs.Count; i++)
        {
            _wxTabs[i].Foreground = i == _wxView ? Theme.Primary : Theme.Tertiary;
            if (_wxTabs[i].Parent is Border b) b.Background = i == _wxView ? Theme.Fill : Brushes.Transparent;
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
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dir * 18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
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
                string Uv(double u) => u switch { < 3 => "低", < 6 => "中等", < 8 => "高", _ => "很高" };
                void Add(string label, string value)
                {
                    var c = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                    var l = Txt(10.5, FontWeights.Normal, Theme.Tertiary); l.Text = label;
                    var t = Txt(12.5, FontWeights.SemiBold, Theme.Primary); t.Text = value;
                    c.Children.Add(l); c.Children.Add(t);
                    g.Children.Add(c);
                }
                Add("湿度", $"{w.Humidity:0}%");
                Add("风", $"{x.WindDir} {w.WindSpeed:0} km/h");
                Add("降水概率", $"{x.RainChance}%");
                Add("紫外线", $"{x.Uv:0} · {Uv(x.Uv)}");
                Add("能见度", $"{x.Visibility:0} km");
                Add("气压", x.Pressure > 0 ? $"{x.Pressure:0} hPa" : "—");
                Add("降水", $"{w.Precipitation:0.#} mm");
                Add("月相", x.MoonPhase.Length > 0 ? $"{x.MoonPhase} {x.MoonIllum}%" : "—");
                return g;
            }
            case 2:
            {
                var sp = new StackPanel();
                var days = x.Days.Take(3).ToList();
                if (days.Count == 0) return sp;
                var min = days.Min(d => d.Low); var max = days.Max(d => d.High);
                for (var i = 0; i < days.Count; i++)
                {
                    var d = days[i];
                    var r = new Grid { Height = 22 };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                    r.ColumnDefinitions.Add(new ColumnDefinition());
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
                    var n = Txt(12, FontWeights.SemiBold, Theme.Primary);
                    n.Text = i == 0 ? "今天" : i == 1 ? "明天" : "周" + "日一二三四五六"[(int)d.Date.DayOfWeek];
                    n.VerticalAlignment = VerticalAlignment.Center;
                    var g = WxGlyph(Theme.WeatherGlyph(d.Code, false), 14, Theme.Primary); Grid.SetColumn(g, 1);
                    var rain = Txt(10.5, FontWeights.SemiBold, Theme.Blue); rain.Text = d.Rain >= 30 ? $"{d.Rain}%" : ""; rain.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(rain, 2);
                    var lo = Txt(12, FontWeights.Medium, Theme.Tertiary); lo.Text = $"{d.Low:0}°"; lo.VerticalAlignment = VerticalAlignment.Center; lo.HorizontalAlignment = HorizontalAlignment.Right; lo.Margin = new Thickness(0, 0, 8, 0); Grid.SetColumn(lo, 3);
                    var tb = new TempBar { Min = min, Max = max, Low = d.Low, High = d.High, Track = Theme.Fill, Height = 5, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(tb, 4);
                    var hi = Txt(12, FontWeights.SemiBold, Theme.Primary); hi.Text = $"{d.High:0}°"; hi.VerticalAlignment = VerticalAlignment.Center; hi.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(hi, 5);
                    r.Children.Add(n); r.Children.Add(g); r.Children.Add(rain); r.Children.Add(lo); r.Children.Add(tb); r.Children.Add(hi);
                    sp.Children.Add(r);
                }
                return sp;
            }
            default:
            {
                var cells = new UniformGrid { Rows = 1 };
                cells.Children.Add(WeatherCell("现在", w.Code, $"{w.Temperature:0}°", x.RainChance >= 30 ? $"{x.RainChance}%" : null, Theme.IsNight(DateTime.Now)));
                foreach (var h in x.Hours.Take(7))
                    cells.Children.Add(WeatherCell(h.At.Hour == 0 ? "明天" : h.At.ToString("H时"), h.Code, $"{h.Temp:0}°", h.Rain >= 30 ? $"{h.Rain}%" : null, Theme.IsNight(h.At)));
                return cells;
            }
        }
    }

    private void UpdateSun()
    {
        if (_sun == null || _sunHint == null) return;
        _sun.InvalidateVisual();
        var now = DateTime.Now.TimeOfDay;
        string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分" : $"{Math.Max(1, t.Minutes)} 分钟";
        if (now < _sun.Rise) { _sunHint.Text = $"{Dur(_sun.Rise - now)}后日出"; _sunHint.Foreground = Theme.Secondary; }
        else if (now < _sun.Set) { _sunHint.Text = $"{Dur(_sun.Set - now)}后日落"; _sunHint.Foreground = Theme.Orange; }
        else { _sunHint.Text = $"{Dur(_sun.Rise + TimeSpan.FromDays(1) - now)}后日出"; _sunHint.Foreground = Theme.Secondary; }
    }

    // ───────────── 系统 ─────────────

    private Border Tappable(string target, string tip)
    {
        var card = new Border { Background = Theme.Fill, CornerRadius = new CornerRadius(16), Height = 60, Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, ToolTip = tip, ClipToBounds = true };
        var scale = new ScaleTransform(1, 1);
        card.RenderTransform = scale; card.RenderTransformOrigin = new Point(0.5, 0.5);
        void Press(double to) { var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to < 1 ? 90 : 260)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } }; scale.BeginAnimation(ScaleTransform.ScaleXProperty, a); scale.BeginAnimation(ScaleTransform.ScaleYProperty, a); }
        card.MouseEnter += (_, _) => card.Background = Theme.Hover;
        card.MouseLeave += (_, _) => { card.Background = Theme.Fill; Press(1); };
        card.MouseLeftButtonDown += (_, e) => { Press(0.95); e.Handled = true; };
        card.MouseLeftButtonUp += (_, e) =>
        {
            Press(1);
            e.Handled = true;
            try { Launcher.UnlockForeground(); Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); _pinned = false; SetMode(DesiredRestMode()); }
            catch (Exception ex) { App.Log("打开 " + target + ": " + ex.Message); }
        };
        return card;
    }

    private Border StatTile(string label, Brush color, string target, string tip, out (RingArc, TextBlock, TextBlock) refs)
    {
        var card = Tappable(target, tip);
        var g = new Grid { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var ring = new RingArc { Width = 32, Height = 32, Thickness = 4, Stroke = color, Track = Theme.Fill };
        g.Children.Add(ring);
        var sp = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock { FontFamily = Theme.Display, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary };
        var s = Txt(10.5, FontWeights.Normal, Theme.Tertiary); s.Text = label;
        sp.Children.Add(v); sp.Children.Add(s);
        Grid.SetColumn(sp, 1);
        g.Children.Add(sp);
        card.Child = g;
        refs = (ring, v, s);
        return card;
    }

    private Border NetTile()
    {
        var card = Tappable("ms-settings:network-status", "网络设置");
        card.Margin = new Thickness(0);
        var g = new Grid();
        var spark = new Sparkline { Color = TealC, Capacity = 40, VerticalAlignment = VerticalAlignment.Bottom, Height = 34 };
        g.Children.Add(spark);
        var sp = new StackPanel { Margin = new Thickness(11, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        TextBlock Line(string glyph, Brush c)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            r.Children.Add(Glyph(glyph, 10, c));
            var t = new TextBlock { FontFamily = Theme.Display, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary, Margin = new Thickness(5, 0, 0, 0) };
            r.Children.Add(t);
            sp.Children.Add(r);
            return t;
        }
        var down = Line("\uE74B", Teal);
        var up = Line("\uE74A", Theme.Orange);
        g.Children.Add(sp);
        card.Child = g;
        _netT = (down, up, spark);
        return card;
    }

    // ───────────── 日历 ─────────────

    private FrameworkElement CalendarView()
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1).AddMonths(_calOffset);
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, -2, 0, 0) };
        var m = Txt(13, FontWeights.SemiBold, _calOffset == 0 ? Theme.Primary : Theme.Orange);
        m.Text = month.Year == today.Year ? $"{month.Month}月" : $"{month.Year}年{month.Month}月";
        m.VerticalAlignment = VerticalAlignment.Center;
        if (_calOffset != 0) { m.Cursor = Cursors.Hand; m.ToolTip = "回到本月"; m.MouseLeftButtonUp += (_, e) => { FlipMonth(-_calOffset); e.Handled = true; }; }
        head.Children.Add(m);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        nav.Children.Add(RoundIcon("\uE76B", 22, () => FlipMonth(-1)));
        nav.Children.Add(RoundIcon("\uE76C", 22, () => FlipMonth(1)));
        head.Children.Add(nav);
        sp.Children.Add(head);

        var grid = new UniformGrid { Columns = 7, Rows = 7, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var d in "一二三四五六日")
        {
            var t = Txt(10, FontWeights.Medium, Theme.Tertiary); t.Text = d.ToString(); t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(t);
        }
        var lead = ((int)month.DayOfWeek + 6) % 7;
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var events = (Countdown?.Items ?? []).Where(c => c.At.Year == month.Year && c.At.Month == month.Month).GroupBy(c => c.At.Day).ToDictionary(g => g.Key, g => string.Join("、", g.Select(c => c.Name)));
        for (var i = 0; i < 42; i++)
        {
            var d = i - lead + 1;
            if (d < 1 || d > days) { grid.Children.Add(new Border()); continue; }
            var date = new DateTime(month.Year, month.Month, d);
            var isToday = date == today;
            var weekend = i % 7 >= 5;
            var cell = new Grid { Height = 18 };
            if (isToday) cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 19, Height = 19, Fill = Theme.Red });
            cell.Children.Add(new TextBlock
            {
                Text = d.ToString(CultureInfo.InvariantCulture), FontFamily = Theme.Text, FontSize = 10.5,
                FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                Foreground = isToday ? Brushes.White : date < today ? Theme.Tertiary : weekend ? Theme.Secondary : Theme.Primary,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            if (events.TryGetValue(d, out var name))
            {
                cell.Children.Add(new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = isToday ? Brushes.White : Theme.Orange, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, -2) });
                var left = (date - today).Days;
                cell.ToolTip = left > 0 ? $"{name} · 还有 {left} 天" : left == 0 ? $"{name} · 今天" : name;
                cell.Background = Brushes.Transparent;
            }
            grid.Children.Add(cell);
        }
        sp.Children.Add(grid);
        return sp;
    }

    private void FlipMonth(int delta)
    {
        if (_calHost == null || delta == 0) return;
        _calOffset += delta;
        var next = CalendarView();
        var tt = new TranslateTransform(0, delta > 0 ? 14 : -14);
        next.RenderTransform = tt; next.Opacity = 0;
        _calHost.Child = next;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private static Border RoundIcon(string glyph, double size, Action click, Brush? fg = null, string? tip = null)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip, Margin = new Thickness(2, 0, 0, 0) };
        var g = Glyph(glyph, size * 0.45, fg ?? Theme.Secondary); g.HorizontalAlignment = HorizontalAlignment.Center;
        b.Child = g;
        b.MouseEnter += (_, _) => b.Background = Theme.Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => { b.Background = Theme.Press; e.Handled = true; };
        b.MouseLeftButtonUp += (_, e) => { b.Background = Theme.Hover; click(); e.Handled = true; };
        return b;
    }

    // ───────────── 专注 ─────────────

    private void UpdateTimerCard()
    {
        if (_timerCard == null) return;
        var t = Timer;
        var state = t is not { Active: true } ? "idle" : t.Paused ? "paused" : "run";
        if (state != _timerState)
        {
            var first = _timerState.Length == 0;
            _timerState = state;
            var next = state == "idle" ? TimerIdle() : TimerActive();
            if (!first)
            {
                next.Opacity = 0;
                next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            _timerCard.Child = next;
        }
        if (state != "idle" && _timerT is { } r && t != null)
        {
            var left = t.Remaining;
            var total = Math.Max(1, t.Total.TotalSeconds);
            r.ring.AnimateTo(1 - left.TotalSeconds / total, 900);
            r.time.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
            r.label.Text = t.Paused ? "已暂停" : (t.Task ?? t.Label + "中");
            r.ring.Stroke = t.Paused ? Theme.Secondary : t.Label == "休息" ? Theme.Green : Theme.Orange;
            r.pause.Text = t.Paused ? "\uE768" : "\uE769";
        }
    }

    private FrameworkElement TimerIdle()
    {
        _timerT = null;
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Glyph("\uE916", 13, Theme.Orange));
        var title = Txt(13, FontWeights.SemiBold, Theme.Primary); title.Text = "专注计时"; title.Margin = new Thickness(6, 0, 0, 0);
        head.Children.Add(title);
        sp.Children.Add(head);
        var sub = Txt(11, FontWeights.Normal, Theme.Tertiary); sub.Text = "结束时岛会展开提醒你"; sub.Margin = new Thickness(0, 2, 0, 10);
        sp.Children.Add(sub);
        foreach (var (min, label, kind) in new[] { (25, "专注 25 分钟", "专注"), (50, "专注 50 分钟", "专注"), (5, "休息 5 分钟", "休息") })
            sp.Children.Add(PillButton(label, kind == "休息" ? Theme.Green : Theme.Blue, () => { Timer?.Start(min, kind); UpdateTimerCard(); }));
        return sp;
    }

    private FrameworkElement TimerActive()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition());
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ringBox = new Grid { Width = 100, Height = 100, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new RingArc { Thickness = 6, Stroke = Theme.Orange, Track = Theme.Fill };
        ringBox.Children.Add(ring);
        var mid = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var time = new TextBlock { FontFamily = Theme.Display, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = Theme.Primary, HorizontalAlignment = HorizontalAlignment.Center };
        var label = Txt(10.5, FontWeights.Medium, Theme.Tertiary); label.HorizontalAlignment = HorizontalAlignment.Center; label.MaxWidth = 80;
        mid.Children.Add(time); mid.Children.Add(label);
        ringBox.Children.Add(mid);
        g.Children.Add(ringBox);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var pause = RoundIcon("\uE769", 30, () => { if (Timer?.Paused == true) Timer.Resume(); else Timer?.Pause(); UpdateTimerCard(); }, Theme.Primary, "暂停 / 继续");
        var plus = RoundIcon("\uE710", 30, () => { Timer?.Extend(5); UpdateTimerCard(); }, Theme.Primary, "再加 5 分钟");
        var stop = RoundIcon("\uE71A", 30, () => { Timer?.Cancel(); UpdateTimerCard(); }, Theme.Red, "结束");
        foreach (var b in new[] { pause, plus, stop }) { b.Background = Theme.Fill; b.Margin = new Thickness(5, 0, 5, 0); var bb = b; bb.MouseLeave += (_, _) => bb.Background = Theme.Fill; }
        btns.Children.Add(pause); btns.Children.Add(plus); btns.Children.Add(stop);
        Grid.SetRow(btns, 1);
        g.Children.Add(btns);
        _timerT = (ring, time, label, (TextBlock)pause.Child);
        return g;
    }
}
