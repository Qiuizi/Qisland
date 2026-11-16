using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

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
        new("todo", "待办", "\uE73A", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))), true),
        new("clip", "剪贴板", "\uE77F", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF))), true),
        new("calendar", "日历", "\uE787", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))), true),
        new("timer", "专注", "\uE823", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A))), true),
        new("system", "系统", "\uE950", Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58))), false),
    ];
    private static string WidgetFile => System.IO.Path.Combine(App.DataDir, "widgets.txt");
    private List<(string Id, bool On)>? _widgetList;
    private bool _widgetEdit;
    private const double WidgetViewMax = 440, HalfH = 168;
    private Border? _wView;
    private readonly TranslateTransform _wScrollTT = new();
    private double _wScroll, _wScrollMax;
    private DateTime _wScrolledAt = DateTime.MinValue;

    private List<(string Id, bool On)> Widgets()
    {
        if (_widgetList != null) return _widgetList;
        var list = new List<(string, bool)>();
        try
        {
            if (File.Exists(WidgetFile))
                foreach (var raw in File.ReadAllLines(WidgetFile))
                {
                    var l = raw.Trim();
                    if (l.Length == 0 || l.StartsWith('#')) continue;
                    var on = !l.StartsWith('-');
                    var id = l.TrimStart('-').Trim().ToLowerInvariant();
                    if (WidgetDefs.Any(d => d.Id == id) && list.All(x => x.Item1 != id)) list.Add((id, on));
                }
        }
        catch (Exception ex) { App.Log("读取 widgets.txt: " + ex.Message); }
        // 新版本加的组件、文件里漏掉的组件：补在最后，默认显示
        foreach (var d in WidgetDefs) if (list.All(x => x.Item1 != d.Id)) list.Add((d.Id, true));
        return _widgetList = list;
    }

    private void SaveWidgets()
    {
        try
        {
            var lines = new List<string> { "# Qisland 组件页：一行一个，从上到下就是显示顺序；前面加 - 表示隐藏", "# 可选：" + string.Join(" ", WidgetDefs.Select(d => d.Id)) };
            lines.AddRange(Widgets().Select(w => (w.On ? "" : "-") + w.Id));
            File.WriteAllLines(WidgetFile, lines);
        }
        catch (Exception ex) { App.Log("保存 widgets.txt: " + ex.Message); }
    }

    private static WidgetDef Def(string id) => WidgetDefs.First(d => d.Id == id);

    private void BuildWidgets(StackPanel stack)
    {
        _wxTabs.Clear();
        _cpuT = _memT = _batT = null; _netT = null; _calHost = null; _timerCard = null; _sun = null; _sunHint = null; _wxBody = null;
        _wView = null;
        if (_widgetEdit) { BuildWidgetEditor(stack); return; }

        var inner = new StackPanel();
        var on = Widgets().Where(w => w.On).Select(w => w.Id).ToList();
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
        if (_wScrollMax > 0)
        {
            _wView = new Border { Height = WidgetViewMax, ClipToBounds = true, Child = inner };
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
            case "calendar":
                _calHost = WCard(CalendarView()); _calHost.Height = HalfH; return _calHost;
            case "timer":
                _timerCard = WCard(null); _timerCard.Height = HalfH; return _timerCard;
            case "todo": return TodoWidget();
            default: return ClipWidget();
        }
    }

    /// <summary>小组件的标题行：图标 + 名字 + 右侧小字，点标题去对应的整页。</summary>
    private FrameworkElement WidgetHead(WidgetDef d, string right, Page? go)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
        var l = new StackPanel { Orientation = Orientation.Horizontal };
        l.Children.Add(Glyph(d.Glyph, 12, d.Tint));
        var t = Txt(12.5, FontWeights.SemiBold, Theme.Primary); t.Text = d.Name; t.Margin = new Thickness(6, 0, 0, 0); l.Children.Add(t);
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

    private Border TodoWidget()
    {
        var open = (Todo?.Items ?? []).Where(i => !i.Done).ToList();
        var card = WCard(null); card.Height = HalfH;
        var sp = new StackPanel();
        sp.Children.Add(WidgetHead(Def("todo"), open.Count > 0 ? $"{open.Count} 件" : "", Page.Todo));
        foreach (var it in open.Take(4))
        {
            var row = new Grid { Height = 25, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = it.Text };
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
            var empty = Txt(12, FontWeights.Normal, Theme.Tertiary); empty.Text = "没有待办"; empty.Margin = new Thickness(0, 18, 0, 6); empty.HorizontalAlignment = HorizontalAlignment.Center;
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
            var row = new Grid { Height = 25, Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = c.Text.Length > 300 ? c.Text[..300] + "…" : c.Text };
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
            row.MouseEnter += (_, _) => tx.Foreground = Theme.Blue;
            row.MouseLeave += (_, _) => tx.Foreground = Theme.Primary;
            row.MouseLeftButtonDown += (_, e) => e.Handled = true;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyBack(c); };
            sp.Children.Add(row);
        }
        if (_clips.Count == 0)
        {
            var empty = Txt(12, FontWeights.Normal, Theme.Tertiary); empty.Text = "复制点什么\n会出现在这里"; empty.TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(0, 22, 0, 0); empty.HorizontalAlignment = HorizontalAlignment.Center;
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

            var tile = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(8), Background = d.Tint, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = on ? 1 : 0.45 };
            var gl = Glyph(d.Glyph, 12, Brushes.White); gl.HorizontalAlignment = HorizontalAlignment.Center; gl.VerticalAlignment = VerticalAlignment.Center;
            tile.Child = gl;
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
        var anim = new DoubleAnimation(-_wScroll, TimeSpan.FromMilliseconds(Math.Abs(delta) >= 120 ? 260 : 90)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _wScrollTT.BeginAnimation(TranslateTransform.YProperty, anim);
        var mask = (LinearGradientBrush)_wView.OpacityMask;
        mask.GradientStops[2].Color = _wScroll >= _wScrollMax - 1 ? Colors.Black : Colors.Transparent;
        return true;
    }
}
