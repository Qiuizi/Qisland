using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 设置 → 插件 → 插件市场：内置离线目录 + 官方在线索引（Ed25519 验签）+ 第三方源（明确标「未验签」）。
/// 分类筛选、关键词搜索、每张卡片列出权限（L2 青色安全徽章 / L3 琥珀色警告）、一键安装 / 更新 / 停用 / 卸载 / 打开目录；主题包一键应用。
/// </summary>
public sealed partial class SettingsWindow
{
    private string _mkCat = "all";
    private string _mkQuery = "";
    private bool _mkSources;
    private string? _mkBusy;
    private string _mkNewSource = "";

    private static readonly Color VerifiedColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color OkColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color ThemeColor = Color.FromRgb(0xBF, 0x5A, 0xF2);
    private static readonly Color RevokedColor = Color.FromRgb(0xFF, 0x45, 0x3A);

    private static string CategoryAccent(string c) => c switch { "productivity" => "#30D158", "dev" => "#0A84FF", "info" => "#5E5CE6", "theme" => "#BF5AF2", _ => "#8E8E93" };

    internal static Border RevokedBanner(string reason)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = DialogKit.Icons, FontSize = 12, Foreground = Br(RevokedColor), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top });
        var t = DialogKit.Text("已被撤销，已自动停用：" + reason, 11.5, FontWeights.SemiBold, Br(RevokedColor));
        t.MaxWidth = 430;
        sp.Children.Add(t);
        return new Border { Child = sp, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 8), Margin = new Thickness(0, 8, 0, 2), Background = Br(RevokedColor, 0x18), BorderBrush = Br(RevokedColor, 0x44), BorderThickness = new Thickness(1) };
    }

    /// <summary>来源徽章：官方已验签（蓝盾）/ 内置离线目录 / 第三方源 · 未验签（琥珀）。</summary>
    private static Border SourceChip(MarketListing l) =>
        l.Builtin ? Chip("\uE8B7", "内置离线目录", Br(VerifiedColor), Br(VerifiedColor, 0x1A))
        : l.Verified ? Chip("\uEA18", l.Source.Official ? "官方 · 已验签" : l.Source.Name + " · 已验签", Br(VerifiedColor), Br(VerifiedColor, 0x1E))
        : Chip("\uE7BA", "第三方源 · 未验签", Br(L3Color), Br(L3Color, 0x1C));

    private static Border ThemeBadge()
    {
        var b = Chip("\uE790", "主题包 · 纯配置", Br(ThemeColor), Br(ThemeColor, 0x22));
        b.BorderBrush = Br(ThemeColor, 0x55); b.BorderThickness = new Thickness(0.8);
        b.ToolTip = "一个 theme.json，只改颜色、圆角、透明度和字体，不联网、不含代码";
        return b;
    }

    /// <summary>权限清单：L2 列域名 / 不联网 / 本机数据源；L3 醒目写「会运行本机程序」。</summary>
    private static void PermissionChips(WrapPanel w, MarketItem item)
    {
        var p = item.Permissions;
        if (p.RunsProgram) w.Children.Add(Chip("\uE756", "会运行本机程序 · 未沙箱", Br(L3Color), Br(L3Color, 0x1C)));
        if (p.Offline) w.Children.Add(Chip("\uE8CE", "不联网", Br(OkColor), Br(OkColor, 0x1C)));
        foreach (var d in p.Domains) w.Children.Add(Chip("\uE774", d));
        foreach (var l in p.Local) w.Children.Add(Chip("\uE977", "本机 " + l, Br(L3Color), Br(L3Color, 0x1C)));
    }

    private void MarketPage(StackPanel p, PluginHost host)
    {
        var market = _app.Market;
        if (market == null) { p.Children.Add(Small("插件市场还没启动")); return; }

        // ── 目录状态 ──
        var official = market.Status(MarketSources.Official);
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var online = official?.Registry != null && official.Verified;
        var glyph = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            Background = Theme.Freeze(new LinearGradientBrush(Color.FromRgb(0x3A, 0x9B, 0xFF), Color.FromRgb(0x0A, 0x60, 0xD6), 90)),
            Child = new TextBlock { Text = online ? "\uEA18" : "\uE8B7", FontFamily = DialogKit.Icons, FontSize = 16, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        head.Children.Add(glyph);
        var hs = new StackPanel();
        hs.Children.Add(DialogKit.Text(online ? "官方索引 · Ed25519 已验签" : "内置离线目录", 13.5, FontWeights.SemiBold, DialogKit.Fg));
        var total = market.Listings.Count;
        var sub = online
            ? $"{total} 项 · 索引更新于 {official!.Registry!.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" + (market.LastRefresh is { } lr ? $"\n上次检查 {lr.ToLocalTime():MM-dd HH:mm}" : "")
            : $"{total} 项随程序内置，没网也能装" + (official?.Error != null ? " · 在线索引暂时连不上" : market.LastRefresh == null ? " · 点「刷新在线目录」获取社区插件" : "");
        var st = Small(sub); st.Margin = new Thickness(0, 2, 0, 0); st.FontSize = 11.5;
        hs.Children.Add(st);
        Grid.SetColumn(hs, 1);
        head.Children.Add(hs);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(DialogKit.Button(market.Refreshing ? "刷新中…" : "刷新在线目录", async () =>
        {
            if (market.Refreshing) return;
            Render();
            var msg = await market.Refresh();
            _plMsg = msg; _plMsgBad = msg.Contains("失败") || msg.Contains("丢弃");
            if (IsLoaded) Render();
        }));
        btns.Children.Add(DialogKit.Button(_mkSources ? "收起索引源" : $"索引源 · {1 + market.CustomSources.Count}", () => { _mkSources = !_mkSources; Render(); }));
        Grid.SetColumn(btns, 2);
        head.Children.Add(btns);
        p.Children.Add(new Border { Child = head, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 8, 12), Margin = new Thickness(0, 0, 0, 8), Background = DialogKit.Faint, BorderBrush = Br(VerifiedColor, 0x30), BorderThickness = new Thickness(1) });

        if (_mkSources) p.Children.Add(SourcesPanel(market));
        if (_plMsg is { Length: > 0 } msg0)
        {
            var m = DialogKit.Text(msg0, 12, FontWeights.Normal, _plMsgBad ? DialogKit.Danger : DialogKit.Fg);
            p.Children.Add(new Border { Child = m, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8), Background = _plMsgBad ? Br(RevokedColor, 0x1C) : DialogKit.Faint });
        }

        // ── 分类 + 搜索 ──
        var filter = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        filter.ColumnDefinitions.Add(new ColumnDefinition());
        filter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cats = new[] { "all" }.Concat(MarketCategories.All).ToArray();
        var labels = cats.Select(c => (c == "all" ? "全部" : MarketCategories.Name(c)) + " " + (c == "all" ? total : market.Listings.Count(l => l.Item.Category == c))).ToArray();
        filter.Children.Add(DialogKit.Segmented(labels, Math.Max(0, Array.IndexOf(cats, _mkCat)), i => { _mkCat = cats[i]; Render(); }));
        var list = new StackPanel();
        var search = SearchBox(_mkQuery, q => { _mkQuery = q; FillMarket(list, market, host); });
        Grid.SetColumn(search, 1);
        filter.Children.Add(search);
        p.Children.Add(filter);
        p.Children.Add(list);
        FillMarket(list, market, host);

        var foot = Small("安全卡片（L2）只有一个 plugin.json，不含代码；高级插件（L3）会运行本机程序、没有沙箱，安装和启用都会单独确认。所有下载只走 https，并逐字节核对 SHA-256；命中撤销名单的插件会被立即停用。");
        foot.FontSize = 11; foot.LineHeight = 16; foot.Margin = new Thickness(2, 6, 0, 0);
        p.Children.Add(foot);
    }

    /// <summary>带放大镜和占位字的搜索框；输入时只重画列表，不丢焦点。</summary>
    private static Border SearchBox(string text, Action<string> changed)
    {
        var g = new Grid { Width = 168 };
        var icon = new TextBlock { Text = "\uE721", FontFamily = DialogKit.Icons, FontSize = 11.5, Foreground = DialogKit.Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 1, 0, 0), IsHitTestVisible = false };
        var ghost = DialogKit.Text("搜索名称、作者、描述", 12, FontWeights.Normal, DialogKit.Sub);
        ghost.VerticalAlignment = VerticalAlignment.Center; ghost.Margin = new Thickness(30, 0, 0, 1); ghost.IsHitTestVisible = false;
        ghost.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var tb = new TextBox
        {
            Text = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = DialogKit.Fg, CaretBrush = DialogKit.Fg,
            FontFamily = Theme.Text, FontSize = 12.5, Padding = new Thickness(26, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center, MaxLength = 60,
        };
        tb.TextChanged += (_, _) => { ghost.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; changed(tb.Text); };
        g.Children.Add(tb); g.Children.Add(icon); g.Children.Add(ghost);
        return new Border { Child = g, CornerRadius = new CornerRadius(9), Background = DialogKit.Faint, BorderBrush = DialogKit.Line, BorderThickness = new Thickness(1), Height = 30, VerticalAlignment = VerticalAlignment.Center };
    }

    private void FillMarket(StackPanel list, MarketService market, PluginHost host)
    {
        list.Children.Clear();
        var items = MarketCatalog.Filter(market.Listings, _mkCat, _mkQuery);
        if (items.Count == 0)
        {
            var t = Small(_mkQuery.Length > 0 ? $"没有找到和「{_mkQuery.Trim()}」相关的插件或主题。" : "这个分类暂时还没有内容。");
            t.Margin = new Thickness(2, 10, 0, 14);
            list.Children.Add(t);
            return;
        }
        string? lastCat = null;
        foreach (var l in items)
        {
            if (_mkCat == "all" && l.Item.Category != lastCat)
            {
                lastCat = l.Item.Category;
                list.Children.Add(Section(MarketCategories.Name(lastCat)));
            }
            list.Children.Add(MarketCard(market, host, l));
        }
    }

    private Border MarketCard(MarketService market, PluginHost host, MarketListing l)
    {
        var item = l.Item;
        var st = market.StateOf(l);
        var box = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement tile = item.Kind == MarketKind.Theme && ThemeStore.Builtins.FirstOrDefault(b => b.Key == "builtin:" + item.Id) is { } bt
            ? ThemeSwatch(bt.Pack, 34) : CardRenderer.Tile(item.Icon, st.Plugin?.Manifest.Accent is { Length: > 0 } ac ? ac : CategoryAccent(item.Category), 34);
        tile.VerticalAlignment = VerticalAlignment.Top; tile.Margin = new Thickness(0, 1, 12, 0);
        if (st.State == MarketState.Revoked) tile.Opacity = 0.5;
        top.Children.Add(tile);

        var info = new StackPanel();
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        var name = DialogKit.Text(item.Name, 13.5, FontWeights.SemiBold, DialogKit.Fg); name.TextWrapping = TextWrapping.NoWrap;
        nameLine.Children.Add(name);
        var ver = DialogKit.Text("v" + item.Version + (item.Author.Length > 0 ? " · " + item.Author : ""), 11, FontWeights.Normal, DialogKit.Sub);
        ver.Margin = new Thickness(8, 2, 0, 0); ver.TextWrapping = TextWrapping.NoWrap;
        nameLine.Children.Add(ver);
        info.Children.Add(nameLine);

        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        chips.Children.Add(item.Kind == MarketKind.Theme ? ThemeBadge() : TierBadge(item.Tier, compact: true));
        chips.Children.Add(SourceChip(l));
        PermissionChips(chips, item);
        info.Children.Add(chips);
        if (item.Description.Length > 0)
        {
            var d = Small(item.Description); d.Margin = new Thickness(0, 2, 0, 0); d.LineHeight = 17;
            info.Children.Add(d);
        }
        Grid.SetColumn(info, 1);
        top.Children.Add(info);

        // 右上角：状态
        var (stateText, stateColor) = st.State switch
        {
            MarketState.Installed when item.Kind == MarketKind.Theme => st.Active ? ("使用中", OkColor) : (st.Note == "内置" ? "内置" : "已安装", VerifiedColor),
            MarketState.Installed => st.Active ? ("已启用", OkColor) : ("已安装", VerifiedColor),
            MarketState.Update => ($"可更新 · v{st.InstalledVersion}", L3Color),
            MarketState.Incompatible => ("需要新版 Qisland", L3Color),
            MarketState.Revoked => ("已撤销", RevokedColor),
            _ => ("", VerifiedColor),
        };
        if (stateText.Length > 0)
        {
            var s = DialogKit.Text(stateText, 11, FontWeights.SemiBold, Br(stateColor)); s.TextWrapping = TextWrapping.NoWrap;
            var sb = new Border { Child = s, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2.5, 8, 3), Background = Br(stateColor, 0x1E), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 1, 0, 0) };
            Grid.SetColumn(sb, 2);
            top.Children.Add(sb);
        }
        box.Children.Add(top);

        var inner = new StackPanel { Margin = new Thickness(46, 0, 0, 0) };
        if (st.State == MarketState.Revoked && st.Note != null) inner.Children.Add(RevokedBanner(st.Note));
        else if (st.State == MarketState.Incompatible && st.Note != null) { var n = Small(st.Note, Br(L3Color)); n.Margin = new Thickness(0, 4, 0, 0); n.FontSize = 11; inner.Children.Add(n); }

        var bar = new WrapPanel { Margin = new Thickness(-8, 8, 0, 0) };
        var busy = _mkBusy == item.Id;
        void Act(string label, Func<Task> run, bool primary = false, bool danger = false) =>
            bar.Children.Add(DialogKit.Button(busy ? "请稍候…" : label, async () =>
            {
                if (_mkBusy != null) return;
                _mkBusy = item.Id; Render();
                try { await run(); }
                catch (Exception ex) { _plMsg = "出错了：" + ex.Message; _plMsgBad = true; App.Log("市场: " + ex); }
                finally { _mkBusy = null; if (IsLoaded) Render(); }
            }, primary, danger));

        if (item.Kind == MarketKind.Theme)
        {
            if (st.State is MarketState.Install or MarketState.Update)
                Act(st.State == MarketState.Install ? "安装" : $"更新到 v{item.Version}", async () =>
                {
                    if (!l.Verified && !ConfirmMarketInstall(l)) return;
                    var (ok, msg) = await market.Install(l);
                    if (ok && market.StateOf(l).ThemeKey is { } k) _app.SetThemePack(k);
                    _plMsg = ok ? msg + "，已应用" : msg; _plMsgBad = !ok;
                }, primary: true);
            if (st.ThemeKey != null && st.State != MarketState.Revoked)
            {
                if (st.Active) Act("恢复默认外观", () => { _app.SetThemePack(null); _plMsg = "已恢复默认外观"; _plMsgBad = false; return Task.CompletedTask; });
                else Act("应用", () => { _app.SetThemePack(st.ThemeKey); _plMsg = $"已应用主题「{item.Name}」"; _plMsgBad = false; return Task.CompletedTask; }, primary: st.State == MarketState.Installed);
            }
            if (st.ThemeKey?.StartsWith("user:", StringComparison.Ordinal) == true)
                Act("卸载", () => { _plMsg = market.Uninstall(l, st); _plMsgBad = false; return Task.CompletedTask; }, danger: true);
        }
        else
        {
            if (st.State is MarketState.Install or MarketState.Update)
                Act(st.State == MarketState.Install ? "安装" : $"更新到 v{item.Version}", async () =>
                {
                    if ((item.Tier == PluginTier.Process || !l.Verified) && !ConfirmMarketInstall(l)) return;
                    var (ok, msg) = await market.Install(l);
                    _plMsg = msg; _plMsgBad = !ok;
                    // 装好后顺手打开：走 Wave 2 的权限确认（L2 列域名、L3 醒目警告、本机数据源逐项勾选）；取消就保持关闭
                    if (ok && host.Find(item.Id) is { } e && !host.IsEnabled(e) && st.State == MarketState.Install)
                    {
                        if (host.SetEnabled(e, true)) _plMsg = msg + "，已启用";
                        else _plMsg = msg + "（没有启用，需要时在这里或「已安装」里打开）";
                    }
                }, primary: true);
            if (st.Plugin is { } pe && st.InstalledVersion != null && st.State != MarketState.Revoked)
            {
                if (host.IsEnabled(pe)) Act("停用", () => { host.SetEnabled(pe, false); _plMsg = $"已停用 {pe.Manifest.Name}"; _plMsgBad = false; return Task.CompletedTask; });
                else Act("启用", () => { _plMsg = host.SetEnabled(pe, true) ? $"已启用 {pe.Manifest.Name}" : null; _plMsgBad = false; return Task.CompletedTask; }, primary: st.State == MarketState.Installed);
            }
            if (st.Plugin is { } pd && st.InstalledVersion != null)
            {
                if (pd.Dir != null) Act("打开目录", () => { try { Process.Start(new ProcessStartInfo(pd.Dir) { UseShellExecute = true })?.Dispose(); } catch (Exception ex) { App.Log("打开插件目录: " + ex.Message); } return Task.CompletedTask; });
                Act("卸载", () =>
                {
                    var ask = pd.Bundled ? $"卸载内置插件「{pd.Manifest.Name}」？参数会保留，随时可以在这里装回。" : $"卸载「{pd.Manifest.Name}」并删除它的文件夹？";
                    if (MessageBox.Show(this, ask, "Qisland", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK) { _plMsg = market.Uninstall(l, st); _plMsgBad = false; }
                    return Task.CompletedTask;
                }, danger: true);
            }
        }
        if (item.Homepage.Length > 0) Act("主页", () => { CardRenderer.Open(new CardAction("主页", "", "open_url", item.Homepage)); return Task.CompletedTask; });
        if (bar.Children.Count > 0) inner.Children.Add(bar);
        box.Children.Add(inner);

        var border = st.State == MarketState.Revoked ? Br(RevokedColor, 0x40)
            : item.Tier == PluginTier.Process ? Br(L3Color, 0x40)
            : st.Active ? Br(item.Kind == MarketKind.Theme ? ThemeColor : L2Color, 0x40) : DialogKit.Line;
        return new Border { Child = box, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 10), Margin = new Thickness(0, 0, 0, 8), Background = DialogKit.Faint, BorderBrush = border, BorderThickness = new Thickness(1) };
    }

    /// <summary>主题包的小色块：底色渐变 + 强调色圆点，一眼看出是什么色调。</summary>
    private static Border ThemeSwatch(ThemePack pack, double size)
    {
        static Color C(Argb? a, Color fb) => a is { } v ? Color.FromArgb(0xFF, v.R, v.G, v.B) : fb;
        var bg = C(pack.Background, Color.FromRgb(0x1C, 0x1C, 0x1E));
        var end = C(pack.BackgroundEnd ?? pack.Background, bg);
        var accent = C(pack.Accent, Color.FromRgb(0x0A, 0x84, 0xFF));
        var dot = new Border { Width = size * 0.36, Height = size * 0.36, CornerRadius = new CornerRadius(size), Background = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Child = dot,
            Background = new LinearGradientBrush(bg, end, 90), BorderBrush = Br(Colors.White, 0x30), BorderThickness = new Thickness(0.8),
        };
    }

    private Border SourcesPanel(MarketService market)
    {
        var sp = new StackPanel();
        sp.Children.Add(DialogKit.Label("索引源"));
        Border Row(string name, string url, Border chip, UIElement? action)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            var nl = new WrapPanel();
            var n = DialogKit.Text(name, 12.5, FontWeights.SemiBold, DialogKit.Fg); n.Margin = new Thickness(0, 1, 8, 0);
            nl.Children.Add(n); nl.Children.Add(chip);
            left.Children.Add(nl);
            var u = Mono(url); u.FontSize = 11; u.Foreground = DialogKit.Sub; u.TextTrimming = TextTrimming.CharacterEllipsis; u.TextWrapping = TextWrapping.NoWrap;
            u.HorizontalAlignment = HorizontalAlignment.Left; u.Margin = new Thickness(0, 2, 0, 0); u.ToolTip = url;
            left.Children.Add(u);
            g.Children.Add(left);
            if (action != null) { Grid.SetColumn(action, 1); g.Children.Add(action); }
            return new Border { Child = g };
        }
        sp.Children.Add(Row("官方索引", MarketSources.OfficialRegistry, Chip("\uEA18", "Ed25519 验签", Br(VerifiedColor), Br(VerifiedColor, 0x1E)), null));
        var mirror = Small("连不上时自动改走 jsDelivr 镜像；签名对不上、或者比上次的旧，都会被丢弃。"); mirror.FontSize = 11; mirror.Margin = new Thickness(0, -2, 0, 8);
        sp.Children.Add(mirror);
        foreach (var s in market.CustomSources)
        {
            var status = market.Status(s);
            var chip = status?.Verified == true ? Chip("\uEA18", "已验签", Br(VerifiedColor), Br(VerifiedColor, 0x1E)) : Chip("\uE7BA", "第三方源 · 未验签", Br(L3Color), Br(L3Color, 0x1C));
            sp.Children.Add(Row(s.Name + (status?.Error != null ? " · " + status.Error : status?.Registry != null ? $" · {status.Registry.Items.Count} 项" : ""), s.Url, chip,
                DialogKit.Button("移除", () => { market.RemoveSource(s); _plMsg = $"已移除 {s.Name}"; _plMsgBad = false; Render(); }, danger: true)));
        }
        var add = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        add.ColumnDefinitions.Add(new ColumnDefinition());
        add.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = DialogKit.Box(_mkNewSource);
        tb.ToolTip = "例如 https://example.com/qisland/registry.json";
        var ghost = DialogKit.Text("粘贴第三方索引地址：https://…/registry.json", 12, FontWeights.Normal, DialogKit.Sub);
        ghost.VerticalAlignment = VerticalAlignment.Center; ghost.Margin = new Thickness(9, 0, 0, 1); ghost.IsHitTestVisible = false;
        ghost.TextTrimming = TextTrimming.CharacterEllipsis; ghost.TextWrapping = TextWrapping.NoWrap;
        ghost.Visibility = _mkNewSource.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        tb.TextChanged += (_, _) => { _mkNewSource = tb.Text; ghost.Visibility = tb.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
        add.Children.Add(tb);
        add.Children.Add(ghost);
        var ab = DialogKit.Button("添加第三方源", async () =>
        {
            var err = market.AddSource(_mkNewSource, null);
            if (err != null) { _plMsg = err; _plMsgBad = true; Render(); return; }
            _mkNewSource = "";
            _plMsg = "已添加，正在拉取…"; _plMsgBad = false; Render();
            _plMsg = await market.Refresh(); _plMsgBad = false;
            if (IsLoaded) Render();
        });
        Grid.SetColumn(ab, 1);
        add.Children.Add(ab);
        sp.Children.Add(add);
        var warn = Small("第三方源由别人维护，Qisland 只能核对文件和它自己索引里写的 SHA-256 一致，确认不了发布者是谁。它的插件照样分 L2 / L3、照样逐项确认权限，官方撤销名单对它同样生效。");
        warn.FontSize = 11; warn.LineHeight = 16; warn.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(warn);
        return new Border { Child = sp, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 2, 14, 12), Margin = new Thickness(0, 0, 0, 8), Background = DialogKit.Faint, BorderBrush = DialogKit.Line, BorderThickness = new Thickness(1) };
    }

    /// <summary>安装前确认：L3 或第三方未验签源才弹。L3 醒目提示会运行本机程序；未验签说明只核对了 SHA-256。</summary>
    private bool ConfirmMarketInstall(MarketListing l)
    {
        var ok = false;
        var w = new Window { Owner = this };
        var body = MarketConfirmBody(l, () => { ok = true; w.Close(); }, () => w.Close());
        DialogKit.Shell(w, l.Item.Tier == PluginTier.Process ? "安装高级插件" : "从第三方源安装", body, 440);
        w.ShowDialog();
        return ok;
    }

    internal static StackPanel MarketConfirmBody(MarketListing l, Action ok, Action cancel)
    {
        var item = l.Item;
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        var tile = CardRenderer.Tile(item.Icon, CategoryAccent(item.Category), 42); tile.Margin = new Thickness(0, 0, 12, 0);
        head.Children.Add(tile);
        var hs = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        hs.Children.Add(DialogKit.Text(item.Name + "  v" + item.Version, 15, FontWeights.SemiBold, DialogKit.Fg));
        var sub = new WrapPanel { Margin = new Thickness(0, 4, 0, -4) };
        sub.Children.Add(item.Kind == MarketKind.Theme ? ThemeBadge() : TierBadge(item.Tier));
        sub.Children.Add(SourceChip(l));
        hs.Children.Add(sub);
        Grid.SetColumn(hs, 1);
        head.Children.Add(hs);
        sp.Children.Add(head);

        Border Warn(Color c, string title, string text)
        {
            var box = new StackPanel();
            var wt = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            wt.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = DialogKit.Icons, FontSize = 13, Foreground = Br(c), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            wt.Children.Add(DialogKit.Text(title, 13, FontWeights.SemiBold, Br(c)));
            box.Children.Add(wt);
            var wd = DialogKit.Text(text, 12, FontWeights.Normal, DialogKit.Fg); wd.LineHeight = 18;
            box.Children.Add(wd);
            return new Border { Child = box, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10, 12, 11), Background = Br(c, 0x1C), BorderBrush = Br(c, 0x55), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 10) };
        }
        if (item.Tier == PluginTier.Process)
            sp.Children.Add(Warn(L3Color, "它会在你的电脑上运行程序", "高级插件没有沙箱：能读写你能访问的文件、联网、启动其他程序。装好后默认关闭，打开时还会再确认一次入口文件和 SHA-256。只安装你信任的作者发布的插件。"));
        if (!l.Verified)
            sp.Children.Add(Warn(L3Color, "来自第三方源，未验签", $"「{l.Source.Name}」不是官方索引。Qisland 会核对下载到的文件和它索引里写的 SHA-256 一致，但确认不了发布者是谁。"));

        sp.Children.Add(DialogKit.Label("它申请的权限"));
        var chips = new WrapPanel();
        PermissionChips(chips, item);
        if (chips.Children.Count == 0) chips.Children.Add(Chip("\uE8CE", "不联网", Br(OkColor), Br(OkColor, 0x1C)));
        sp.Children.Add(chips);
        sp.Children.Add(DialogKit.Label("下载地址"));
        var u = Mono(item.DownloadUrl); u.FontSize = 11; u.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(u);
        var sha = Mono("SHA-256 " + item.Sha256[..16] + "…"); sha.FontSize = 11; sha.Foreground = DialogKit.Sub; sha.Margin = new Thickness(0, 4, 0, 0);
        sp.Children.Add(sha);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        btns.Children.Add(DialogKit.Button("取消", cancel));
        btns.Children.Add(DialogKit.Button(item.Tier == PluginTier.Process ? "我信任它，下载安装" : "下载安装", ok, primary: true));
        sp.Children.Add(btns);
        return sp;
    }
}
