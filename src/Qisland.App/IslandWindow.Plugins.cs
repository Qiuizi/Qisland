using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 5.0 插件卡片进组件页：每个启用的插件是一个整行组件（键 p:插件id），和内置组件一样能在「布局与 DIY」里开关、拖动排序。
/// 数据刷新时只换这张卡片的内容，不重建整页。
/// </summary>
public sealed partial class IslandWindow
{
    private PluginHost? _plugins;
    private readonly Dictionary<string, Border> _pluginCards = [];

    internal PluginHost? Plugins
    {
        get => _plugins;
        set
        {
            _plugins = value;
            if (value == null) return;
            value.ListChanged += () => Dispatcher.BeginInvoke(() =>
            {
                _widgetList = null;
                _pageCache.Remove(Page.Widgets);
                if (_mode == Mode.Expanded && _page == Page.Widgets && !_animating && !_widgetEdit) RebuildExpanded(); else _expDirty = true;
            });
            value.ViewChanged += key => Dispatcher.BeginInvoke(() => UpdatePluginCard(key));
        }
    }

    /// <summary>组件页认识的全部组件：内置的 + 已启用且授权有效的插件卡片。</summary>
    private List<string> WidgetIds()
    {
        var ids = WidgetDefs.Select(d => d.Id).ToList();
        if (_plugins != null) ids.AddRange(_plugins.Cards.Select(e => e.Manifest.WidgetKey).Where(k => !ids.Contains(k)));
        return ids;
    }

    private static readonly Brush PluginTint = Theme.Freeze(new SolidColorBrush(Color.FromRgb(0x40, 0xC8, 0xE0)));

    private WidgetDef PluginDef(string key)
    {
        var e = _plugins?.FindByKey(key) ?? PluginHost.Current?.FindByKey(key);
        return new WidgetDef(key, e?.Manifest.Name ?? key[2..], "\uE74C", PluginTint, false);
    }

    private FrameworkElement PluginWidget(string key)
    {
        var card = WCard(PluginBody(key));
        _pluginCards[key] = card;
        return card;
    }

    private UIElement PluginBody(string key)
    {
        var e = _plugins?.FindByKey(key);
        var v = e == null ? null : _plugins!.View(e);
        if (e == null || v == null)
        {
            var t = Txt(12, FontWeights.Normal, Theme.Tertiary);
            t.Text = "插件不可用";
            return t;
        }
        return CardRenderer.Build(v, a =>
        {
            if (a.Kind == "refresh") _plugins!.Refresh(e);
            else CardRenderer.Open(a);
        });
    }

    private void UpdatePluginCard(string key)
    {
        if (!_pluginCards.TryGetValue(key, out var card)) return;
        if (!card.IsLoaded) { _pluginCards.Remove(key); _pageCache.Remove(Page.Widgets); _expDirty = true; return; }
        var before = card.ActualHeight;
        SetBody(card, PluginBody(key));
        card.UpdateLayout();
        // 高度变了（比如从「加载中」变成有数据）：整页重排，滚动范围才对
        if (Math.Abs(card.ActualHeight - before) > 1)
        {
            _pageCache.Remove(Page.Widgets);
            if (_mode == Mode.Expanded && _page == Page.Widgets && !_animating && !_widgetEdit) RebuildExpanded(); else _expDirty = true;
        }
    }
}

public sealed partial class IslandWindow
{
    /// <summary>素材用的离线样例数据（形状和真实接口一致，数字是编的）。</summary>
    internal static Dictionary<string, Dictionary<string, string>> DemoPluginMocks()
    {
        var day = DateTime.Today;
        var npm = string.Join(",", Enumerable.Range(0, 30).Select(i =>
        {
            var weekend = day.AddDays(i - 30).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var v = 1_480_000 + i * 21_000 + (int)(Math.Sin(i * 0.9) * 60_000) - (weekend ? 520_000 : 0);
            return $"{{\"day\":\"{day.AddDays(i - 30):yyyy-MM-dd}\",\"downloads\":{v}}}";
        }));
        var unix = DateTimeOffset.Now.AddHours(-3).ToUnixTimeSeconds();
        return new()
        {
            ["github-repo-radar"] = new() { ["repo"] = $$"""{ "stargazers_count": 1286, "forks_count": 94, "open_issues_count": 7, "subscribers_count": 23, "language": "C#", "description": "Windows 上的灵动岛：音乐、歌词、AI 额度、作业 DDL 都在顶部一眼看到", "pushed_at": "{{DateTime.UtcNow.AddHours(-2):yyyy-MM-ddTHH:mm:ssZ}}" }""" },
            ["exchange-rate-watch"] = new() { ["rx"] = $$"""{ "result": "success", "time_last_update_unix": {{unix}}, "rates": { "CNY": 1, "USD": 0.13812, "EUR": 0.13105, "JPY": 21.47, "HKD": 1.0752, "GBP": 0.10921 } }""" },
            ["hn-top-pulse"] = new()
            {
                ["top"] = "[41000001, 41000002, 41000003]",
                ["s1"] = $$"""{ "id": 41000001, "title": "Show HN: A tiny declarative widget engine in 2k lines", "score": 486, "by": "demo_user", "descendants": 173, "time": {{DateTimeOffset.Now.AddHours(-4).ToUnixTimeSeconds()}} }""",
                ["s2"] = """{ "id": 41000002, "title": "Why JSON-only plugins beat scripting for small UIs", "score": 312 }""",
            },
            ["npm-package-stats"] = new() { ["range"] = "{ \"package\": \"typescript\", \"downloads\": [" + npm + "] }" },
        };
    }

    /// <summary>素材用的 L3 条目：只拿来展示徽章和确认弹窗，不会运行任何东西。</summary>
    internal static PluginManifest DemoProcessPlugin() => PluginManifest.Parse("""
    { "id": "gpu-temp-script", "name": "显卡温度（示例脚本）", "version": "0.3.1", "author": "社区示例", "icon": "♨", "accent": "#FF9F0A",
      "tier": "process", "entry": "gpu.ps1", "interval_seconds": 60, "timeout_seconds": 10,
      "description": "每分钟运行一次本机 PowerShell 脚本读取显卡温度。会运行本机程序，只装信任来源的。" }
    """);

    /// <summary>--snapshot：组件页里的声明式卡片（离线样例数据），外加一张错误态。</summary>
    internal async Task SnapshotPlugins(string dir, PluginHost host)
    {
        Plugins = host;
        foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id);
        Media = null;
        async Task Show(string name, params string[] ids)
        {
            var keys = ids.Select(i => "p:" + i).ToList();
            _widgetList = keys.Select(k => (k, true)).Concat(WidgetIds().Where(k => !keys.Contains(k)).Select(k => (k, false))).ToList();
            _page = Page.Widgets; _expDirty = true; _pageCache.Clear(); _wScroll = 0;
            await Shot(dir, name, Mode.Expanded);
        }
        await Show("40-declarative-cards", "github-repo-radar", "countdown-milestone");
        await Show("40b-declarative-cards", "npm-package-stats", "exchange-rate-watch");
        await Show("40c-declarative-cards", "hn-top-pulse", "quick-dev-links");
        host.DemoError("exchange-rate-watch", "请求超时（8 秒）");
        await Show("44-declarative-error-state", "exchange-rate-watch", "quick-dev-links");
        _widgetList = null;
    }
}
