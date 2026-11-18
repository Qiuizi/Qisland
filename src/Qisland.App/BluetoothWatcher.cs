using System.Windows.Threading;
using Qisland.Core;
using Windows.Devices.Enumeration;

namespace Qisland.App;

/// <summary>
/// 蓝牙耳机连上 / 断开 + 电量。
/// 用系统的 DeviceWatcher 看已配对的经典蓝牙设备的 IsConnected（系统推事件，不轮询），只管音频类（耳机 / 音箱）——
/// 鼠标键盘睡醒重连太频繁，不报。
/// 电量读设备节点上的 DEVPKEY_Bluetooth_Battery（走免提协议 HFP 上报电量的耳机才有），读不到就不显示电量；
/// 连着的时候每 10 分钟看一次，掉到 20% / 10% 各提醒一次。
/// </summary>
public sealed class BluetoothWatcher
{
    private const string IsConnected = "System.Devices.Aep.IsConnected";
    private const string Major = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string Container = "System.Devices.Aep.ContainerId";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string Aqs = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";

    private readonly ActivityStore _store;
    private readonly Dispatcher _ui;
    private DeviceWatcher? _watcher;
    private bool _ready;

    private sealed class Dev
    {
        public string Id = "", Name = ""; public bool Audio, Connected; public Guid? ContainerId;
        public int? Battery; public DateTime BatteryAt; public bool Warned20, Warned10;
    }
    private readonly Dictionary<string, Dev> _devs = [];
    public DateTime LastConnect { get; private set; } = DateTime.MinValue;
    public string LastName { get; private set; } = "";

    public BluetoothWatcher(ActivityStore store, Dispatcher ui) { _store = store; _ui = ui; }

    public IEnumerable<(string Name, int? Battery)> Connected => _devs.Values.Where(d => d.Connected && d.Audio).Select(d => (d.Name, d.Battery));

    public void Start()
    {
        if (_watcher != null) return;
        try
        {
            _ready = false;
            _watcher = DeviceInformation.CreateWatcher(Aqs, [IsConnected, Major, Container, "System.ItemNameDisplay"], DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, info) => _ui.BeginInvoke(() => OnAdded(info));
            _watcher.Updated += (_, upd) => _ui.BeginInvoke(() => OnUpdated(upd));
            _watcher.Removed += (_, upd) => _ui.BeginInvoke(() => _devs.Remove(upd.Id));
            _watcher.EnumerationCompleted += (_, _) => _ui.BeginInvoke(() =>
            {
                _ready = true;
                var on = _devs.Values.Where(d => d.Connected && d.Audio).ToList();
                App.Log($"蓝牙监听: 已开始，{_devs.Count} 个已配对设备，{_devs.Values.Count(d => d.Audio)} 个音频设备，连着 {on.Count} 个");
                foreach (var d in on) _ = ReadBattery(d, announce: false);
            });
            _watcher.Stopped += (_, _) => App.Log("蓝牙监听: 已停止");
            _watcher.Start();
        }
        catch (Exception ex) { App.Log("蓝牙监听: 启动失败 " + ex.Message); _watcher = null; }
    }

