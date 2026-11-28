using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>
/// 设置 → 插件：两级徽章一眼区分「安全卡片 · L2（纯配置、无代码）」和「高级插件 · L3（会运行本机程序、未沙箱）」；
/// 开关前按需弹出权限确认；展开一行可以预览卡片、改参数、手动刷新。
/// </summary>
public sealed partial class SettingsWindow
{
    private string? _plOpen;
    private string? _plMsg;
    private bool _plMsgBad;
    /// <summary>0 = 已安装，1 = 插件市场。</summary>
    private int _plTab;

    internal static readonly Color L2Color = Color.FromRgb(0x40, 0xC8, 0xE0);
    internal static readonly Color L3Color = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static Brush Br(Color c, byte a = 0xFF) => Theme.Freeze(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)));

    /// <summary>分级徽章：青色盾牌 = L2 安全卡片；琥珀色感叹号 = L3 高级插件。</summary>
    internal static Border TierBadge(PluginTier tier, bool compact = false)
    {
        var l2 = tier == PluginTier.Declarative;
        var c = l2 ? L2Color : L3Color;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = l2 ? "\uEA18" : "\uE7BA", FontFamily = DialogKit.Icons, FontSize = 10.5, Foreground = Br(c), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        var t = DialogKit.Text(l2 ? (compact ? "安全卡片 · L2" : "安全卡片 · L2 · 纯配置") : (compact ? "高级插件 · L3" : "高级插件 · L3 · 未沙箱"), 11, FontWeights.SemiBold, Br(c));
        t.TextWrapping = TextWrapping.NoWrap;
        sp.Children.Add(t);
        return new Border
        {
            Child = sp, CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 2.5, 8, 3), Margin = new Thickness(0, 0, 6, 4),
            Background = Br(c, 0x22), BorderBrush = Br(c, 0x55), BorderThickness = new Thickness(0.8),
            ToolTip = l2 ? "无代码：Qisland 只按 plugin.json 的声明去拉数据、画卡片，只能访问它声明的 ≤3 个 HTTPS 域名" : "会在你的电脑上运行脚本或程序，没有沙箱，只安装你信任来源的插件",
        };
    }

    private static Border Chip(string glyph, string text, Brush? fg = null, Brush? bg = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph.Length > 0) sp.Children.Add(new TextBlock { Text = glyph, FontFamily = DialogKit.Icons, FontSize = 10, Foreground = fg ?? DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 5, 0) });
        var t = DialogKit.Text(text, 11, FontWeights.Medium, fg ?? DialogKit.Sub); t.TextWrapping = TextWrapping.NoWrap;
        sp.Children.Add(t);
        return new Border { Child = sp, CornerRadius = new CornerRadius(7), Padding = new Thickness(7, 2.5, 8, 3), Margin = new Thickness(0, 0, 6, 4), Background = bg ?? DialogKit.Faint };
    }

    private void PluginsPage(StackPanel p)
    {
        var host = _app.Plugins;
        if (host == null) { p.Children.Add(Small("插件还没启动")); return; }

        var installedCount = host.Installed.Count();
        var tabs = DialogKit.Segmented([$"已安装 · {installedCount}", "插件市场"], _plTab, i => { _plTab = i; _plMsg = null; Render(); });
        tabs.Margin = new Thickness(0, 0, 0, 14);
        p.Children.Add(tabs);
        if (_plTab == 1) { MarketPage(p, host); return; }

        // 两级说明
        var intro = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        intro.ColumnDefinitions.Add(new ColumnDefinition());
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        intro.ColumnDefinitions.Add(new ColumnDefinition());
        Border Tier(PluginTier t, string body)
        {
            var c = t == PluginTier.Declarative ? L2Color : L3Color;
            var sp = new StackPanel();
            sp.Children.Add(TierBadge(t));
            var d = Small(body); d.Margin = new Thickness(1, 2, 0, 0); d.LineHeight = 17;
            sp.Children.Add(d);
            return new Border
            {
                Child = sp, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 10),
                Background = Theme.Freeze(new LinearGradientBrush(Color.FromArgb(0x1A, c.R, c.G, c.B), Color.FromArgb(0x06, c.R, c.G, c.B), 90)),
                BorderBrush = Br(c, 0x30), BorderThickness = new Thickness(1),
            };
        }
        intro.Children.Add(Tier(PluginTier.Declarative, "只有一个 plugin.json，不含任何代码。Qisland 代它按声明拉取数据：只走 HTTPS、最多 3 个声明过的域名、不碰内网，每张卡最快 60 秒刷新一次。"));
        var l3 = Tier(PluginTier.Process, "会在你的电脑上运行脚本或程序，没有沙箱，能读写你能访问的文件。只安装信任来源的插件；启用和每次更新入口文件都要你确认。");
        Grid.SetColumn(l3, 2);
        intro.Children.Add(l3);
        p.Children.Add(intro);
        var rule = Small("所有插件都拿不到微信消息、剪贴板、其他软件的配置和审批接口。");
        rule.Margin = new Thickness(2, 4, 0, 8); rule.FontSize = 11;
        p.Children.Add(rule);

        // 操作
        var bar = new WrapPanel { Margin = new Thickness(-8, 0, 0, 4) };
        bar.Children.Add(DialogKit.Button("导入 ZIP…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Qisland 插件 (*.zip)|*.zip", Title = "导入插件" };
            if (dlg.ShowDialog(this) != true) return;
            _plMsg = host.Import(dlg.FileName);
            _plMsgBad = !_plMsg.StartsWith("已", StringComparison.Ordinal);
            Render();
        }, primary: true));
        bar.Children.Add(DialogKit.Button("新建卡片模板", () =>
        {
            try { var id = host.CreateTemplate(); _plOpen = id; _plMsg = $"已在插件文件夹建好 {id}\\plugin.json（纯 JSON，默认关闭）"; _plMsgBad = false; }
            catch (Exception ex) { _plMsg = "新建失败：" + ex.Message; _plMsgBad = true; }
            Render();
        }));
        bar.Children.Add(DialogKit.Button("打开插件文件夹", () =>
        {
            try { Directory.CreateDirectory(PluginHost.UserDir); Process.Start(new ProcessStartInfo(PluginHost.UserDir) { UseShellExecute = true }); }
            catch (Exception ex) { App.Log("打开插件文件夹: " + ex.Message); }
        }));
        bar.Children.Add(DialogKit.Button("重新扫描", () => { host.Reload(); _plMsg = null; Render(); }));
        p.Children.Add(bar);
        if (_plMsg is { Length: > 0 } msg)
        {
            var m = DialogKit.Text(msg, 12, FontWeights.Normal, _plMsgBad ? DialogKit.Danger : DialogKit.Fg);
            p.Children.Add(new Border { Child = m, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 6, 0, 2), Background = _plMsgBad ? Br(Color.FromRgb(0xFF, 0x45, 0x3A), 0x1C) : DialogKit.Faint });
        }

        var official = host.Installed.Where(e => e.Official).ToList();
        var mine = host.Installed.Where(e => !e.Official).ToList();
        p.Children.Add(Section(official.Count > 0 ? $"官方 · {official.Count} 张安全卡片（默认关闭，打开就能用）" : "官方"));
        if (official.Count == 0)
        {
            var t = Small("官方插件都卸载了。去「插件市场」可以一键装回来。");
            t.Margin = new Thickness(2, 0, 0, 8);
            p.Children.Add(t);
        }
        foreach (var e in official) p.Children.Add(PluginRow(host, e));
        p.Children.Add(Section("我安装 / 导入的"));
        if (mine.Count == 0)
        {
            var t = Small("还没有。去「插件市场」装一个，导入别人分享的 .zip，或者点「新建卡片模板」照着改一个自己的。");
            t.Margin = new Thickness(2, 0, 0, 8);
            p.Children.Add(t);
        }
        foreach (var e in mine) p.Children.Add(PluginRow(host, e));
    }

    private Border PluginRow(PluginHost host, PluginEntry e)
    {
        var m = e.Manifest;
        var on = host.IsEnabled(e);
        var pending = m.IsValid && e.LoadError == null && on ? host.PendingConsent(e) : [];
        var open = string.Equals(_plOpen, e.Id, StringComparison.OrdinalIgnoreCase);
        var box = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tile = CardRenderer.Tile(m.Icon, m.Accent, 34);
        tile.VerticalAlignment = VerticalAlignment.Top; tile.Margin = new Thickness(0, 1, 12, 0);
        tile.Opacity = on && pending.Count == 0 ? 1 : 0.6;
        top.Children.Add(tile);

        var info = new StackPanel();
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        var name = DialogKit.Text(m.Name, 13.5, FontWeights.SemiBold, DialogKit.Fg); name.TextWrapping = TextWrapping.NoWrap;
        nameLine.Children.Add(name);
        var ver = DialogKit.Text((m.Version.Length > 0 ? "v" + m.Version : "") + (m.Author.Length > 0 ? " · " + m.Author : ""), 11, FontWeights.Normal, DialogKit.Sub);
        ver.Margin = new Thickness(8, 2, 0, 0); ver.TextWrapping = TextWrapping.NoWrap;
        nameLine.Children.Add(ver);
        info.Children.Add(nameLine);

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        chips.Children.Add(TierBadge(m.Tier, compact: true));
        if (e.Official) chips.Children.Add(Chip("\uE73E", "官方", Br(Color.FromRgb(0x0A, 0x84, 0xFF)), Br(Color.FromRgb(0x0A, 0x84, 0xFF), 0x1E)));
        if (e.Receipt is { } rc) chips.Children.Add(rc.Verified ? Chip("\uEA18", "市场 · 已验签", Br(Color.FromRgb(0x0A, 0x84, 0xFF)), Br(Color.FromRgb(0x0A, 0x84, 0xFF), 0x1E)) : Chip("\uE7BA", "第三方源 · 未验签", Br(L3Color), Br(L3Color, 0x1C)));
        if (m.Tier == PluginTier.Declarative)
        {
            if (m.Domains.Count == 0 && m.LocalSources.Count == 0) chips.Children.Add(Chip("\uE8CE", "不联网", Br(Color.FromRgb(0x30, 0xD1, 0x58)), Br(Color.FromRgb(0x30, 0xD1, 0x58), 0x1C)));
            foreach (var d in m.Domains) chips.Children.Add(Chip("\uE774", d));
            foreach (var l in m.LocalSources) chips.Children.Add(Chip("\uE977", "本机 " + l, Br(L3Color), Br(L3Color, 0x1C)));
        }
        else
        {
            chips.Children.Add(Chip("\uE756", "运行 " + m.Entry, Br(L3Color), Br(L3Color, 0x1C)));
            chips.Children.Add(Chip("\uE72E", m.Sha256.Length > 0 ? "SHA-256 已声明" : "未声明 SHA-256"));
        }
        info.Children.Add(chips);
        if (m.Description.Length > 0)
        {
            var d = Small(m.Description); d.Margin = new Thickness(0, 2, 0, 0); d.LineHeight = 17;
            info.Children.Add(d);
        }
        Grid.SetColumn(info, 1);
        top.Children.Add(info);

        var sw = DialogKit.Switch(on, v => { if (!host.SetEnabled(e, v)) Dispatcher.BeginInvoke(Render); else RenderSoon(); });
        sw.VerticalAlignment = VerticalAlignment.Top; sw.Margin = new Thickness(12, 2, 0, 0);
        if (!m.IsValid || e.LoadError != null) sw.IsEnabled = false;
        Grid.SetColumn(sw, 2);
        top.Children.Add(sw);
        box.Children.Add(top);

        var inner = new StackPanel { Margin = new Thickness(46, 0, 0, 0) };
        if (host.RevokedReason(e) is { } revoked)
        {
            sw.IsEnabled = false;
            inner.Children.Add(RevokedBanner(revoked));
        }
        else if (!m.IsValid || e.LoadError != null)
        {
            var errs = m.IsValid ? [e.LoadError!] : m.Errors.Take(5).ToList();
            var t = DialogKit.Text("无法启用：\n· " + string.Join("\n· ", errs), 11.5, FontWeights.Normal, DialogKit.Danger);
            t.Margin = new Thickness(0, 6, 0, 2);
            inner.Children.Add(t);
        }
        else if (pending.Count > 0)
        {
            var banner = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            banner.ColumnDefinitions.Add(new ColumnDefinition());
            banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var bt = DialogKit.Text("插件更新后申请了新能力，已暂停：\n· " + string.Join("\n· ", pending), 11.5, FontWeights.Normal, Br(L3Color));
            banner.Children.Add(bt);
            var ok = DialogKit.Button("查看并确认", () => { if (host.SetEnabled(e, true)) RenderSoon(); });
            ok.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(ok, 1);
            banner.Children.Add(ok);
            inner.Children.Add(new Border { Child = banner, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 6, 8), Background = Br(L3Color, 0x1A), BorderBrush = Br(L3Color, 0x44), BorderThickness = new Thickness(1) });
        }
        else if (on)
        {
            var status = e.LastError != null ? "刷新失败：" + e.LastError
                : e.LastOk is { } t ? $"上次更新 {t:HH:mm}" + (e.NextRun > DateTime.Now && e.NextRun < DateTime.MaxValue ? $" · 下次约 {e.NextRun:HH:mm}" : "")
                : m.Tier == PluginTier.Declarative && !m.Sources.Any(s => s.Type is "https" or "local") ? "本地计算 · 实时" : "等待第一次刷新";
            var st = Small(status, e.LastError != null ? DialogKit.Danger : null); st.Margin = new Thickness(0, 4, 0, 0); st.FontSize = 11;
            inner.Children.Add(st);
        }

        // 展开：预览 + 参数 + 刷新 / 删除
        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        var tt = DialogKit.Text(open ? "收起" : "预览与设置", 11.5, FontWeights.SemiBold, DialogKit.Accent);
        toggle.Children.Add(tt);
        toggle.Children.Add(new TextBlock { Text = open ? "\uE70E" : "\uE70D", FontFamily = DialogKit.Icons, FontSize = 9, Foreground = DialogKit.Accent, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 2, 0, 0) });
        toggle.MouseLeftButtonUp += (_, _) => { _plOpen = open ? null : e.Id; Render(); };
        if (m.IsValid) inner.Children.Add(toggle);
        if (open && m.IsValid) inner.Children.Add(PluginDetail(host, e));
        box.Children.Add(inner);

        var accent = m.Tier == PluginTier.Process ? Br(L3Color, 0x40) : on && pending.Count == 0 ? Br(L2Color, 0x30) : DialogKit.Line;
        return new Border { Child = box, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8), Background = DialogKit.Faint, BorderBrush = accent, BorderThickness = new Thickness(1) };
    }

    private StackPanel PluginDetail(PluginHost host, PluginEntry e)
    {
        var m = e.Manifest;
        var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        // 预览：和岛上一样的卡片底色
        if (host.View(e) is { } view)
        {
            var preview = new Border
            {
                Width = 372, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(Theme.CardRadius), Padding = new Thickness(14, 12, 14, 12),
                Background = Theme.IsGlass ? Br(Color.FromRgb(0x2C, 0x2C, 0x2E)) : Theme.Card,
                Child = CardRenderer.Build(view, a => { if (a.Kind == "refresh") { host.Refresh(e); RenderSoon(); } else CardRenderer.Open(a); }),
            };
            var frame = new Border { Child = preview, Background = Br(Colors.Black, DialogKit.Dark ? (byte)0x40 : (byte)0xE0), CornerRadius = new CornerRadius(Theme.CardRadius + 8), Padding = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(frame);
            var cap = Small(e.Tier == PluginTier.Declarative ? "预览 · 打开后它会作为整行组件出现在岛的组件页，可在「布局与 DIY」里拖动排序" : "预览 · 高级插件的卡片来自它程序的输出");
            cap.FontSize = 11; cap.Margin = new Thickness(2, 6, 0, 0);
            sp.Children.Add(cap);
        }

        var values = new Dictionary<string, string>(host.ConfigOf(e));
        if (m.ConfigFields.Count > 0)
        {
            sp.Children.Add(Section("参数"));
            foreach (var f in m.ConfigFields)
            {
                FrameworkElement input;
                if (f.Type == "select")
                {
                    var cb = new ComboBox { Width = 200, Height = 30, FontFamily = Theme.Text, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
                    foreach (var o in f.Options) cb.Items.Add(o);
                    cb.SelectedItem = values.TryGetValue(f.Key, out var cur) ? cur : f.Default;
                    cb.SelectionChanged += (_, _) => values[f.Key] = cb.SelectedItem as string ?? f.Default;
                    input = cb;
                }
                else
                {
                    var tb = DialogKit.Box(values.TryGetValue(f.Key, out var cur) ? cur : f.Default, 220);
                    tb.MaxLength = CardLimits.MaxConfigValue;
                    tb.ToolTip = f.Placeholder.Length > 0 ? "例如：" + f.Placeholder : f.Type == "date" ? "格式：2027-01-15" : null;
                    tb.TextChanged += (_, _) => values[f.Key] = tb.Text.Trim();
                    input = tb;
                }
                sp.Children.Add(DialogKit.Row(f.Label, f.Placeholder.Length > 0 ? "例如 " + f.Placeholder : f.Type == "date" ? "yyyy-MM-dd" : null, input));
            }
        }
        var bar = new WrapPanel { Margin = new Thickness(-8, 10, 0, 0) };
        if (m.ConfigFields.Count > 0)
            bar.Children.Add(DialogKit.Button("保存参数", () =>
            {
                var err = host.SetConfig(e, values);
                _plMsg = err ?? $"{m.Name} 的参数已保存"; _plMsgBad = err != null;
                Render();
            }, primary: true));
        if (host.IsEnabled(e)) bar.Children.Add(DialogKit.Button("立即刷新", () => { host.Refresh(e); RenderSoon(); }));
        if (e.Dir != null) bar.Children.Add(DialogKit.Button("打开插件目录", () =>
        {
            try { Process.Start(new ProcessStartInfo(e.Dir) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { App.Log("打开插件目录: " + ex.Message); }
        }));
        bar.Children.Add(DialogKit.Button(e.Bundled ? "卸载" : "删除", () =>
        {
            var ask = e.Bundled ? $"卸载内置插件「{m.Name}」？它会从已安装里消失、不再运行，参数会保留，在插件市场里可以一键装回。"
                : e.Official ? $"删除「{m.Name}」从市场更新来的版本？删掉后回到程序内置的版本。"
                : $"删除插件「{m.Name}」和它的文件夹？";
            if (MessageBox.Show(this, ask, "Qisland", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _plMsg = host.Remove(e) ? (e.Bundled ? $"已卸载 {m.Name}" : $"已删除 {m.Name}") : "删除失败"; _plMsgBad = false; _plOpen = null;
            Render();
        }, danger: true));
        if (bar.Children.Count > 0) sp.Children.Add(bar);
        return sp;
    }

    // ───────────── 权限确认弹窗 ─────────────

    /// <summary>确认弹窗：L2 列出要访问的域名；L3 醒目提示「会运行本机程序」并列出入口文件与 SHA-256；本机数据源逐项勾选。</summary>
    internal static List<string>? AskConsent(PluginEntry e, IReadOnlyList<string> reasons)
    {
        List<string>? result = null;
        var w = new Window();
        var body = ConsentBody(e, reasons, ok => { result = ok; w.Close(); }, () => w.Close());
        DialogKit.Shell(w, e.Tier == PluginTier.Process ? "启用高级插件" : "启用安全卡片", body, 440);
        w.ShowDialog();
        return result;
    }

    internal static StackPanel ConsentBody(PluginEntry e, IReadOnlyList<string> reasons, Action<List<string>> ok, Action cancel)
    {
        var m = e.Manifest;
        var l3 = m.Tier == PluginTier.Process;
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = CardRenderer.Tile(m.Icon, m.Accent, 42); tile.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(tile);
        var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hs.Children.Add(DialogKit.Text(m.Name, 15, FontWeights.SemiBold, DialogKit.Fg));
        var sub = new WrapPanel { Margin = new Thickness(0, 4, 0, -4) };
        sub.Children.Add(TierBadge(m.Tier));
        if (m.Author.Length > 0) sub.Children.Add(Chip("", "作者 " + m.Author));
        hs.Children.Add(sub);
        Grid.SetColumn(hs, 1);
        head.Children.Add(hs);
        sp.Children.Add(head);

        if (l3)
        {
            var warn = new StackPanel();
            var wt = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            wt.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = DialogKit.Icons, FontSize = 14, Foreground = Br(L3Color), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            wt.Children.Add(DialogKit.Text("它会在你的电脑上运行程序", 13, FontWeights.SemiBold, Br(L3Color)));
            warn.Children.Add(wt);
            var wd = DialogKit.Text("高级插件没有沙箱：它能读写你能访问的文件、联网、启动其他程序。Qisland 只负责按时运行它、超时结束它、读取它的输出。只安装你信任来源的插件。", 12, FontWeights.Normal, DialogKit.Fg);
            wd.LineHeight = 18;
            warn.Children.Add(wd);
            sp.Children.Add(new Border { Child = warn, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 11), Background = Br(L3Color, 0x1C), BorderBrush = Br(L3Color, 0x55), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) });
            sp.Children.Add(DialogKit.Row("入口文件", null, Mono(m.Entry)));
            sp.Children.Add(DialogKit.Row("SHA-256", m.Sha256.Length > 0 ? "和 plugin.json 声明的一致" : "plugin.json 没声明，已记下当前值；文件一变就要重新确认", Mono(e.EntryHash.Length >= 16 ? e.EntryHash[..16] + "…" : e.EntryHash)));
            if (m.IntervalSeconds > 0) sp.Children.Add(DialogKit.Row("运行频率", $"每 {m.IntervalSeconds} 秒一次，最长 {m.TimeoutSeconds} 秒，超时结束整个进程树", new TextBlock()));
        }
        else
        {
            var safe = DialogKit.Text("这是纯 JSON 卡片，不包含也不会运行任何代码。", 12.5, FontWeights.Normal, DialogKit.Fg);
            safe.Margin = new Thickness(0, 0, 0, 8);
            sp.Children.Add(safe);
            var dl = DialogKit.Label(m.Domains.Count == 0 ? "网络访问" : "Qisland 会代它访问（仅 HTTPS，只读）");
            sp.Children.Add(dl);
            var chips = new WrapPanel();
            if (m.Domains.Count == 0) chips.Children.Add(Chip("\uE8CE", "不联网，完全在本机计算", Br(Color.FromRgb(0x30, 0xD1, 0x58)), Br(Color.FromRgb(0x30, 0xD1, 0x58), 0x1C)));
            foreach (var d in m.Domains) chips.Children.Add(Chip("\uE774", d, DialogKit.Fg));
            sp.Children.Add(chips);
        }

        var checks = new List<(CheckBox Box, string Source)>();
        if (m.LocalSources.Count > 0)
        {
            sp.Children.Add(DialogKit.Label("本机 / 内网数据（默认不允许，逐项勾选）"));
            foreach (var l in m.LocalSources)
            {
                var cb = new CheckBox { Content = DialogKit.Text("允许读取 " + l, 12.5, FontWeights.Normal, DialogKit.Fg), Margin = new Thickness(0, 2, 0, 4), IsChecked = false };
                checks.Add((cb, l));
                sp.Children.Add(cb);
            }
        }

        if (reasons.Count > 0)
        {
            sp.Children.Add(DialogKit.Label("为什么需要确认"));
            foreach (var r in reasons)
            {
                var t = DialogKit.Text("· " + r, 12, FontWeights.Normal, DialogKit.Sub);
                t.Margin = new Thickness(2, 0, 0, 2);
                sp.Children.Add(t);
            }
        }
        var note = DialogKit.Text("不管哪一级，插件都拿不到微信消息、剪贴板、其他软件的配置和审批接口。", 11, FontWeights.Normal, DialogKit.Sub);
        note.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(note);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        btns.Children.Add(DialogKit.Button("取消", cancel));
        btns.Children.Add(DialogKit.Button(l3 ? "我信任它，启用" : "启用", () => ok(checks.Where(c => c.Box.IsChecked == true).Select(c => c.Source).ToList()), primary: true));
        sp.Children.Add(btns);
        return sp;
    }

    private static TextBlock Mono(string s)
    {
        var t = DialogKit.Text(s, 12, FontWeights.Medium, DialogKit.Fg);
        t.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        t.TextWrapping = TextWrapping.NoWrap;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    // ───────────── 素材 ─────────────

    /// <summary>--snapshot：插件管理页（L2 / L3 徽章）、一行展开的预览与参数、L3 确认弹窗。全部演示数据，不联网、不落盘。</summary>
    internal static async Task SnapshotPlugins(App app, string dir)
    {
        var host = app.Plugins;
        if (host == null) return;
        var w = new SettingsWindow(app, new Forms.ContextMenuStrip(), 2);
        async Task Save(UIElement content, string name, double width)
        {
            var host2 = new Border { Background = DialogKit.Surface, Padding = new Thickness(22, 20, 22, 14), Child = content, CornerRadius = new CornerRadius(18), Width = width };
            host2.Measure(new Size(width, double.PositiveInfinity));
            host2.Arrange(new Rect(host2.DesiredSize));
            host2.UpdateLayout();
            await Task.Delay(700);   // 进度条动画走完
            host2.UpdateLayout();
            const double scale = 2;
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(host2.ActualWidth * scale), (int)Math.Ceiling(host2.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host2);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            await using var f = File.Create(System.IO.Path.Combine(dir, name + ".png"));
            enc.Save(f);
        }
        StackPanel Page()
        {
            var body = new StackPanel { Width = 560 };
            var h = DialogKit.Text("插件", 20, FontWeights.SemiBold, DialogKit.Fg); h.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(h);
            w.PluginsPage(body);
            return body;
        }
        w._plOpen = null;
        await Save(Page(), "41-plugin-manager-tiers", 604);
        w._plOpen = "npm-package-stats";
        await Save(Page(), "43-plugin-preview-config", 604);
        if (host.All.FirstOrDefault(e => e.Tier == PluginTier.Process) is { } l3)
            await Save(ConsentBody(l3, PluginPermissions.Escalations(null, PluginGrant.Of(l3.Manifest, l3.EntryHash)), _ => { }, () => { }), "42-plugin-consent-l3", 484);
        // 5.0 第三波：插件市场按默认的「内置离线目录」渲染（不模拟已验签的在线索引）
        if (app.Market is { } market)
        {
            w._plTab = 1; w._plMsg = null;
            w._mkCat = "all"; w._mkSources = false;
            await Save(Page(), "44-plugin-market", 604);
            w._mkCat = "theme";
            await Save(Page(), "45-plugin-market-themes", 604);
            w._mkCat = "all"; w._mkQuery = "github";
            await Save(Page(), "46-plugin-market-search", 604);
            w._mkQuery = ""; w._mkSources = true; w._mkCat = "dev";
            await Save(Page(), "47-plugin-market-sources", 604);
            if (market.Listings.FirstOrDefault(l => l.Item.Tier == PluginTier.Process) is { } l3m)
                await Save(MarketConfirmBody(l3m, () => { }, () => { }), "48-market-install-l3-third-party", 484);
            w._plTab = 0; w._mkSources = false; w._mkCat = "all";
        }
        w.Close();
    }
}
