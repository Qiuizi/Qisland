using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>
/// 统一设置窗：左边分类，右边设置项。
/// 托盘里的开关 / 入口在这里原样出现（直接调用同一个托盘菜单项，所以同意弹窗、备份、改动账本这些流程和托盘完全一样），
/// 再加上托盘里不好做的：停靠和极简、健康数值、本机日程列表、当前快捷键。托盘菜单保留不变。
/// 思路参考小糖岛仔（MIT）的设置窗，WPF 重写。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly App _app;
    private readonly Forms.ContextMenuStrip _tray;
    private readonly StackPanel _nav = new() { Width = 150 };
    private readonly StackPanel _body = new();
    private readonly ScrollViewer _scroll;
    private readonly List<(string Name, string Glyph, Action<StackPanel> Build)> _cats;
    private int _cat;
    private static readonly MethodInfo? Opening = typeof(Forms.ToolStripDropDownItem).GetMethod("OnDropDownOpening", BindingFlags.NonPublic | BindingFlags.Instance);

    public SettingsWindow(App app, Forms.ContextMenuStrip tray, int category = 0)
    {
        _app = app; _tray = tray;
        _cats =
        [
            ("外观与停靠", "\uE771", Look),
            ("隐藏与穿透", "\uE890", HideAndThrough),
            ("岛上提示", "\uEA8F", p => Mirror(p, "岛上提示")),
            ("天气", "\uE706", Weather),
            ("健康", "\uEB51", Health),
            ("日历与待办", "\uE787", CalendarTodo),
            ("课程与作业", "\uE7BE", p => { Mirror(p, "导入课表"); Mirror(p, "作业 DDL"); }),
            ("翻译、AI 与快捷键", "\uE8C1", Ai),
            ("接入其他软件", "\uE71B", p => { Mirror(p, "Codex"); Mirror(p, "接入其他 AI 工具"); Mirror(p, "对其他软件的改动"); }),
            ("其他", "\uE712", Other),
        ];
        _cat = Math.Clamp(category, 0, _cats.Count - 1);
        var grid = new Grid { Height = 500 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_nav);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _body, Margin = new Thickness(18, 0, 0, 0), Padding = new Thickness(0, 0, 8, 0), FocusVisualStyle = null };
        Grid.SetColumn(_scroll, 1);
        grid.Children.Add(_scroll);
        DialogKit.Shell(this, "Qisland 设置", grid, 700);
        RenderNav();
        Render();
        Loaded += (_, _) => Activate();
        // 快捷键 / 托盘改了穿透锁定：正开着「隐藏与穿透」页就跟着刷新开关
        if (_app.Island is { } island)
        {
            Action sync = () => Dispatcher.BeginInvoke(() => { if (IsLoaded && _cats[_cat].Name == "隐藏与穿透") Render(); });
            island.ClickLockChanged += sync;
            Closed += (_, _) => island.ClickLockChanged -= sync;
        }
    }

    public void Show(int category) { _cat = Math.Clamp(category, 0, _cats.Count - 1); RenderNav(); Render(); _scroll.ScrollToTop(); Activate(); }

    private void RenderNav()
    {
        _nav.Children.Clear();
        for (var i = 0; i < _cats.Count; i++)
        {
            var idx = i;
            var sel = i == _cat;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = _cats[i].Glyph, FontFamily = DialogKit.Icons, FontSize = 13, Foreground = sel ? Brushes.White : DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, Width = 22 });
            var t = DialogKit.Text(_cats[i].Name, 13, sel ? FontWeights.SemiBold : FontWeights.Normal, sel ? Brushes.White : DialogKit.Fg); t.TextWrapping = TextWrapping.NoWrap;
            sp.Children.Add(t);
            var b = new Border { Child = sp, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2), Background = sel ? DialogKit.Accent : Brushes.Transparent, Cursor = Cursors.Hand };
            if (!sel) { b.MouseEnter += (_, _) => b.Background = DialogKit.Faint; b.MouseLeave += (_, _) => b.Background = Brushes.Transparent; }
            b.MouseLeftButtonUp += (_, _) => { _cat = idx; RenderNav(); Render(); _scroll.ScrollToTop(); };
            _nav.Children.Add(b);
        }
    }

    private void Render()
    {
        _body.Children.Clear();
        var h = DialogKit.Text(_cats[_cat].Name, 20, FontWeights.SemiBold, DialogKit.Fg); h.Margin = new Thickness(0, 0, 0, 12);
        _body.Children.Add(h);
        try { _cats[_cat].Build(_body); }
        catch (Exception ex) { _body.Children.Add(DialogKit.Text("这一页没显示出来：" + ex.Message, 12, FontWeights.Normal, DialogKit.Danger)); App.Log("设置页: " + ex); }
    }

    /// <summary>托盘点完之后状态可能是异步改的：稍等一下再刷新这一页。</summary>
    private void RenderSoon() => Dispatcher.BeginInvoke(async () => { await Task.Delay(350); if (IsLoaded) Render(); });

    private static TextBlock Section(string s) { var t = DialogKit.Text(s, 12, FontWeights.SemiBold, DialogKit.Sub); t.Margin = new Thickness(0, 14, 0, 6); return t; }

    private static Border Card(UIElement child) => new() { Background = DialogKit.Faint, CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12, 14, 4), Margin = new Thickness(0, 0, 0, 8), Child = child };

    // ───────────── 托盘镜像 ─────────────

    private Forms.ToolStripItem? Find(string prefix) =>
        _tray.Items.OfType<Forms.ToolStripItem>().FirstOrDefault(i => i.Text?.StartsWith(prefix, StringComparison.Ordinal) == true);

    private static void Refresh(Forms.ToolStripDropDownItem m)
    {
        try { Opening?.Invoke(m, [EventArgs.Empty]); } catch (Exception ex) { App.Log("设置页刷新菜单: " + ex.InnerException?.Message); }
    }

    /// <summary>把托盘里的一项（或一个子菜单）画成设置行：可勾选的是开关，单选组显示「当前」，其余是按钮。</summary>
    private void Mirror(StackPanel p, string prefix, bool header = true)
    {
        if (Find(prefix) is not Forms.ToolStripMenuItem mi) return;
        Refresh(mi);
        var inner = new StackPanel();
        if (mi.DropDownItems.Count > 0)
        {
            if (header) p.Children.Add(Section(mi.Text ?? ""));
            MirrorItems(inner, mi.DropDownItems);
        }
        else MirrorOne(inner, mi, false);
        p.Children.Add(Card(inner));
    }

    private void MirrorItems(StackPanel p, Forms.ToolStripItemCollection items)
    {
        var list = items.OfType<Forms.ToolStripItem>().ToList();
        // 单选组：分隔线隔开的一段里正好一项打勾、都不是开关（外观、动效、天气城市这种）
        var radioOf = new HashSet<Forms.ToolStripItem>();
        var seg = new List<Forms.ToolStripMenuItem>();
        void Close()
        {
            if (seg.Count > 1 && seg.Count(i => i.Checked) == 1 && seg.All(i => !i.CheckOnClick)) foreach (var i in seg) radioOf.Add(i);
            seg.Clear();
        }
        foreach (var it in list) { if (it is Forms.ToolStripSeparator) Close(); else if (it is Forms.ToolStripMenuItem m && m.DropDownItems.Count == 0) seg.Add(m); }
        Close();
        foreach (var it in list)
        {
            var radio = radioOf.Contains(it);
            if (it is Forms.ToolStripSeparator) { p.Children.Add(new Border { Height = 1, Background = DialogKit.Line, Margin = new Thickness(0, 2, 0, 10) }); continue; }
            if (it is not Forms.ToolStripMenuItem mi || !mi.Available) continue;
            if (mi.Text is "…" or "") continue;
            if (mi.DropDownItems.Count > 0)
            {
                Refresh(mi);
                p.Children.Add(DialogKit.Text(mi.Text ?? "", 12.5, FontWeights.SemiBold, DialogKit.Sub));
                var inner = new StackPanel { Margin = new Thickness(12, 6, 0, 0) };
                MirrorItems(inner, mi.DropDownItems);
                p.Children.Add(inner);
                continue;
            }
            MirrorOne(p, mi, radio);
        }
    }

    private void MirrorOne(StackPanel p, Forms.ToolStripMenuItem mi, bool radio)
    {
        var title = mi.Text ?? "";
        var desc = mi.ToolTipText;
        FrameworkElement control;
        if (mi.CheckOnClick)
            control = DialogKit.Switch(mi.Checked, _ => { mi.PerformClick(); RenderSoon(); });
        else if (radio)
            control = mi.Checked
                ? DialogKit.Text("✓ 当前", 12.5, FontWeights.SemiBold, DialogKit.Accent)
                : DialogKit.Button("选这个", () => { mi.PerformClick(); RenderSoon(); });
        else
        {
            var label = title.EndsWith("…") || title.EndsWith("...") ? "打开" : mi.Checked ? "已开 · 点击切换" : "执行";
            var b = DialogKit.Button(label, () => { mi.PerformClick(); RenderSoon(); });
            b.IsEnabled = mi.Enabled;
            control = b;
        }
        if (!mi.Enabled && control is not Border) control.IsEnabled = false;
        p.Children.Add(DialogKit.Row(title.TrimEnd('…'), desc, control));
    }

    // ───────────── 各页 ─────────────

    private void Look(StackPanel p)
    {
        var inner = new StackPanel();
        var docks = new[] { DockSide.Top, DockSide.Left, DockSide.Right };
        inner.Children.Add(DialogKit.Row("停靠位置", "侧边停靠时收起是竖向小胶囊，展开面板从屏幕边缘滑出；指针顶到那一侧边缘也能呼出",
            DialogKit.Segmented(["顶部", "左侧", "右侧"], Array.IndexOf(docks, IslandWindow.Dock), i => _app.SetLayout(docks[i], IslandWindow.Minimal))));
        inner.Children.Add(DialogKit.Row("极简模式", "没事时只留一小段线；指针靠近会变大，停一下展开；有通知、放音乐时照常显示",
            DialogKit.Switch(IslandWindow.Minimal, v => _app.SetLayout(IslandWindow.Dock, v))));
        inner.Children.Add(FeatureRow(Features.Clock12, "12 小时制时钟", "收起时显示 3:05 而不是 15:05"));
        inner.Children.Add(FeatureRow(Features.OledShift, "OLED 防烧屏", "收起静止时每 3 分钟挪 1–2 像素"));
        p.Children.Add(Card(inner));
        Mirror(p, "外观");
        Mirror(p, "动效");
    }

    // ───────────── 隐藏与穿透 ─────────────

    private void HideAndThrough(StackPanel p)
    {
        var w = _app.Island;
        p.Children.Add(Section("鼠标"));
        var mouse = new StackPanel();
        mouse.Children.Add(DialogKit.Row("锁定点击穿透", "整座岛不接鼠标：点击、悬停、滚轮都落到下面的窗口，通知和音乐照常显示。重启后自动解除",
            DialogKit.Switch(w?.ClickLocked == true, v => w?.SetClickLock(v, "设置"))));
        var hk = Features.On(Features.ClickLockHotkey);
        var hkDesc = !hk ? "关掉后只能用上面的开关或托盘菜单"
            : w?.ClickLockHotkeyActive == true ? "随时按一下锁定，再按一下解除；岛上会提示当前状态"
            : "这个组合被别的程序占用了，暂时只能用上面的开关或托盘菜单";
        mouse.Children.Add(DialogKit.Row($"快捷键 {IslandWindow.ClickLockHotkeyText}", hkDesc,
            DialogKit.Switch(hk, v => { Features.Set(Features.ClickLockHotkey, v); RenderSoon(); })));
        p.Children.Add(Card(mouse));

        p.Children.Add(Section("截图与录屏"));
        var cap = new StackPanel();
        cap.Children.Add(FeatureRow(Features.HideFromCapture, "截图录屏时隐藏", "截图、录屏、共享屏幕里看不到岛，自己屏幕上照常显示。想截岛本身时记得关掉"));
        p.Children.Add(Card(cap));

        p.Children.Add(Section("自动隐藏"));
        var (show, hide) = HideRules.Read();
        var rules = new StackPanel();
        rules.Children.Add(DialogKit.Row("全屏时让开", "游戏、全屏视频、演示时岛自动藏起来，一直开着；Ctrl+Alt+Q 可以临时叫回",
            DialogKit.Text("始终", 12.5, FontWeights.SemiBold, DialogKit.Sub)));
        rules.Children.Add(FeatureRow(Features.HideList, "按程序隐藏", "下面名单里的程序在前台时，岛藏起来，哪怕它没全屏"));
        rules.Children.Add(FeatureRow(Features.HideOnlyCovering, "只在挡住岛时才藏", "名单里的程序窗口盖到岛的位置才藏；挪开、缩小或最小化后岛回来"));
        rules.Children.Add(new Border { Height = 1, Background = DialogKit.Line, Margin = new Thickness(0, 2, 0, 12) });
        rules.Children.Add(NameList("隐藏名单", "在前台时藏起岛", hide, n => { var h = hide.ToList(); h.RemoveAll(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); Save(show, h); }, n => Save(show, hide.Append(n)), suggest: true));
        rules.Children.Add(NameList("全屏也显示", "这些程序全屏时岛照常显示，比如看视频时仍想看音量条和通知", show, n => { var s = show.ToList(); s.RemoveAll(x => x.Equals(n, StringComparison.OrdinalIgnoreCase)); Save(s, hide); }, n => Save(show.Append(n), hide), suggest: false));
        var file = DialogKit.Text("名单存在数据目录的 fullscreen.txt 里，手动改也会在一秒内生效。", 11.5, FontWeights.Normal, DialogKit.Sub);
        file.Margin = new Thickness(0, 0, 0, 10);
        rules.Children.Add(file);
        p.Children.Add(Card(rules));

        void Save(IEnumerable<string> s, IEnumerable<string> h) { if (HideRules.Write(s, h)) Render(); }
    }

    /// <summary>一组进程名：已加的是带 × 的圆角标签，下面一行输入框 +「添加」，可选「正在运行」的建议（点一下加入）。</summary>
    private FrameworkElement NameList(string title, string desc, List<string> names, Action<string> remove, Action<string> add, bool suggest)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(DialogKit.Text(title, 13.5, FontWeights.Medium, DialogKit.Fg));
        var d = DialogKit.Text(desc, 11.5, FontWeights.Normal, DialogKit.Sub); d.Margin = new Thickness(8, 2, 0, 0); d.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(d);
        sp.Children.Add(head);

        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        if (names.Count == 0)
        {
            var empty = DialogKit.Text("还没有程序", 12, FontWeights.Normal, DialogKit.Sub); empty.Margin = new Thickness(0, 2, 0, 8);
            wrap.Children.Add(empty);
        }
        foreach (var n in names) wrap.Children.Add(NameTag(n, () => remove(n)));
        sp.Children.Add(wrap);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var box = DialogKit.Box();
        box.ToolTip = "进程名，不带 .exe，比如 notepad；也可以粘贴 exe 的完整路径";
        var hint = DialogKit.Text("输入进程名，如 notepad", 12.5, FontWeights.Normal, DialogKit.Sub);
        hint.IsHitTestVisible = false; hint.Margin = new Thickness(9, 0, 0, 0); hint.VerticalAlignment = VerticalAlignment.Center;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var boxHost = new Grid(); boxHost.Children.Add(box); boxHost.Children.Add(hint);
        row.Children.Add(boxHost);
        void Commit()
        {
            var n = HideRules.Clean(box.Text);
            if (n.Length == 0) return;
            if (names.Contains(n, StringComparer.OrdinalIgnoreCase)) { box.Text = ""; return; }
            add(n);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Commit(); } };
        var btn = DialogKit.Button("添加", Commit);
        Grid.SetColumn(btn, 1);
        row.Children.Add(btn);
        row.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(row);

        if (suggest)
        {
            var running = HideRules.RunningApps().Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            if (running.Count > 0)
            {
                var sw = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                var l = DialogKit.Text("正在运行", 11.5, FontWeights.Normal, DialogKit.Sub); l.Margin = new Thickness(0, 0, 8, 6); l.VerticalAlignment = VerticalAlignment.Center;
                sw.Children.Add(l);
                foreach (var n in running) sw.Children.Add(SuggestTag(n, () => add(n)));
                sp.Children.Add(sw);
            }
        }
        return sp;
    }

    private static Border NameTag(string name, Action remove)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var t = DialogKit.Text(name, 12.5, FontWeights.SemiBold, DialogKit.Fg); t.TextWrapping = TextWrapping.NoWrap; t.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(t);
        var x = new TextBlock { Text = "\uE711", FontFamily = DialogKit.Icons, FontSize = 8.5, Foreground = DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) };
        var xHit = new Border { Child = x, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(2, 4, 2, 4), ToolTip = "移出名单" };
        xHit.MouseEnter += (_, _) => x.Foreground = DialogKit.Danger;
        xHit.MouseLeave += (_, _) => x.Foreground = DialogKit.Sub;
        xHit.MouseLeftButtonUp += (_, e) => { e.Handled = true; remove(); };
        sp.Children.Add(xHit);
        return new Border { Child = sp, Background = DialogKit.Line, CornerRadius = new CornerRadius(13), Padding = new Thickness(11, 3, 7, 3), Margin = new Thickness(0, 0, 6, 6) };
    }

    private static Border SuggestTag(string name, Action add)
    {
        var t = DialogKit.Text("+ " + name, 12, FontWeights.Medium, DialogKit.Sub); t.TextWrapping = TextWrapping.NoWrap;
        var b = new Border { Child = t, BorderBrush = DialogKit.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = $"把 {name} 加进隐藏名单" };
        b.MouseEnter += (_, _) => { b.Background = DialogKit.Faint; t.Foreground = DialogKit.Fg; };
        b.MouseLeave += (_, _) => { b.Background = Brushes.Transparent; t.Foreground = DialogKit.Sub; };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; add(); };
        return b;
    }

    // ───────────── 歌词校准（按播放器） ─────────────

    private void LyricCalibration(StackPanel p)
    {
        var lyr = _app.LyricsSvc;
        var inner = new StackPanel();
        var on = Features.On(Features.PlayerLyricOffset);
        inner.Children.Add(DialogKit.Row("按播放器校准歌词", "有的播放器报给系统的进度总是慢或快一截：在这里调，或按住 Ctrl 在岛上的歌词上滚滚轮，这个播放器放的每首歌都跟着走；和按歌的滚轮微调叠加。正数 = 歌词提前",
            DialogKit.Switch(on, v => { Features.Set(Features.PlayerLyricOffset, v); RenderSoon(); })));
        if (on && lyr != null)
        {
            var list = lyr.PlayerOffsets.ToDictionary(k => k.Key, k => k.Value);
            if (lyr.Player.Length > 0 && !list.ContainsKey(lyr.Player)) list[lyr.Player] = new LyricsService.PlayerOffset(lyr.PlayerName, 0);
            if (list.Count == 0)
            {
                var empty = DialogKit.Text("还没有播放器。放一首有歌词的歌，正在用的播放器会出现在这里。", 12, FontWeights.Normal, DialogKit.Sub);
                empty.Margin = new Thickness(0, 0, 0, 10);
                inner.Children.Add(empty);
            }
            else inner.Children.Add(new Border { Height = 1, Background = DialogKit.Line, Margin = new Thickness(0, 2, 0, 10) });
            foreach (var (key, po) in list.OrderByDescending(k => k.Key == lyr.Player).ThenBy(k => k.Value.Name, StringComparer.CurrentCulture))
                inner.Children.Add(DialogKit.Row(po.Name, key == lyr.Player ? "正在播放" : null, Stepper(lyr, key, po)));
        }
        p.Children.Add(Card(inner));
    }

    /// <summary>[−] +0.3 秒 [+]：每次 0.1 秒，就地改数字不重画整页；数字是 0 时灰掉。</summary>
    private static FrameworkElement Stepper(LyricsService lyr, string key, LyricsService.PlayerOffset po)
    {
        var v = po.Seconds;
        var val = DialogKit.Text("", 13, FontWeights.SemiBold, DialogKit.Fg);
        val.Width = 66; val.TextAlignment = TextAlignment.Center; val.VerticalAlignment = VerticalAlignment.Center; val.TextWrapping = TextWrapping.NoWrap;
        void Paint()
        {
            val.Text = Math.Abs(v) < 0.05 ? "0.0 秒" : $"{v:+0.0;-0.0} 秒";
            val.Foreground = Math.Abs(v) < 0.05 ? DialogKit.Sub : DialogKit.Fg;
        }
        Border Round(string glyph, string tip, double step)
        {
            var g = new TextBlock { Text = glyph, FontFamily = DialogKit.Icons, FontSize = 10, Foreground = DialogKit.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = DialogKit.Faint, Child = g, Cursor = Cursors.Hand, ToolTip = tip };
            b.MouseEnter += (_, _) => b.Background = DialogKit.Line;
            b.MouseLeave += (_, _) => b.Background = DialogKit.Faint;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                v = Math.Round(Math.Clamp(v + step, -5, 5), 1);
                lyr.SetPlayerOffset(key, po.Name, v);
                Paint();
            };
            return b;
        }
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(Round("\uE738", "推后 0.1 秒", -0.1));
        sp.Children.Add(val);
        sp.Children.Add(Round("\uE710", "提前 0.1 秒", 0.1));
        Paint();
        return sp;
    }

    private static Grid FeatureRow(string key, string title, string desc) =>
        DialogKit.Row(title, desc, DialogKit.Switch(Features.On(key), v => Features.Set(key, v)));

    private void Weather(StackPanel p)
    {
        var inner = new StackPanel();
        inner.Children.Add(FeatureRow(Features.WeatherAlerts, "天气提醒", "快下雨、雷阵雨、下雪、大风、高温、严寒、明天大降温；同一件事一天只提醒一次"));
        var list = App.WeatherCityList;
        var cities = list.Saved.Count == 0 ? "只用自动定位" : "自动定位 + " + string.Join("、", list.Saved.Select(c => c.Label));
        inner.Children.Add(DialogKit.Row("城市", cities + $"（最多 {OpenMeteo.MaxCities} 个；岛上天气卡片点地名切换）", DialogKit.Button("管理城市", () => _app.OpenWeatherCities())));
        inner.Children.Add(DialogKit.Row("立即刷新", "数据来自 Open-Meteo，平时 15 分钟刷新一次", DialogKit.Button("刷新", _app.RefreshWeatherNow)));
        p.Children.Add(Card(inner));
        Mirror(p, "天气");
    }

    private void Health(StackPanel p)
    {
        var h = App.Health;
        var inner = new StackPanel();
        inner.Children.Add(FeatureRow(Features.Water, "喝水提醒", "提醒时段内隔一段时间没记喝水就提醒一次；点提醒记一杯"));
        inner.Children.Add(FeatureRow(Features.Sit, "久坐提醒", "连续用电脑太久提醒起来活动；离开几分钟算休息过"));
        p.Children.Add(Card(inner));

        p.Children.Add(Section("数值"));
        var form = new StackPanel();
        TextBox Num(string title, string desc, int v, string unit)
        {
            var box = DialogKit.Box(v.ToString(System.Globalization.CultureInfo.InvariantCulture), 70);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(box);
            var u = DialogKit.Text(unit, 12, FontWeights.Normal, DialogKit.Sub); u.Margin = new Thickness(6, 6, 0, 0); u.Width = 36;
            sp.Children.Add(u);
            form.Children.Add(DialogKit.Row(title, desc, sp));
            return box;
        }
        var water = Num("喝水间隔", null!, h.WaterMinutes, "分钟");
        var serving = Num("一杯", null!, h.ServingMl, "ml");
        var goal = Num("每天目标", null!, h.GoalMl, "ml");
        var start = Num("提醒开始", "几点以后才提醒", h.StartHour, "点");
        var end = Num("提醒结束", "几点以后不再提醒", h.EndHour, "点");
        var sit = Num("久坐时长", null!, h.SitMinutes, "分钟");
        var lunch = h.LunchQuiet;
        form.Children.Add(DialogKit.Row("午休免打扰", "12:00–13:30 不提醒", DialogKit.Switch(lunch, v => lunch = v)));
        var msg = DialogKit.Text("", 12, FontWeights.SemiBold, DialogKit.Accent);
        int N(TextBox b, int fallback) => int.TryParse(b.Text.Trim(), out var n) ? n : fallback;
        var save = DialogKit.Button("保存", () =>
        {
            App.Health = h with
            {
                WaterMinutes = N(water, h.WaterMinutes), ServingMl = N(serving, h.ServingMl), GoalMl = N(goal, h.GoalMl),
                StartHour = N(start, h.StartHour), EndHour = N(end, h.EndHour), SitMinutes = N(sit, h.SitMinutes), LunchQuiet = lunch,
            };
            msg.Text = "已保存（超出范围的已自动调整）";
            RenderSoon();
        }, primary: true);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        form.Children.Add(save);
        form.Children.Add(msg);
        p.Children.Add(Card(form));
    }

    private void CalendarTodo(StackPanel p)
    {
        var cal = _app.CalendarService;
        p.Children.Add(Section("本机日程"));
        var inner = new StackPanel();
        if (cal == null) inner.Children.Add(DialogKit.Text("日历没启动", 12.5, FontWeights.Normal, DialogKit.Sub));
        else
        {
            var items = cal.Local.Items.Where(e => e.Weekly || e.End >= DateTime.Today).Take(30).ToList();
            if (items.Count == 0) inner.Children.Add(DialogKit.Text("还没有本机日程。也可以在岛上的月历里双击某一天添加。", 12.5, FontWeights.Normal, DialogKit.Sub));
            foreach (var e in items)
            {
                var ev = e;
                var when = e.AllDay ? $"{e.Start:M/d} 全天" : $"{e.Start:M/d HH:mm}–{e.End:HH:mm}";
                if (e.Weekly) when += " · 每周" + "日一二三四五六"[(int)e.Start.DayOfWeek];
                if (e.Location.Length > 0) when += " · " + e.Location;
                inner.Children.Add(DialogKit.Row(e.Title, when, DialogKit.Button("编辑", () => { var w = new CalendarEventWindow(cal, ev); w.Closed += (_, _) => RenderSoon(); w.Show(); })));
            }
            var add = DialogKit.Button("新建日程", () => { var w = new CalendarEventWindow(cal); w.Closed += (_, _) => RenderSoon(); w.Show(); }, primary: true);
            add.HorizontalAlignment = HorizontalAlignment.Left; add.Margin = new Thickness(0, 4, 0, 10);
            inner.Children.Add(add);
        }
        p.Children.Add(Card(inner));
        Mirror(p, "日历");
        p.Children.Add(Section("待办"));
        var todo = new StackPanel();
        todo.Children.Add(DialogKit.Text("在岛上的待办页悬停一条，点铃铛设提醒时间和每周重复；到点岛上提醒。也可以直接在 todo.md 里写：", 12.5, FontWeights.Normal, DialogKit.Fg));
        var ex = DialogKit.Text("- [ ] 交实验报告 ⏰ 2026-11-21 20:00 🔁 每周五", 12.5, FontWeights.SemiBold, DialogKit.Sub);
        ex.FontFamily = new FontFamily("Cascadia Mono, Consolas"); ex.Margin = new Thickness(0, 6, 0, 10);
        todo.Children.Add(ex);
        p.Children.Add(Card(todo));
        Mirror(p, "编辑待办", header: false);
    }

    private void Ai(StackPanel p)
    {
        var w = _app.Island;
        var inner = new StackPanel();
        inner.Children.Add(DialogKit.Row("划词翻译", "选中文字后按", DialogKit.Text(w?.ActiveTranslateHotkey ?? "未启用", 13, FontWeights.SemiBold, DialogKit.Fg)));
        inner.Children.Add(DialogKit.Row("问 AI", "选中文字后按", DialogKit.Text(w?.ActiveAskHotkey ?? "未启用", 13, FontWeights.SemiBold, DialogKit.Fg)));
        var note = DialogKit.Text("首选组合被别的程序占用时会自动换备用组合；在下面「翻译与 AI 设置」里可以换快捷键、填 API Key（Key 存在 Windows 凭据管理器里，不写日志）。", 11.5, FontWeights.Normal, DialogKit.Sub);
        note.Margin = new Thickness(0, 0, 0, 10);
        inner.Children.Add(note);
        p.Children.Add(Card(inner));
        Mirror(p, "翻译与 AI");
    }

    private void Other(StackPanel p)
    {
        Mirror(p, "显示歌词", header: false);
        LyricCalibration(p);
        foreach (var prefix in new[] { "通知里的验证码", "显示输入法", "接管音量键", "开机自启" }) Mirror(p, prefix, header: false);
        Mirror(p, "计时");
        foreach (var prefix in new[] { "编辑倒计时", "检查更新", "打开数据目录" }) Mirror(p, prefix, header: false);
    }
}
