using System.Globalization;

namespace Qisland.Core;

public enum PeripheralKind { Mouse, Keyboard, Headset, Gamepad, Pen, Phone, Other }

/// <summary>一个读到了电量的无线外设。读不到电量的设备根本不进这个列表。</summary>
public sealed record Peripheral(string Id, string Name, PeripheralKind Kind, int Battery, string Via);

/// <summary>外设电量：分类、格式化、低电量提醒防抖。纯逻辑，可在 Linux 上单测。</summary>
public static class Peripherals
{
    public const int Low = 20, VeryLow = 10;

    /// <summary>
    /// 经典蓝牙设备类别（Class of Device）：主类别 1 电脑 2 电话 4 音频 5 外设（次类别里键盘 0x10 / 鼠标 0x20 / 手柄 0x01–0x02 / 数位板 0x05）。
    /// BLE 外观值（Appearance）：0x03C1 键盘 0x03C2 鼠标 0x03C3/0x03C4 手柄 0x03C5 数位板 0x03C7 触控笔，0x0940–0x097F 耳机类。
    /// 两样都没有就按名字猜；猜不出是 Other（照样显示，只是图标通用）。
    /// </summary>
    public static PeripheralKind Classify(string name, int? codMajor = null, int? codMinor = null, int? appearance = null)
    {
        if (appearance is { } a and > 0)
        {
            switch (a)
            {
                case 0x03C1: return PeripheralKind.Keyboard;
                case 0x03C2: return PeripheralKind.Mouse;
                case 0x03C3 or 0x03C4: return PeripheralKind.Gamepad;
                case 0x03C5 or 0x03C7: return PeripheralKind.Pen;
            }
            var cat = a >> 6;
            if (cat == 0x0F) { /* HID 其他 */ }
            else if (cat == 0x025 || a is >= 0x0940 and <= 0x097F || cat == 0x021) return PeripheralKind.Headset;   // 0x0941 耳机 / 0x0841 头戴
            else if (cat == 0x001) return PeripheralKind.Phone;
        }
        if (codMajor is { } maj)
        {
            if (maj == 4) return PeripheralKind.Headset;
            if (maj == 2) return PeripheralKind.Phone;
            if (maj == 5 && codMinor is { } min)
            {
                var kb = (min & 0x10) != 0; var pt = (min & 0x20) != 0;
                if (pt && !kb) return PeripheralKind.Mouse;
                if (kb) return PeripheralKind.Keyboard;
                if ((min & 0x0F) is 1 or 2) return PeripheralKind.Gamepad;
                if ((min & 0x0F) is 5) return PeripheralKind.Pen;
            }
        }
        return ByName(name);
    }

    public static PeripheralKind ByName(string name)
    {
        bool Has(params string[] ks) => ks.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (Has("mouse", "鼠标", "MX Master", "MX Anywhere", "Trackball", "G502", "G304", "Viper")) return PeripheralKind.Mouse;
        if (Has("keyboard", "键盘", "Keychron", "MX Keys", "K380", "K780", "HHKB")) return PeripheralKind.Keyboard;
        if (Has("controller", "gamepad", "手柄", "Xbox", "DualSense", "DualShock", "Joy-Con", "Pro Controller", "8BitDo")) return PeripheralKind.Gamepad;
        if (Has("pen", "stylus", "触控笔", "Pencil")) return PeripheralKind.Pen;
        if (Has("耳机", "耳麦", "音箱", "Buds", "AirPods", "Headphone", "Headset", "Earbuds", "WH-", "WF-", "FreeBuds", "FreeClip", "Beats", "Soundcore", "Speaker", "Enco", "Fit900", "Bass BF")) return PeripheralKind.Headset;
        if (Has("iPhone", "Phone", "手机", "Galaxy", "Pixel")) return PeripheralKind.Phone;
        return PeripheralKind.Other;
    }

    public static string KindName(PeripheralKind k) => k switch
    {
        PeripheralKind.Mouse => "鼠标",
        PeripheralKind.Keyboard => "键盘",
        PeripheralKind.Headset => "耳机",
        PeripheralKind.Gamepad => "手柄",
        PeripheralKind.Pen => "触控笔",
        PeripheralKind.Phone => "手机",
        _ => "设备",
    };

    /// <summary>名字太长的截短（岛上一行放不下）：保留前 14 个字。</summary>
    public static string ShortName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "无线设备";
        return n.Length > 14 ? n[..13] + "…" : n;
    }

    public static string Percent(int pct) => Math.Clamp(pct, 0, 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>电量合法才算读到（0–100）；系统偶尔给 255 / -1 之类的「未知」。</summary>
    public static bool Valid(int? pct) => pct is >= 0 and <= 100;

    /// <summary>排序：电量低的在前，同电量按名字。</summary>
    public static IReadOnlyList<Peripheral> Sort(IEnumerable<Peripheral> list) =>
        list.OrderBy(p => p.Battery).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
}

/// <summary>
/// 低电量提醒防抖：每个设备掉到 20% 提醒一次，掉到 10% 再提醒一次；充上电（回到 30% 以上）后重新计。
/// </summary>
public sealed class LowBatteryAlerts
{
    private readonly Dictionary<string, int> _warned = new(StringComparer.Ordinal);

    /// <summary>返回该提醒的档位（20 / 10），不该提醒返回 0。</summary>
    public int Check(string id, int pct)
    {
        var w = _warned.GetValueOrDefault(id, 100);
        if (pct >= 30) { _warned.Remove(id); return 0; }
        if (pct <= Peripherals.VeryLow && w > Peripherals.VeryLow) { _warned[id] = Peripherals.VeryLow; return Peripherals.VeryLow; }
        if (pct <= Peripherals.Low && w > Peripherals.Low) { _warned[id] = Peripherals.Low; return Peripherals.Low; }
        return 0;
    }
}

/// <summary>收起胶囊上的一行系统状态：CPU% / 内存% / 网速，挑哪几项由设置决定。</summary>
public static class CompactStats
{
    [Flags] public enum Items { None = 0, Cpu = 1, Mem = 2, Net = 4 }

    public static Items Parse(string? s)
    {
        var r = Items.None;
        foreach (var p in (s ?? "").Split(',', ' ', ';'))
            r |= p.Trim().ToLowerInvariant() switch { "cpu" => Items.Cpu, "mem" or "memory" => Items.Mem, "net" => Items.Net, _ => Items.None };
        return r == Items.None ? Items.Cpu | Items.Mem : r;
    }

    public static string Serialize(Items i) =>
        string.Join(",", new[] { (Items.Cpu, "cpu"), (Items.Mem, "mem"), (Items.Net, "net") }.Where(x => i.HasFlag(x.Item1)).Select(x => x.Item2));

    /// <summary>网速压成最多 4 个字符：0K / 850K / 1.2M / 12M。</summary>
    public static string Speed(double bytesPerSec)
    {
        if (!double.IsFinite(bytesPerSec) || bytesPerSec < 0) bytesPerSec = 0;
        var k = bytesPerSec / 1024;
        if (k < 1000) return k.ToString("0", CultureInfo.InvariantCulture) + "K";
        var m = k / 1024;
        return (m < 10 ? m.ToString("0.0", CultureInfo.InvariantCulture) : m.ToString("0", CultureInfo.InvariantCulture)) + "M";
    }

    public static string Pct(double fraction) => Math.Clamp(Math.Round(fraction * 100), 0, 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
