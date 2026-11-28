using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>
/// 5.0「布局与 DIY」页：收起胶囊编排（空闲 / 播放分开配，带实时预览）、展开组件拖拽排序、主题包、自定义快捷按钮。
/// 每一处改动都当场写盘并同步到岛上，不用重启。
/// </summary>
public sealed partial class SettingsWindow
{
    private CapsuleState _capState = CapsuleState.Idle;
    private int _quickEdit = -2;            // -2 = 没在编辑；-1 = 新增；>=0 = 编辑第几个
    private QuickAction? _quickDraft;
    private string? _quickError;
    private List<string> _themeNotes = [];
    private bool _themeNoteBad;

    private void Diy(StackPanel p)
    {
        var intro = DialogKit.Text("不写代码也能把岛改成自己的样子：拖动排序、换主题、加快捷按钮。改完立刻生效。", 12, FontWeights.Normal, DialogKit.Sub);
        intro.Margin = new Thickness(0, -4, 0, 6);
        p.Children.Add(intro);
        CapsuleEditor(p);
        WidgetEditor(p);
        ThemeEditor(p);
        QuickEditor(p);
    }

    // ───────────── 公用小件 ─────────────

    private static TextBlock Small(string s, Brush? fg = null) { var t = DialogKit.Text(s, 11.5, FontWeights.Normal, fg ?? DialogKit.Sub); return t; }