    public void Stop()
    {
        try { if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted }) _watcher.Stop(); } catch { }
        _watcher = null;
        _devs.Clear();
    }

    private void OnAdded(DeviceInformation info)
    {
        Counters.Hit("bt.event");
        var d = new Dev { Id = info.Id, Name = info.Name };
        Apply(d, info.Properties);
        _devs[info.Id] = d;
    }

    private void OnUpdated(DeviceInformationUpdate upd)
    {
        Counters.Hit("bt.event");
        if (!_devs.TryGetValue(upd.Id, out var d)) return;
        var was = d.Connected;
        Apply(d, upd.Properties);
        if (!_ready || was == d.Connected || !d.Audio) return;
        if (d.Connected)
        {
            LastConnect = DateTime.Now; LastName = d.Name;
            d.Battery = null; d.Warned10 = d.Warned20 = false;
            App.Log("蓝牙: 音频设备已连接");
            if (Features.On(Features.Bluetooth))
            {
                _store.Remove("audio:device");   // 系统换输出设备的那条「已连接耳机」和这条说的是一回事
                _store.Upsert(new IslandActivity("bt:" + d.Id, "bluetooth", $"已连接 {d.Name}", "蓝牙耳机", ActivityKind.Done, 62, DurationSeconds: 5, ResetTtl: true));
            }
            _ = ReadBattery(d, announce: true);
        }
        else
        {
            App.Log("蓝牙: 音频设备已断开");
            if (Features.On(Features.Bluetooth))
                _store.Upsert(new IslandActivity("bt:" + d.Id, "bluetooth", $"{d.Name} 已断开", d.Battery is { } b ? $"断开前电量 {b}%" : "蓝牙耳机", ActivityKind.Info, 55, DurationSeconds: 4, ResetTtl: true));
        }
    }

    private static void Apply(Dev d, IReadOnlyDictionary<string, object> props)
    {
        if (props.TryGetValue("System.ItemNameDisplay", out var n) && n is string s && s.Length > 0) d.Name = s;
        if (props.TryGetValue(IsConnected, out var c) && c is bool b) d.Connected = b;
        if (props.TryGetValue(Container, out var g) && g is Guid gid) d.ContainerId = gid;
        if (props.TryGetValue(Major, out var m) && m != null)
        {
            try { d.Audio = Convert.ToInt32(m) == 4; } catch { }
        }
        // 有的耳机类别报得不对：按名字兜底
        if (!d.Audio && LooksLikeHeadset(d.Name)) d.Audio = true;
    }

    internal static bool LooksLikeHeadset(string name) =>
        new[] { "耳机", "耳麦", "音箱", "Buds", "AirPods", "Headphone", "Headset", "Earbuds", "WH-", "WF-", "FreeBuds", "FreeClip", "Beats", "QC", "Soundcore", "Speaker" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>每分钟调一次：连着的耳机 10 分钟读一次电量。</summary>
    public void Tick()
    {
        foreach (var d in _devs.Values.Where(d => d.Connected && d.Audio && DateTime.Now - d.BatteryAt > TimeSpan.FromMinutes(10)).ToList())
            _ = ReadBattery(d, announce: false);
    }

    private async Task ReadBattery(Dev d, bool announce)
    {
        d.BatteryAt = DateTime.Now;
        if (d.ContainerId is not { } cid) return;
        if (announce) await Task.Delay(TimeSpan.FromSeconds(4));   // 刚连上时 HFP 还没把电量报上来
        int? pct = null;
        try
        {
            Counters.Hit("bt.battery");
            var list = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{cid}}}\"", [BatteryKey], DeviceInformationKind.Device);
            foreach (var i in list)
                if (i.Properties.TryGetValue(BatteryKey, out var v) && v != null) { pct = Convert.ToInt32(v); break; }
        }
        catch (Exception ex) { App.Log("蓝牙电量: " + ex.Message); }
        if (pct is not { } p || p < 0 || p > 100) { if (announce) App.Log("蓝牙电量: 这个设备没有上报电量"); return; }
        var first = d.Battery == null;
        d.Battery = p;
        if (first) App.Log($"蓝牙电量: {p}%");
        if (!Features.On(Features.Bluetooth) || !d.Connected) return;
        if (announce && _store.Get("bt:" + d.Id) is { } live && live.Title.StartsWith("已连接"))
            _store.Upsert(new IslandActivity("bt:" + d.Id, "bluetooth", $"已连接 {d.Name}", $"电量 {p}%", ActivityKind.Done, 62, DurationSeconds: 4, ResetTtl: true), progress: p / 100.0);
        if (p <= 10 && !d.Warned10) { d.Warned10 = d.Warned20 = true; Low(d, p); }
        else if (p <= 20 && !d.Warned20) { d.Warned20 = true; Low(d, p); }
    }

    private void Low(Dev d, int p) =>
        _store.Upsert(new IslandActivity("bt:low:" + d.Id, "bluetooth-low", $"{d.Name} 电量 {p}%", "该充电了", ActivityKind.Warning, 70, DurationSeconds: 8, ResetTtl: true), progress: p / 100.0);
}
