using System.Collections.Concurrent;
using System.IO;

namespace Qisland.App;

/// <summary>
/// 各项提示的开关：%APPDATA%\Qisland\features\&lt;键&gt;.off 存在就是关。
/// 只在启动时和托盘里改的时候读写文件，平时查的是内存。
/// </summary>
public static class Features
{
    public const string Privacy = "privacy", Calendar = "calendar", Calls = "calls", Focus = "focus",
        Bluetooth = "bluetooth", Network = "network", LockKeys = "lockkeys",
        WeatherAlerts = "weather-alerts", Water = "water", Sit = "sit", OledShift = "oled-shift", Clock12 = "clock-12h",
        ClickLockHotkey = "click-lock-hotkey", HideList = "hide-list", HideOnlyCovering = "hide-only-covering",
        PlayerLyricOffset = "player-lyric-offset", HideFromCapture = "hide-from-capture";

    /// <summary>
    /// 默认关、要自己打开的（文件是 &lt;键&gt;.on）：喝水 / 久坐提醒会主动打扰，OLED 防烧屏会挪动岛；
    /// 「只在挡住岛时才藏」会改变隐藏名单原来的行为；截图录屏时隐藏会让自己截图、录教程时岛也拍不到。
    /// </summary>
    private static readonly HashSet<string> OptIn = [Water, Sit, OledShift, Clock12, HideOnlyCovering, HideFromCapture];

    private static readonly ConcurrentDictionary<string, bool> Cache = new();
    private static string Dir => Path.Combine(App.DataDir, "features");
    public static event Action<string>? Changed;

    public static bool On(string key) => Cache.GetOrAdd(key, k => OptIn.Contains(k) ? File.Exists(Path.Combine(Dir, k + ".on")) : !File.Exists(Path.Combine(Dir, k + ".off")));

    public static void Set(string key, bool on)
    {
        Cache[key] = on;
        try
        {
            Directory.CreateDirectory(Dir);
            if (OptIn.Contains(key))
            {
                var f = Path.Combine(Dir, key + ".on");
                if (on) File.WriteAllText(f, ""); else File.Delete(f);
            }
            else
            {
                var f = Path.Combine(Dir, key + ".off");
                if (on) File.Delete(f); else File.WriteAllText(f, "");
            }
        }
        catch (Exception ex) { App.Log($"开关 {key}: {ex.Message}"); }
        App.Log($"开关 {key} → {(on ? "开" : "关")}");
        Changed?.Invoke(key);
    }
}

/// <summary>各个监听器的事件计数（进 /v1/perf），用来确认它们真的是事件驱动、几乎不花钱。</summary>
public static class Counters
{
    private static readonly ConcurrentDictionary<string, long> Map = new();
    public static void Hit(string key) => Map.AddOrUpdate(key, 1, (_, v) => v + 1);
    public static string Json() => "{" + string.Join(",", Map.OrderBy(k => k.Key).Select(k => $"\"{k.Key}\":{k.Value}")) + "}";
}