    /// <summary>圆形小图标按钮（上移 / 下移 / 删除）。</summary>
    private static Border IconBtn(string glyph, string tip, Action click, bool enabled = true, Brush? fg = null)
    {
        var t = new TextBlock { Text = glyph, FontFamily = DialogKit.Icons, FontSize = 11, Foreground = fg ?? DialogKit.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brushes.Transparent, Child = t, ToolTip = tip, Cursor = enabled ? Cursors.Hand : Cursors.Arrow, Opacity = enabled ? 1 : 0.28, Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        if (enabled)
        {
            b.MouseEnter += (_, _) => b.Background = DialogKit.Faint;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        }
        return b;
    }

    /// <summary>左 / 中 / 右 小号分段选择。</summary>
    private static Border SlotPicker(CapsuleSlot cur, bool locked, Action<CapsuleSlot> changed)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new Border { Background = DialogKit.Faint, CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = sp, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        foreach (var (slot, name) in new[] { (CapsuleSlot.Left, "左"), (CapsuleSlot.Center, "中"), (CapsuleSlot.Right, "右") })
        {
            var on = slot == cur;
            var t = DialogKit.Text(name, 11.5, FontWeights.SemiBold, on ? Brushes.White : DialogKit.Fg); t.TextWrapping = TextWrapping.NoWrap; t.HorizontalAlignment = HorizontalAlignment.Center;
            var c = new Border { Width = 28, Height = 22, CornerRadius = new CornerRadius(6), Background = on ? DialogKit.Accent : Brushes.Transparent, Child = t };
            t.VerticalAlignment = VerticalAlignment.Center;
            if (!locked && !on)
            {
                c.Cursor = Cursors.Hand;
                var s = slot;
                c.MouseEnter += (_, _) => c.Background = DialogKit.Line;
                c.MouseLeave += (_, _) => c.Background = Brushes.Transparent;
                c.MouseLeftButtonUp += (_, e) => { e.Handled = true; changed(s); };
            }
            sp.Children.Add(c);
        }
        if (locked) { host.Opacity = 0.55; host.ToolTip = "歌名 / 歌词固定在中间，两边的项围着它排"; }
        return host;
    }

    private static Border HeaderRow(string title, string? desc, UIElement? right)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(DialogKit.Text(title, 13.5, FontWeights.SemiBold, DialogKit.Fg));
        if (desc != null) { var d = Small(desc); d.Margin = new Thickness(0, 2, 0, 0); left.Children.Add(d); }
        g.Children.Add(left);
        if (right != null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
        return new Border { Child = g };
    }

    // ───────────── 一、收起胶囊编排 ─────────────

    private void CapsuleEditor(StackPanel p)
    {
        p.Children.Add(Section("收起胶囊"));
        var box = new StackPanel();
        var layout = CapsuleStore.Current.Clone();
        var st = _capState;
        var tabs = DialogKit.Segmented(["空闲时", "放音乐时"], st == CapsuleState.Idle ? 0 : 1, i => { _capState = i == 0 ? CapsuleState.Idle : CapsuleState.Playing; Render(); });
        var reset = DialogKit.Button("恢复默认", () =>
        {
            var l = CapsuleStore.Current.Clone();
            l.For(st).Clear(); l.For(st).AddRange(CapsuleLayout.DefaultFor(st));
            CapsuleStore.Save(l); Render();
        });
        reset.IsEnabled = !layout.IsDefault(st);
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(tabs);
        Grid.SetColumn(reset, 1); head.Children.Add(reset);
        box.Children.Add(head);

        // 实时预览：一块像桌面的小舞台，上面一颗按当前主题画的胶囊
        box.Children.Add(CapsuleStage(layout, st));
        var cap = Small(st == CapsuleState.Idle ? "左右两槽贴边，中间一槽居中；改动立刻同步到岛上" : "歌名 / 歌词固定在中间、会伸缩；两边放封面、频谱、时间这类小东西");
        cap.HorizontalAlignment = HorizontalAlignment.Center; cap.Margin = new Thickness(0, 6, 0, 12);
        box.Children.Add(cap);

        var list = new DragList();
        var entries = layout.For(st);
        var catalog = CapsuleLayout.Catalog(st);
        for (var i = 0; i < entries.Count; i++)
        {
            var idx = i;
            var e = entries[i];
            var info = catalog.First(c => c.Id == e.Id);
            var row = new Grid { Height = 46, Background = Brushes.Transparent };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var grip = DragList.Grip();
            row.Children.Add(grip);
            var sw = DialogKit.Switch(e.On, v =>
            {
                var l = CapsuleStore.Current.Clone();
                l.Set(st, e.Id, on: v);
                if (!l.For(st).Any(x => x.On)) { Render(); return; }   // 至少留一项
                // 精简状态 / 额度环本身默认关：在这里打开就是你明确要它，顺手把对应的采样开关也打开
                if (v && e.Id == "stats") Features.Set(Features.CapsuleStats, true);
                if (v && e.Id == "quota") Features.Set(Features.QuotaCapsule, true);
                CapsuleStore.Save(l);
                RenderSoon();
            });
            sw.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(sw, 1); row.Children.Add(sw);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nt = DialogKit.Text(info.Name, 13, FontWeights.Medium, e.On ? DialogKit.Fg : DialogKit.Sub); nt.TextWrapping = TextWrapping.NoWrap;
            name.Children.Add(nt);
            var hint = info.Hint;
            var warn = false;
            if (e.On && e.Id == "stats" && !Features.On(Features.CapsuleStats)) { hint = "采样开关关着，不会显示"; warn = true; }
            if (e.On && e.Id == "quota" && !Features.On(Features.QuotaCapsule)) { hint = "「收起时显示小环」关着，不会显示"; warn = true; }
            var ht = Small(hint, warn ? Theme.Orange : null); ht.TextWrapping = TextWrapping.NoWrap; ht.TextTrimming = TextTrimming.CharacterEllipsis; ht.FontSize = 11;
            name.Children.Add(ht);
            Grid.SetColumn(name, 2); row.Children.Add(name);
            var slot = SlotPicker(e.Slot, CapsuleLayout.SlotLocked(st, e.Id), s =>
            {
                var l = CapsuleStore.Current.Clone();
                l.Set(st, e.Id, slot: s);
                CapsuleStore.Save(l); Render();
            });
            Grid.SetColumn(slot, 3); row.Children.Add(slot);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            arrows.Children.Add(IconBtn("\uE70E", "上移", () => MoveCap(st, idx, idx - 1), i > 0));
            arrows.Children.Add(IconBtn("\uE70D", "下移", () => MoveCap(st, idx, idx + 1), i < entries.Count - 1));
            Grid.SetColumn(arrows, 4); row.Children.Add(arrows);
            list.AddRow(row, grip);
        }
        list.Moved += (a, b) => MoveCap(st, a, b);
        box.Children.Add(list);
        p.Children.Add(Card(box));
    }

    private void MoveCap(CapsuleState st, int from, int to)
    {
        var l = CapsuleStore.Current.Clone();
        l.Move(st, from, to);
        CapsuleStore.Save(l);
        Render();
    }

    /// <summary>预览舞台：深色桌面一样的渐变底 + 一颗按当前主题、当前编排画出来的胶囊（示例数据）。</summary>
    private static FrameworkElement CapsuleStage(CapsuleLayout layout, CapsuleState st)
    {
        var stage = new Border
        {
            Height = 86, CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = Theme.Freeze(new LinearGradientBrush(Color.FromRgb(0x3A, 0x45, 0x63), Color.FromRgb(0x8A, 0x6E, 0x8F), 20)),
        };
        var g = new Grid();
        // 两团柔光，让玻璃 / 半透明主题在预览里也看得出质感
        g.Children.Add(new Ellipse { Width = 180, Height = 120, Fill = Theme.Freeze(new RadialGradientBrush(Color.FromArgb(0x70, 0xFF, 0xC0, 0x8A), Color.FromArgb(0, 0xFF, 0xC0, 0x8A))), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-30, -40, 0, 0), VerticalAlignment = VerticalAlignment.Top });
        g.Children.Add(new Ellipse { Width = 200, Height = 140, Fill = Theme.Freeze(new RadialGradientBrush(Color.FromArgb(0x60, 0x7A, 0xC8, 0xFF), Color.FromArgb(0, 0x7A, 0xC8, 0xFF))), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -40, -60), VerticalAlignment = VerticalAlignment.Bottom });
        var pill = CapsulePreview(layout, st);
        pill.HorizontalAlignment = HorizontalAlignment.Center; pill.VerticalAlignment = VerticalAlignment.Top; pill.Margin = new Thickness(0, 14, 0, 0);
        g.Children.Add(pill);
        stage.Child = g;
        return stage;
    }

    /// <summary>示例数据画的胶囊（时间 21:45、晴 23°、两节课的倒计时、示例歌曲…，不读任何真实数据）。</summary>
    internal static FrameworkElement CapsulePreview(CapsuleLayout layout, CapsuleState st)
    {
        var h = st == CapsuleState.Idle ? 34.0 : 36.0;
        var grid = new Grid { Height = h, MinWidth = st == CapsuleState.Idle ? 176 : 200 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock T(string s, double size, FontWeight w, Brush fg) => new() { Text = s, FontFamily = Theme.Text, FontSize = size, FontWeight = w, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
        FrameworkElement? Item(string id)
        {
            switch (id)
            {
                case "clock": return T("21:45", st == CapsuleState.Idle ? 13.5 : 13, FontWeights.SemiBold, Theme.Primary);
                case "weather":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var gl = T("\u2600", 12.5, FontWeights.Normal, Theme.Secondary); gl.FontFamily = Theme.Symbols; gl.Margin = new Thickness(0, 1, 5, 0);
                    sp.Children.Add(gl); sp.Children.Add(T("23°", 13, FontWeights.Medium, Theme.Secondary));
                    return sp;
                }
                case "countdown": return T("高数 14:00", 11.5, FontWeights.SemiBold, Theme.Orange);
                case "stats":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    sp.Children.Add(T("CPU", 9.5, FontWeights.SemiBold, Theme.Tertiary));
                    var a = T("12%", 11, FontWeights.SemiBold, Theme.Secondary); a.Margin = new Thickness(3, 0, 8, 0); sp.Children.Add(a);
                    sp.Children.Add(T("内存", 9.5, FontWeights.SemiBold, Theme.Tertiary));
                    var b = T("48%", 11, FontWeights.SemiBold, Theme.Secondary); b.Margin = new Thickness(3, 0, 0, 0); sp.Children.Add(b);
                    return sp;
                }
                case "quota":
                {
                    var host = new Grid { Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
                    host.Children.Add(new RingArc { Thickness = 2.4, Gradient = (Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x9F, 0x0A)), Track = Theme.Fill, Progress = 0.62 });
                    return host;
                }
                case "cover":
                {
                    var art = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                    art.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0x2A, 0x5C), 0));
                    art.GradientStops.Add(new GradientStop(Color.FromRgb(0xC8, 0x4E, 0x5A), 0.62));
                    art.GradientStops.Add(new GradientStop(Color.FromRgb(0xF2, 0x9A, 0x5C), 1));
                    var tile = new Grid { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                    tile.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = Theme.Freeze(art) });
                    tile.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 5, 0) });
                    return tile;
                }
                case "title":
                {
                    var t = new TextBlock { FontFamily = Theme.Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(10, 0, 10, 1), HorizontalAlignment = HorizontalAlignment.Center };
                    t.Inlines.Add(new Run("晚自习") { Foreground = Theme.Primary, FontWeight = FontWeights.SemiBold });
                    t.Inlines.Add(new Run("  示例歌手") { Foreground = Theme.Secondary, FontWeight = FontWeights.Medium });
                    return t;
                }
                case "bars":
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Height = 14, VerticalAlignment = VerticalAlignment.Center };
                    double[] hs = [0.55, 1, 0.7];
                    for (var i = 0; i < 3; i++)
                        sp.Children.Add(new Rectangle { Width = 3, Height = 14 * hs[i], RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.Red, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(i == 0 ? 0 : 2.5, 0, 0, 0) });
                    return sp;
                }
            }
            return null;
        }
        var inset = st == CapsuleState.Idle ? 16.0 : 14.0;
        foreach (var (slot, col) in new[] { (CapsuleSlot.Left, 0), (CapsuleSlot.Center, 1), (CapsuleSlot.Right, 2) })
        {
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = slot == CapsuleSlot.Center ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                Margin = slot switch { CapsuleSlot.Left => new Thickness(inset, 0, 0, 0), CapsuleSlot.Right => new Thickness(0, 0, st == CapsuleState.Idle ? 15 : 16, 0), _ => new Thickness(10, 0, 10, 0) },
            };
            var ids = layout.Shown(st, slot);
            for (var i = 0; i < ids.Count; i++)
            {
                if (Item(ids[i]) is not { } el) continue;
                if (sp.Children.Count > 0 && ids[i] != "title") el.Margin = new Thickness(8, el.Margin.Top, el.Margin.Right, el.Margin.Bottom);
                sp.Children.Add(el);
            }
            Grid.SetColumn(sp, col);
            grid.Children.Add(sp);
        }
        var pill = new Border { CornerRadius = new CornerRadius(h / 2), Background = Theme.Pill, Child = grid, SnapsToDevicePixels = true };
        if (Theme.Rim != null) { pill.BorderBrush = Theme.Rim; pill.BorderThickness = new Thickness(1); }
        pill.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };
        pill.Opacity = Theme.IslandOpacity;
        return pill;
    }

    // ───────────── 二、展开组件 ─────────────

    private void WidgetEditor(StackPanel p)
    {
        p.Children.Add(Section("展开后的组件页"));
        var box = new StackPanel();
        var island = _app.Island;
        if (island == null) { box.Children.Add(Small("岛还没启动")); p.Children.Add(Card(box)); return; }
        var items = island.WidgetLayout.ToList();
        var reset = DialogKit.Button("恢复默认", () => { island.ResetWidgetLayout(); Render(); });
        box.Children.Add(HeaderRow("顺序与显示", "按住左边的把手拖动；半宽组件相邻时会两两并排", reset));
        var list = new DragList();
        for (var i = 0; i < items.Count; i++)
        {
            var idx = i;
            var (id, nameText, half, on) = items[i];
            var row = new Grid { Height = 44, Background = Brushes.Transparent };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var grip = DragList.Grip();
            row.Children.Add(grip);
            var tile = IslandWindow.WidgetIcon(id, 26); tile.Margin = new Thickness(0, 0, 10, 0); tile.VerticalAlignment = VerticalAlignment.Center; tile.Opacity = on ? 1 : 0.45;
            Grid.SetColumn(tile, 1); row.Children.Add(tile);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nt = DialogKit.Text(nameText, 13, FontWeights.Medium, on ? DialogKit.Fg : DialogKit.Sub); nt.TextWrapping = TextWrapping.NoWrap; name.Children.Add(nt);
            var sub = id switch
            {
                "quick" => QuickStore.Current.Count == 0 ? "还没有按钮，下面加一个就会出现" : $"{QuickStore.Current.Count} 个按钮 · 整行",
                "devices" => "整行 · 读到外设电量时才出现",
                _ when id.StartsWith("p:", StringComparison.Ordinal) => "插件卡片 · 整行 · 在「插件」页开关",
                _ => half ? "半宽" : "整行",
            };
            var st = Small(sub); st.FontSize = 11; name.Children.Add(st);
            Grid.SetColumn(name, 2); row.Children.Add(name);
            var sw = DialogKit.Switch(on, v => { var cur = island.WidgetLayout.Select(x => (x.Id, x.Id == id ? v : x.On)).ToList(); island.SetWidgetLayout(cur); RenderSoon(); });
            sw.Margin = new Thickness(8, 0, 6, 0);
            Grid.SetColumn(sw, 3); row.Children.Add(sw);
            var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            arrows.Children.Add(IconBtn("\uE70E", "上移", () => MoveWidget(idx, idx - 1), i > 0));
            arrows.Children.Add(IconBtn("\uE70D", "下移", () => MoveWidget(idx, idx + 1), i < items.Count - 1));
            Grid.SetColumn(arrows, 4); row.Children.Add(arrows);
            list.AddRow(row, grip);
        }
        list.Moved += MoveWidget;
        box.Children.Add(list);
        var foot = Small("岛上组件页底部的「编辑组件」也能改，两边是同一份配置（widgets.txt）");
        foot.Margin = new Thickness(0, 8, 0, 8); box.Children.Add(foot);
        p.Children.Add(Card(box));
    }

    private void MoveWidget(int from, int to)
    {
        if (_app.Island is not { } island) return;
        var cur = island.WidgetLayout.Select(x => (x.Id, x.On)).ToList();
        WidgetOrder.Move(cur, from, to);
        island.SetWidgetLayout(cur);
        Render();
    }

    // ───────────── 三、主题包 ─────────────

    private void ThemeEditor(StackPanel p)
    {
        p.Children.Add(Section("主题包"));
        var box = new StackPanel();
        box.Children.Add(HeaderRow("换一套配色", "theme.json 只写想改的字段：底色、卡片、强调色、文字、圆角、透明度、材质、模糊、字体", null));
        var wrap = new WrapPanel { Margin = new Thickness(-4, 0, -4, 4) };
        var active = ThemeStore.ActiveKey;
        wrap.Children.Add(ThemeTile(null, null, active == null));
        foreach (var e in ThemeStore.All()) wrap.Children.Add(ThemeTile(e.Key, e, e.Key == active));
        box.Children.Add(wrap);
        var btns = new WrapPanel { Margin = new Thickness(-8, 6, 0, 8) };
        btns.Children.Add(DialogKit.Button("导入主题包…", ImportTheme, primary: true));
        btns.Children.Add(DialogKit.Button("导出当前为 theme.json…", ExportTheme));
        btns.Children.Add(DialogKit.Button("打开主题文件夹", () =>
        {
            try { Directory.CreateDirectory(ThemeStore.UserDir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ThemeStore.UserDir) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { App.Log("打开主题文件夹: " + ex.Message); }
        }));
        box.Children.Add(btns);
        if (_themeNotes.Count > 0)
        {
            var notes = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            foreach (var n in _themeNotes.Take(6)) { var t = Small((_themeNoteBad ? "✕ " : "· ") + n, _themeNoteBad ? DialogKit.Danger : Theme.Orange); t.Margin = new Thickness(0, 1, 0, 1); notes.Children.Add(t); }
            if (_themeNotes.Count > 6) notes.Children.Add(Small($"还有 {_themeNotes.Count - 6} 条"));
            box.Children.Add(notes);
        }
        p.Children.Add(Card(box));
    }

    /// <summary>一张主题卡：上面是这套主题画出来的迷你胶囊 + 卡片条，下面是名字和材质。</summary>
    private Border ThemeTile(string? key, ThemeStore.Entry? e, bool selected)
    {
        var pack = e?.Pack;
        static Color C(Argb? a, Color fb) => a is { } v ? Color.FromArgb(v.A, v.R, v.G, v.B) : fb;
        var bg = C(pack?.Background, Color.FromRgb(0x12, 0x12, 0x14));
        var bgEnd = C(pack?.BackgroundEnd ?? pack?.Background, Color.FromRgb(0x02, 0x02, 0x03));
        var light = pack != null && !pack.DarkBase;
        var text = C(pack?.Text, light ? Color.FromRgb(0x1D, 0x1D, 0x1F) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        var sub = C(pack?.Subtext, light ? Color.FromArgb(0xA6, 0x3C, 0x3C, 0x43) : Color.FromArgb(0x9E, 0xEB, 0xEB, 0xF5));
        var accent = C(pack?.Accent, Color.FromRgb(0x0A, 0x84, 0xFF));
        var card = C(pack?.Card, light ? Color.FromArgb(0x17, 0, 0, 0) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        var font = pack?.Font is { } f ? new FontFamily(f + ", Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI") : Theme.Text;
        var glass = pack?.Material is ThemeMaterial.Frosted or ThemeMaterial.Liquid;

        var stage = new Grid { Height = 78 };
        var backdrop = new LinearGradientBrush(glass ? Color.FromRgb(0x9F, 0xC9, 0xB8) : Color.FromRgb(0x2E, 0x34, 0x48), glass ? Color.FromRgb(0xE9, 0xD7, 0xB5) : Color.FromRgb(0x52, 0x45, 0x5E), 30);
        stage.Children.Add(new Border { CornerRadius = new CornerRadius(10, 10, 0, 0), Background = Theme.Freeze(backdrop) });
        // 迷你胶囊
        var pillGrid = new Grid { Height = 22, Width = 104 };
        pillGrid.Children.Add(new TextBlock { Text = "21:45", FontFamily = font, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(text), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) });
        var wx = new TextBlock { Text = "\u2600 23°", FontFamily = font, FontSize = 9.5, Foreground = new SolidColorBrush(sub), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        pillGrid.Children.Add(wx);
        var pill = new Border { CornerRadius = new CornerRadius(11), Background = new LinearGradientBrush(bg, bgEnd, 90), Child = pillGrid, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
        if (glass) { pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)); pill.BorderThickness = new Thickness(0.8); }
        stage.Children.Add(pill);
        // 展开卡片的一角：卡片底色 + 强调色进度条
        var cardRow = new Grid { Width = 120, Height = 30, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 8) };
        var radius = (pack?.Radius ?? 30) / 3.2;
        var cardBg = new Border { CornerRadius = new CornerRadius(radius), Background = new LinearGradientBrush(bg, bgEnd, 90) };
        cardRow.Children.Add(cardBg);
        var inner = new Border { CornerRadius = new CornerRadius(Math.Max(3, radius - 2)), Background = new SolidColorBrush(card), Margin = new Thickness(4) };
        var innerSp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 7, 0) };
        innerSp.Children.Add(new Border { Height = 3, Width = 44, CornerRadius = new CornerRadius(1.5), Background = new SolidColorBrush(text) { Opacity = 0.85 }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) });
        var track = new Grid { Height = 4 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(sub) { Opacity = 0.35 } });
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(accent), Width = 58, HorizontalAlignment = HorizontalAlignment.Left });
        innerSp.Children.Add(track);
        inner.Child = innerSp;
        cardRow.Children.Add(inner);
        stage.Children.Add(cardRow);
        if (selected)
        {
            var badge = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = DialogKit.Accent, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0) };
            badge.Child = new TextBlock { Text = "\uE73E", FontFamily = DialogKit.Icons, FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
            stage.Children.Add(badge);
        }

        var name = DialogKit.Text(pack?.Name ?? "默认", 12.5, FontWeights.SemiBold, DialogKit.Fg); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var mat = pack == null ? "跟随外观设置" : (pack.Material switch { ThemeMaterial.Frosted => "磨砂玻璃", ThemeMaterial.Liquid => "液态玻璃", ThemeMaterial.Solid => pack.DarkBase ? "纯色 · 深" : "纯色 · 浅", _ => "沿用外观" })
            + (e is { Builtin: true } ? " · 官方" : e != null ? " · 已导入" : "");
        var meta = Small(mat); meta.FontSize = 10.5; meta.TextWrapping = TextWrapping.NoWrap; meta.TextTrimming = TextTrimming.CharacterEllipsis;
        var foot = new StackPanel { Margin = new Thickness(10, 7, 10, 8) };
        foot.Children.Add(name); foot.Children.Add(meta);
        var sp = new StackPanel(); sp.Children.Add(stage); sp.Children.Add(foot);
        var tile = new Border
        {
            Width = 150, Margin = new Thickness(4, 0, 4, 8), CornerRadius = new CornerRadius(12), Child = sp, Cursor = Cursors.Hand,
            Background = DialogKit.Faint, BorderThickness = new Thickness(selected ? 2 : 1), BorderBrush = selected ? DialogKit.Accent : DialogKit.Line,
            ToolTip = pack == null ? "不用主题包，回到外观设置里选的样子" : (pack.Description.Length > 0 ? pack.Description : pack.Name) + (pack.Author.Length > 0 ? "\n作者：" + pack.Author : ""),
        };
        stage.Clip = null;
        if (!selected)
        {
            tile.MouseEnter += (_, _) => tile.BorderBrush = DialogKit.Sub;
            tile.MouseLeave += (_, _) => tile.BorderBrush = DialogKit.Line;
            tile.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; _themeNotes = []; _app.SetThemePack(key); RenderSoon(); };
        }
        return tile;
    }

    private void ImportTheme()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "导入主题包", Filter = "Qisland 主题包 (*.json)|*.json", CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        var key = ThemeStore.Import(dlg.FileName, out var pack, out var error, out var warnings);
        if (key == null || pack == null) { _themeNotes = ["没能导入：" + (error ?? "未知原因")]; _themeNoteBad = true; Render(); return; }
        _themeNoteBad = false;
        _themeNotes = warnings.Count > 0 ? warnings.Prepend($"已导入「{pack.Name}」，有 {warnings.Count} 处被忽略或调整：").ToList() : [];
        _app.SetThemePack(key);
        RenderSoon();
    }

    private void ExportTheme()
    {
        var pack = ThemeStore.ExportPack(Theme.Current);
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "导出主题包", Filter = "Qisland 主题包 (*.json)|*.json", FileName = ThemePack.SafeFileName(pack.Name), AddExtension = true, DefaultExt = ".json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, pack.ToJson(), new System.Text.UTF8Encoding(false));
            _themeNoteBad = false; _themeNotes = [$"已导出「{pack.Name}」，可以直接发给朋友导入"];
        }
        catch (Exception ex) { _themeNoteBad = true; _themeNotes = ["导出失败：" + ex.Message]; }
        Render();
    }

    // ───────────── 四、快捷按钮 ─────────────

    private static readonly string[] QuickIcons = ["⚡", "★", "♥", "☕", "✉", "✎", "♪", "☀", "☾", "⚙", "⏻", "✈", "⌂", "☎", "✓", "⏱"];

    private void QuickEditor(StackPanel p)
    {
        p.Children.Add(Section("快捷按钮"));
        var box = new StackPanel();
        var list = QuickStore.Current.ToList();
        var add = DialogKit.Button("＋ 添加", () => { _quickEdit = -1; _quickDraft = new QuickAction("⚡", "", QuickKind.Url, "https://"); _quickError = null; Render(); }, primary: true);
        add.IsEnabled = list.Count < QuickActions.Max && _quickEdit == -2;
        box.Children.Add(HeaderRow("一键动作", $"出现在组件页的「快捷按钮」卡片里，最多 {QuickActions.Max} 个；一个都没有时卡片不占位", add));
        if (list.Count == 0 && _quickEdit == -2)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 14) };
            var gl = new TextBlock { Text = "⚡", FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), FontSize = 22, Foreground = DialogKit.Sub, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(gl);
            var t = Small("还没有快捷按钮。可以放常用网站、常开的文件夹、一句提醒，或者本机脚本的接口"); t.TextAlignment = TextAlignment.Center; t.MaxWidth = 340; t.Margin = new Thickness(0, 4, 0, 0);
            empty.Children.Add(t);
            box.Children.Add(empty);
        }
        var dl = new DragList();
        for (var i = 0; i < list.Count; i++)
        {
            var idx = i;
            var a = list[i];
            var row = new Grid { Height = 50, Background = _quickEdit == i ? DialogKit.Faint : Brushes.Transparent, Opacity = _quickEdit >= 0 && _quickEdit != i ? 0.5 : 1 };
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
            var grip = DragList.Grip();
            row.Children.Add(grip);
            var tile = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Background = DialogKit.Faint, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            tile.Child = new TextBlock { Text = a.Icon, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), FontSize = 16, Foreground = DialogKit.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tile, 1); row.Children.Add(tile);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nt = DialogKit.Text(a.Title, 13, FontWeights.Medium, DialogKit.Fg); nt.TextWrapping = TextWrapping.NoWrap; name.Children.Add(nt);
            var target = a.Kind == QuickKind.Open ? System.IO.Path.GetFileName(a.Target.TrimEnd('\\', '/')) is { Length: > 0 } fn ? fn : a.Target : a.Target;
            var st = Small(QuickActions.KindName(a.Kind) + " · " + target); st.FontSize = 11; st.TextWrapping = TextWrapping.NoWrap; st.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Children.Add(st);
            Grid.SetColumn(name, 2); row.Children.Add(name);
            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            acts.Children.Add(IconBtn("\uE70F", "编辑", () => { _quickEdit = idx; _quickDraft = a; _quickError = null; Render(); }, _quickEdit == -2));
            acts.Children.Add(IconBtn("\uE74D", "删除", () => { var l = QuickStore.Current.ToList(); l.RemoveAt(idx); QuickStore.Save(l); Render(); }, _quickEdit == -2, DialogKit.Danger));
            acts.Children.Add(IconBtn("\uE70E", "上移", () => MoveQuick(idx, idx - 1), i > 0 && _quickEdit == -2));
            acts.Children.Add(IconBtn("\uE70D", "下移", () => MoveQuick(idx, idx + 1), i < list.Count - 1 && _quickEdit == -2));
            Grid.SetColumn(acts, 3); row.Children.Add(acts);
            dl.AddRow(row, grip);
        }
        dl.Moved += MoveQuick;
        box.Children.Add(dl);
        if (_quickEdit != -2) box.Children.Add(QuickForm());
        var note = Small("网页只认 http(s)；程序 / 文件要选本机已存在的路径，直接交给系统打开、不经过命令行；本机接口只能发到 127.0.0.1 / localhost。");
        note.Margin = new Thickness(0, 6, 0, 10); note.FontSize = 11;
        box.Children.Add(note);
        p.Children.Add(Card(box));
    }

    private void MoveQuick(int from, int to)
    {
        var l = QuickStore.Current.ToList();
        WidgetOrder.Move(l, from, to);
        QuickStore.Save(l);
        Render();
    }

    private static bool PathExists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>新增 / 编辑一个快捷按钮的表单（内嵌在列表里，不另开窗口）。</summary>
    private FrameworkElement QuickForm()
    {
        var d = _quickDraft ?? new QuickAction("⚡", "", QuickKind.Url, "https://");
        var form = new StackPanel();
        var card = new Border { Background = DialogKit.Faint, BorderBrush = DialogKit.Accent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 4, 0, 8), Child = form };

        var title = DialogKit.Box(d.Title);
        var target = DialogKit.Box(d.Target);
        var body = DialogKit.Box(d.Body);

        // 图标
        form.Children.Add(DialogKit.Label("图标"));
        var icons = new WrapPanel();
        var custom = DialogKit.Box(QuickIcons.Contains(d.Icon) ? "" : d.Icon, 56);
        custom.ToolTip = "也可以粘贴一个 emoji";
        custom.MaxLength = 8;
        foreach (var ic in QuickIcons)
        {
            var on = ic == d.Icon;
            var c = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 5, 5), Cursor = Cursors.Hand, Background = on ? DialogKit.Accent : DialogKit.Faint };
            c.Child = new TextBlock { Text = ic, FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Symbol"), FontSize = 14, Foreground = on ? Brushes.White : DialogKit.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var pick = ic;
            c.MouseLeftButtonUp += (_, e) => { e.Handled = true; _quickDraft = Read() with { Icon = pick }; Render(); };
            icons.Children.Add(c);
        }
        custom.Margin = new Thickness(2, 0, 0, 5);
        icons.Children.Add(custom);
        form.Children.Add(icons);

        // 名称
        form.Children.Add(DialogKit.Label($"名称（最多 {QuickActions.MaxTitle} 个字）"));
        title.MaxLength = 24;
        form.Children.Add(title);

        // 动作
        form.Children.Add(DialogKit.Label("点了之后"));
        var kinds = new[] { QuickKind.Url, QuickKind.Open, QuickKind.Notify, QuickKind.Local };
        body.Height = 56; body.AcceptsReturn = true; body.TextWrapping = TextWrapping.Wrap; body.VerticalContentAlignment = VerticalAlignment.Top;
        form.Children.Add(DialogKit.Segmented(kinds.Select(QuickActions.KindName).ToArray(), Array.IndexOf(kinds, d.Kind), i =>
        {
            var k = kinds[i];
            var cur = Read();
            var t = k == cur.Kind ? cur.Target : k switch { QuickKind.Url => "https://", QuickKind.Local => $"http://127.0.0.1:{App.Port}/v1/island/push", _ => "" };
            var b = k == QuickKind.Local && cur.Body.Length == 0 ? "{\"title\":\"来自快捷按钮\",\"message\":\"hello\"}" : cur.Body;
            _quickDraft = cur with { Kind = k, Target = t, Body = b };
            _quickError = null;
            Render();
        }));

        var (label, tip) = d.Kind switch
        {
            QuickKind.Url => ("网址", "以 http:// 或 https:// 开头"),
            QuickKind.Open => ("程序、文件或文件夹", "要完整路径；点下面的按钮选更省事"),
            QuickKind.Notify => ("要在岛上显示的话", "最多 120 字，比如「站起来走一走」"),
            _ => ("本机地址", "例如 Qisland 自己的推送接口；留空请求内容就发 GET"),
        };
        var lt = DialogKit.Label(label); form.Children.Add(lt);
        form.Children.Add(target);
        var tt = Small(tip); tt.FontSize = 11; tt.Margin = new Thickness(0, 4, 0, 0); form.Children.Add(tt);
        if (d.Kind == QuickKind.Open)
        {
            var pick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 8, 0, 0) };
            pick.Children.Add(DialogKit.Button("选程序 / 文件…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选一个程序或文件", CheckFileExists = true };
                if (dlg.ShowDialog(this) == true) { _quickDraft = Read() with { Target = dlg.FileName }; if (_quickDraft.Title.Length == 0) _quickDraft = _quickDraft with { Title = QuickActions.CleanTitle(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName)) }; Render(); }
            }));
            pick.Children.Add(DialogKit.Button("选文件夹…", () =>
            {
                using var dlg = new Forms.FolderBrowserDialog { Description = "选一个文件夹", UseDescriptionForTitle = true };
                if (dlg.ShowDialog() == Forms.DialogResult.OK) { _quickDraft = Read() with { Target = dlg.SelectedPath }; if (_quickDraft.Title.Length == 0) _quickDraft = _quickDraft with { Title = QuickActions.CleanTitle(new DirectoryInfo(dlg.SelectedPath).Name) }; Render(); }
            }));
            form.Children.Add(pick);
        }
        if (d.Kind == QuickKind.Local)
        {
            form.Children.Add(DialogKit.Label("请求内容（可选，JSON 或文字；填了就用 POST）"));
            form.Children.Add(body);
        }
        if (_quickError != null) { var er = Small("✕ " + _quickError, DialogKit.Danger); er.Margin = new Thickness(0, 8, 0, 0); form.Children.Add(er); }

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        btns.Children.Add(DialogKit.Button("取消", () => { _quickEdit = -2; _quickDraft = null; _quickError = null; Render(); }));
        btns.Children.Add(DialogKit.Button("保存", () =>
        {
            var a = Read();
            if (QuickActions.Validate(a, a.Kind == QuickKind.Open ? PathExists : null) is { } err) { _quickDraft = a; _quickError = err; Render(); return; }
            var l = QuickStore.Current.ToList();
            if (_quickEdit >= 0 && _quickEdit < l.Count) l[_quickEdit] = a; else l.Add(a);
            QuickStore.Save(l);
            _quickEdit = -2; _quickDraft = null; _quickError = null;
            Render();
        }, primary: true));
        form.Children.Add(btns);

        QuickAction Read() => new(
            QuickActions.CleanIcon(custom.Text.Trim().Length > 0 ? custom.Text : (_quickDraft ?? d).Icon),
            QuickActions.CleanTitle(title.Text), (_quickDraft ?? d).Kind, target.Text.Trim(), (_quickDraft ?? d).Kind == QuickKind.Local ? body.Text : "");
        return card;
    }

    // ───────────── 素材 ─────────────

    /// <summary>--snapshot：用演示数据把「布局与 DIY」整页渲染成 PNG（不落盘任何配置、不显示窗口）。</summary>
    internal static async Task SnapshotDiy(App app, string dir)
    {
        QuickStore.Demo(IslandWindow.DemoQuick);
        var lay = CapsuleLayout.Default();
        lay.Set(CapsuleState.Playing, "clock", on: true);
        CapsuleStore.Demo(lay);
        var w = new SettingsWindow(app, new Forms.ContextMenuStrip(), 1);
        foreach (var (state, name) in new[] { (CapsuleState.Idle, "30-settings-diy"), (CapsuleState.Playing, "31-settings-diy-playing") })
        {
            w._capState = state;
            var body = new StackPanel { Width = 520 };
            var h = DialogKit.Text("布局与 DIY", 20, FontWeights.SemiBold, DialogKit.Fg); h.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(h);
            w.Diy(body);
            var host = new Border { Background = DialogKit.Surface, Padding = new Thickness(22, 20, 22, 14), Child = body, CornerRadius = new CornerRadius(18) };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            await Task.Delay(150);
            const double scale = 2;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            await using var f = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        w.Close();
        QuickStore.Demo(null);
        CapsuleStore.Demo(null);
    }
}
