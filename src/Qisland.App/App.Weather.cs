using System.IO;
using System.Windows;
using Qisland.Core;
using Forms = System.Windows.Forms;

namespace Qisland.App;

/// <summary>
/// 天气：多城市（最多 5 个，存在本机 weather-cities.json）、城市搜索、按城市缓存、立即刷新、本地天气提醒。
/// 选「自动定位」时仍按 weather.txt / 网络出口定位，和以前一样。
/// </summary>
public sealed partial class App
{
    private static string CitiesFile => Path.Combine(DataDir, "weather-cities.json");
    private static WeatherCities? _cities;
    public static WeatherCities WeatherCityList => _cities ??= WeatherCities.FromJson(File.Exists(CitiesFile) ? SafeRead(CitiesFile) : null);
    private TaskCompletionSource _weatherWake = new();
    /// <summary>每个城市最近一份天气：切城市时先显示缓存，再联网刷新。键 = 城市标签（自动定位用空串）。</summary>
    private readonly Dictionary<string, (WeatherSnapshot Now, WeatherDetail Detail, string Label)> _wxCache = [];
    private readonly HashSet<string> _alerted = [];

    private static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }

    private static void SaveCities()
    {
        try { File.WriteAllText(CitiesFile, WeatherCityList.ToJson()); }
        catch (Exception ex) { Log("保存天气城市: " + ex.Message); }
    }

    private void ShowWeather(string label, WeatherSnapshot now, WeatherDetail d)
    {
        WeatherLabel = label;
        _wxCache[WeatherCityList.Current?.Label ?? ""] = (now, d, label);
        _window?.SetWeather(now, d);
        AnnounceWeatherAlerts(d);
    }

    /// <summary>切换城市（-1 = 自动定位）：先换上缓存，再马上联网刷新。</summary>
    public void SwitchWeatherCity(int index)
    {
        var list = WeatherCityList;
        list.Active = index >= 0 && index < list.Saved.Count ? index : -1;
        SaveCities();
        var key = list.Current?.Label ?? "";
        if (_wxCache.TryGetValue(key, out var c)) { WeatherLabel = c.Label; _window?.SetWeather(c.Now, c.Detail); }
        else if (list.Current is { } city) WeatherLabel = city.Label;
        Log($"天气城市 → {(list.Current == null ? "自动定位" : "已保存城市 #" + (list.Active + 1))}");  // 不把地名写进日志
        RefreshWeatherNow();
    }

    public void RefreshWeatherNow() => _weatherWake.TrySetResult();

    /// <summary>城市搜索（Open-Meteo 地理编码，按中文习惯补查后缀 / 省会）。</summary>
    public static async Task<List<WeatherCity>> SearchCities(string name, CancellationToken ct)
    {
        var found = new List<(WeatherCity, long)>();
        Exception? last = null;
        foreach (var q in OpenMeteo.CityQueries(name))
        {
            try { found.AddRange(OpenMeteo.ParseCities(await Http.GetStringAsync(OpenMeteo.GeocodeUrl(q), ct))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; }
        }
        if (found.Count == 0 && last != null) throw last;
        return OpenMeteo.RankCities(name, found);
    }

    /// <summary>加入城市列表，返回位置（已在列表里就返回原位置），满了返回 -1。</summary>
    public static int AddWeatherCity(WeatherCity c)
    {
        var i = WeatherCityList.Add(c);
        if (i >= 0) SaveCities();
        return i;
    }

    public static void RemoveWeatherCity(int i) { WeatherCityList.Remove(i); SaveCities(); }

    /// <summary>天气提醒：同一件事一天只提醒一次；只在「岛上提示 → 天气提醒」打开时弹。</summary>
    private void AnnounceWeatherAlerts(WeatherDetail d)
    {
        if (!Features.On(Features.WeatherAlerts)) return;
        foreach (var a in d.Alerts)
        {
            if (!_alerted.Add(a.Key)) continue;
            // 单独的 source（weather-alert）：按普通通知排序上列表和胶囊；普通天气数据仍不进主队列
            _store.Upsert(WeatherAlertCards.ToActivity(a));
            Log($"天气提醒: {a.Title}");
        }
        if (_alerted.Count > 200) _alerted.Clear();
    }

    private Forms.ToolStripMenuItem WeatherMenu()
    {
        var m = new Forms.ToolStripMenuItem("天气");
        m.DropDownItems.Add("…");
        m.DropDownOpening += (_, _) =>
        {
            m.DropDownItems.Clear();
            var list = WeatherCityList;
            var auto = new Forms.ToolStripMenuItem("自动定位") { Checked = list.Current == null, ToolTipText = "按 weather.txt 或网络出口位置" };
            auto.Click += (_, _) => Dispatcher.BeginInvoke(() => SwitchWeatherCity(-1));
            m.DropDownItems.Add(auto);
            for (var i = 0; i < list.Saved.Count; i++)
            {
                var idx = i;
                var c = list.Saved[i];
                var item = new Forms.ToolStripMenuItem(c.Label) { Checked = list.Active == i, ToolTipText = c.Region };
                item.Click += (_, _) => Dispatcher.BeginInvoke(() => SwitchWeatherCity(idx));
                m.DropDownItems.Add(item);
            }
            m.DropDownItems.Add(new Forms.ToolStripSeparator());
            m.DropDownItems.Add("管理城市…", null, (_, _) => Dispatcher.BeginInvoke(OpenWeatherCities));
            m.DropDownItems.Add("立即刷新天气", null, (_, _) => Dispatcher.BeginInvoke(RefreshWeatherNow));
        };
        return m;
    }

    public void OpenWeatherCities()
    {
        foreach (Window w in Windows) if (w is WeatherCitiesWindow cw) { cw.Activate(); return; }
        new WeatherCitiesWindow(this).Show();
    }
}
