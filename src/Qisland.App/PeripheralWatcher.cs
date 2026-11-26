using Qisland.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace Qisland.App;

/// <summary>
/// 无线外设电量：鼠标、键盘、手柄、触控笔、耳机……只显示真的读到电量的，读不到的一律不显示。
/// 三个来源，都是系统公开接口、只读：
///  1. 已连接蓝牙设备节点上的 DEVPKEY_Bluetooth_Battery（Windows 设置「蓝牙和设备」里显示的那个电量，经典蓝牙和 BLE 都有）；
///  2. BLE 设备的标准电池服务（GATT 0x180F / 0x2A19），只读系统缓存值（BluetoothCacheMode.Cached），不额外唤醒设备；
///  3. Xbox 无线手柄（Windows.Gaming.Input 的电池报告）。
/// 平时 5 分钟看一次；关掉开关就完全不跑。
/// </summary>
public sealed class PeripheralWatcher
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ClassicProto = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}", LeProto = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
    private const string IsConnected = "System.Devices.Aep.IsConnected", Container = "System.Devices.Aep.ContainerId";
    private const string CodMajor = "System.Devices.Aep.Bluetooth.Cod.Major", CodMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
    private const string Appearance = "System.Devices.Aep.Bluetooth.Le.Appearance";

    private readonly ActivityStore _store;
    private readonly Action<Action> _post;
    private readonly LowBatteryAlerts _alerts = new();
    private DateTime _next = DateTime.MinValue;
    private int _busy;

    public IReadOnlyList<Peripheral> Devices { get; private set; } = [];
    public DateTime LastScan { get; private set; }
    public event Action? Changed;

    public PeripheralWatcher(ActivityStore store, Action<Action> post)
    {
        _store = store; _post = post;
        // 桌面程序里 Gamepad.Gamepads 要先有人订阅过 GamepadAdded 才会开始填充
        try { Windows.Gaming.Input.Gamepad.GamepadAdded += (_, _) => _next = DateTime.MinValue; } catch { }
    }

    /// <summary>App 每 10 秒调一次。</summary>
    public void Tick()
    {
        if (!Features.On(Features.Peripherals)) { if (Devices.Count > 0) { Devices = []; Changed?.Invoke(); } return; }
        if (DateTime.Now >= _next) Scan();
    }

    /// <summary>翻到组件页、蓝牙设备刚连上时：1 分钟内没看过就马上看一次。</summary>
    public void Soon()
    {
        if (DateTime.Now - LastScan > TimeSpan.FromSeconds(60)) _next = DateTime.MinValue;
    }

    public void Scan()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        _next = DateTime.Now.AddMinutes(5);
        _ = Task.Run(async () =>
        {
            try
            {
                var list = await ReadAll();
                _post(() => Apply(list));
            }
            catch (Exception ex) { App.Log("外设电量: " + ex.GetType().Name); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private void Apply(List<Peripheral> list)
    {
        LastScan = DateTime.Now;
        var sorted = Peripherals.Sort(list);
        var changed = sorted.Count != Devices.Count || sorted.Zip(Devices).Any(p => p.First.Id != p.Second.Id || p.First.Battery != p.Second.Battery);
        if (changed)
            App.Log(sorted.Count == 0 ? "外设电量: 没有读到电量的已连接设备"
                : "外设电量: " + string.Join("，", sorted.Select(p => $"{Peripherals.KindName(p.Kind)} {p.Battery}%（{p.Via}）")));
        Devices = sorted;
        foreach (var p in sorted)
        {
            // 耳机的低电量 BluetoothWatcher 已经在提醒（开着「蓝牙耳机」提示时），这里不重复
            if (p.Kind == PeripheralKind.Headset && Features.On(Features.Bluetooth)) continue;
            var level = _alerts.Check(p.Id, p.Battery);
            if (level == 0 || !Features.On(Features.PeripheralAlerts)) continue;
            _store.Upsert(new IslandActivity("dev:low:" + p.Id, "bluetooth-low", $"{Peripherals.ShortName(p.Name)} 电量 {p.Battery}%",
                level <= Peripherals.VeryLow ? $"{Peripherals.KindName(p.Kind)}快没电了" : $"{Peripherals.KindName(p.Kind)}该充电了",
                ActivityKind.Warning, level <= Peripherals.VeryLow ? 72 : 66, DurationSeconds: 8, ResetTtl: true), progress: p.Battery / 100.0);
        }
        if (changed) Changed?.Invoke();
    }

    private static async Task<List<Peripheral>> ReadAll()
    {
        var res = new List<Peripheral>();
        var seen = new HashSet<Guid>();
        var aqs = $"(System.Devices.Aep.ProtocolId:=\"{ClassicProto}\" OR System.Devices.Aep.ProtocolId:=\"{LeProto}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
        DeviceInformationCollection aeps;
        try { aeps = await DeviceInformation.FindAllAsync(aqs, [IsConnected, Container, CodMajor, CodMinor, Appearance], DeviceInformationKind.AssociationEndpoint); }
        catch (Exception ex) { App.Log("外设电量: 枚举蓝牙设备失败 " + ex.GetType().Name); aeps = null!; }
        foreach (var d in (IEnumerable<DeviceInformation>?)aeps ?? [])
        {
            Counters.Hit("dev.aep");
            if (!(d.Properties.TryGetValue(IsConnected, out var c) && c is true)) continue;
            if (!(d.Properties.TryGetValue(Container, out var g) && g is Guid cid) || cid == Guid.Empty || !seen.Add(cid)) continue;
            var name = string.IsNullOrWhiteSpace(d.Name) ? "无线设备" : d.Name.Trim();
            int? Int(string k) { try { return d.Properties.TryGetValue(k, out var v) && v != null ? Convert.ToInt32(v) : null; } catch { return null; } }
            var kind = Peripherals.Classify(name, Int(CodMajor), Int(CodMinor), Int(Appearance));
            var isLe = d.Id.StartsWith("BluetoothLE", StringComparison.OrdinalIgnoreCase);
            var pct = await DevBattery(cid);
            var via = "系统";
            if (!Peripherals.Valid(pct) && isLe) { pct = await GattBattery(d.Id); via = "BLE"; }
            if (!Peripherals.Valid(pct)) continue;   // 读不到电量的不显示
            res.Add(new Peripheral(cid.ToString("N"), name, kind, pct!.Value, via));
        }
        foreach (var p in Gamepads()) res.Add(p);
        return res;
    }

    private static async Task<int?> DevBattery(Guid container)
    {
        try
        {
            Counters.Hit("dev.battery");
            var list = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey], DeviceInformationKind.Device);
            foreach (var i in list)
                if (i.Properties.TryGetValue(BatteryKey, out var v) && v != null)
                {
                    var p = Convert.ToInt32(v);
                    if (Peripherals.Valid(p)) return p;
                }
        }
        catch { }
        return null;
    }

    /// <summary>BLE 电池服务：只读缓存值，不建立新连接、不订阅通知。</summary>
    private static async Task<int?> GattBattery(string aepId)
    {
        BluetoothLEDevice? dev = null;
        try
        {
            Counters.Hit("dev.gatt");
            dev = await BluetoothLEDevice.FromIdAsync(aepId);
            if (dev == null) return null;
            var svcs = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached);
            if (svcs.Status != GattCommunicationStatus.Success) return null;
            foreach (var s in svcs.Services)
            {
                using (s)
                {
                    var chars = await s.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Cached);
                    if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0) continue;
                    var r = await chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Cached);
                    if (r.Status != GattCommunicationStatus.Success || r.Value == null || r.Value.Length < 1) continue;
                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(r.Value);
                    int p = reader.ReadByte();
                    if (Peripherals.Valid(p)) return p;
                }
            }
        }
        catch { }
        finally { dev?.Dispose(); }
        return null;
    }

    private static IEnumerable<Peripheral> Gamepads()
    {
        var list = new List<Peripheral>();
        try
        {
            var i = 0;
            foreach (var g in Windows.Gaming.Input.Gamepad.Gamepads)
            {
                i++;
                if (!g.IsWireless) continue;
                var r = g.TryGetBatteryReport();
                if (r?.RemainingCapacityInMilliwattHours is not { } rem || r.FullChargeCapacityInMilliwattHours is not { } full || full <= 0) continue;
                var p = (int)Math.Round(rem * 100.0 / full);
                if (Peripherals.Valid(p)) list.Add(new Peripheral("pad" + i, "Xbox 手柄", PeripheralKind.Gamepad, p, "手柄"));
            }
        }
        catch { }
        return list;
    }

    /// <summary>演示用（--snapshot）。</summary>
    public void Demo()
    {
        Devices = Peripherals.Sort([new("m", "MX Master 3S", PeripheralKind.Mouse, 18, "系统"), new("k", "Keychron K3", PeripheralKind.Keyboard, 76, "BLE"), new("h", "AirPods Pro", PeripheralKind.Headset, 64, "系统")]);
    }
}
