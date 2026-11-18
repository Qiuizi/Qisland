using System.Windows.Threading;
using Microsoft.Win32;
using Qisland.Core;
using Windows.Networking.Connectivity;

namespace Qisland.App;

/// <summary>
/// 网络状态：断网 / 恢复、换了 Wi-Fi、连上了但要网页认证（校园网）、系统代理开关。
/// 全是事件驱动：NetworkInformation.NetworkStatusChanged + Internet Settings 注册表通知；
/// 状态要稳定 3 秒才说（Wi-Fi 切换、睡眠唤醒时会连抖好几下），刚唤醒的 25 秒不报断网。
/// </summary>
public sealed class NetworkWatcher : IDisposable
{
    private const string ProxyKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private readonly ActivityStore _store;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _settle, _proxySettle;
    private RegWatch? _proxyWatch;
    private bool _started;

    private enum Net { Unknown, Offline, Local, Captive, Online }
    private Net _state = Net.Unknown;
    private string _name = "";
    private DateTime _offlineSince = DateTime.MinValue;
    private (bool on, string server, string pac)? _proxy;

    public NetworkWatcher(ActivityStore store, Dispatcher ui)
    {
        _store = store; _ui = ui;
        _settle = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(3) };
        _settle.Tick += (_, _) => { _settle.Stop(); Evaluate(announce: true); };
        _proxySettle = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _proxySettle.Tick += (_, _) => { _proxySettle.Stop(); CheckProxy(announce: true); };
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        try { NetworkInformation.NetworkStatusChanged += OnStatus; }
        catch (Exception ex) { App.Log("网络监听: " + ex.Message); }
        _proxyWatch = new RegWatch("proxy", [(Registry.CurrentUser, ProxyKey, false)],
            () => _ui.BeginInvoke(() => { _proxySettle.Stop(); _proxySettle.Start(); }));
        Evaluate(announce: false);
        CheckProxy(announce: false);
        App.Log($"网络监听: 已开始（{Describe()}，代理 {(_proxy?.on == true ? "开" : "关")}）");
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { NetworkInformation.NetworkStatusChanged -= OnStatus; } catch { }
        _proxyWatch?.Dispose(); _proxyWatch = null;
        _settle.Stop(); _proxySettle.Stop();
        _state = Net.Unknown; _proxy = null;
    }

    private void OnStatus(object? sender)
    {
        Counters.Hit("net.event");
        _ui.BeginInvoke(() => { _settle.Stop(); _settle.Start(); });
    }

    /// <summary>从睡眠醒来：先按当前状态静默同步，不把「睡前断网 → 醒来恢复」当新闻。</summary>
    public void OnResume() { if (_started) _ui.BeginInvoke(() => Evaluate(announce: false), DispatcherPriority.Background); }

    private (Net, string) Read()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return (Net.Offline, "");
            var name = "";
            try { if (p.IsWlanConnectionProfile) name = p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? ""; } catch { }
            if (string.IsNullOrEmpty(name)) name = p.ProfileName ?? "";
            var level = p.GetNetworkConnectivityLevel();
            return (level switch
            {
                NetworkConnectivityLevel.InternetAccess => Net.Online,
                NetworkConnectivityLevel.ConstrainedInternetAccess => Net.Captive,
                NetworkConnectivityLevel.LocalAccess => Net.Local,
                _ => Net.Offline,
            }, name);
        }
        catch (Exception ex) { App.Log("网络状态: " + ex.Message); return (Net.Unknown, ""); }
    }

    private string Describe() => _state switch
    {
        Net.Online => "在线" + (_name.Length > 0 ? " · " + _name : ""),
        Net.Captive => "需要认证" + (_name.Length > 0 ? " · " + _name : ""),
        Net.Local => "无 Internet" + (_name.Length > 0 ? " · " + _name : ""),
        Net.Offline => "离线",
        _ => "未知",
    };

    private void Evaluate(bool announce)
    {
        Counters.Hit("net.eval");
        var (s, name) = Read();
        if (s == Net.Unknown) return;
        var prev = _state; var prevName = _name;
        _state = s; _name = name;
        if (s == prev && name == prevName) return;
        App.Log($"网络: {Describe()}");
        if (s is Net.Offline or Net.Local) { if (_offlineSince == DateTime.MinValue) _offlineSince = DateTime.Now; }
        if (!announce || prev == Net.Unknown || !Features.On(Features.Network)) { if (s == Net.Online) _offlineSince = DateTime.MinValue; return; }
        var waking = DateTime.Now - App.ResumedAt < TimeSpan.FromSeconds(25);
        var label = name.Length > 0 ? name : "网络";
        switch (s)
        {
            case Net.Offline when !waking:
                _store.Upsert(new IslandActivity("net:state", "network-off", "网络已断开", prevName.Length > 0 ? $"刚才连的是 {prevName}" : "", ActivityKind.Warning, 60, DurationSeconds: 6, ResetTtl: true));
                break;
            case Net.Local when !waking:
                _store.Upsert(new IslandActivity("net:state", "network-off", $"{label} 上不了网", "已连接，但没有 Internet", ActivityKind.Warning, 60, DurationSeconds: 6, ResetTtl: true));
                break;
            case Net.Captive:
                // 校园网这类要先在网页上登录：点一下打开系统的认证检测页，会被重定向到登录页
                _store.Upsert(new IslandActivity("net:state", "network-off", $"{label} 需要网页认证", "点这里打开登录页", ActivityKind.Warning, 70, DurationSeconds: 12, ResetTtl: true,
                    OpenUri: "http://www.msftconnecttest.com/redirect"));
                break;
            case Net.Online:
                var wasDown = prev is Net.Offline or Net.Local or Net.Captive;
                var downFor = _offlineSince == DateTime.MinValue ? TimeSpan.Zero : DateTime.Now - _offlineSince;
                _offlineSince = DateTime.MinValue;
                if (wasDown && !waking)
                    _store.Upsert(new IslandActivity("net:state", "network", "网络已恢复", name.Length > 0 ? name + (downFor.TotalMinutes >= 1 ? $" · 断了 {(int)downFor.TotalMinutes} 分钟" : "") : "", ActivityKind.Done, 50, DurationSeconds: 4, ResetTtl: true));
                else if (!wasDown && prevName.Length > 0 && name != prevName)
                    _store.Upsert(new IslandActivity("net:state", "network", $"已切换到 {label}", $"之前是 {prevName}", ActivityKind.Done, 50, DurationSeconds: 4, ResetTtl: true));
                else _store.Remove("net:state");
                break;
        }
    }

    private void CheckProxy(bool announce)
    {
        (bool on, string server, string pac) now;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(ProxyKey);
            now = ((k?.GetValue("ProxyEnable") is int e ? e : 0) != 0, k?.GetValue("ProxyServer") as string ?? "", k?.GetValue("AutoConfigURL") as string ?? "");
        }
        catch { return; }
        var prev = _proxy;
        _proxy = now;
        if (prev == null || prev.Value.on == now.on && prev.Value.pac.Length > 0 == now.pac.Length > 0 && (!now.on || prev.Value.server == now.server)) return;
        var desc = now.on ? Short(now.server) : now.pac.Length > 0 ? "PAC 自动配置" : "";
        App.Log($"系统代理: {(now.on || now.pac.Length > 0 ? "开 " + desc : "关")}");
        if (!announce || !Features.On(Features.Network)) return;
        var onAny = now.on || now.pac.Length > 0;
        _store.Upsert(new IslandActivity("net:proxy", "proxy", onAny ? "系统代理已开启" : "系统代理已关闭", desc, onAny ? ActivityKind.Done : ActivityKind.Info, 45, DurationSeconds: 4, ResetTtl: true));
    }

    // "http=127.0.0.1:7890;https=127.0.0.1:7890" → 127.0.0.1:7890
    private static string Short(string server)
    {
        var first = server.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var eq = first.IndexOf('=');
        return eq >= 0 ? first[(eq + 1)..] : first;
    }

    public void Dispose() => Stop();
}
