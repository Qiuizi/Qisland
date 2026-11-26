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
    /// <summary>
    /// 每个城市最近一份天气：切城市时先显示缓存，再联网刷新。键 = 城市标签（自动定位用空串）。
    /// 结果按「请求发出时的城市」入缓存，只有那个城市仍是当前选中时才更新界面 / 发提醒。
    /// </summary>
    private readonly CityResults<WeatherShown> _wx = new();
    private sealed record WeatherShown(WeatherSnapshot Now, WeatherDetail Detail, string Label);
    // 地点身份（名字 + 坐标），同名不同地的城市不共用缓存 / 响应归属
    private static string CityKey(WeatherCity? c) => WeatherKeys.City(c);
    // 天气提醒按城市去重；切城时撤下上一城还挂着的提醒卡
    private readonly WeatherAlertTracker _wxAlerts = new();

    private static string SafeRead(string f) { try { return File.ReadAllText(f); } catch { return ""; } }

    private static void SaveCities()
    {
        try { File.WriteAllText(CitiesFile, WeatherCityList.ToJson()); }
        catch (Exception ex) { Log("保存天气城市: " + ex.Message); }
    }

    /// <param name="cityKey">请求发出时选中的城市（不是现在的）。</param>
    private void ShowWeather(string cityKey, string label, WeatherSnapshot now, WeatherDetail d)
    {
        _wx.Select(CityKey(WeatherCityList.Current));
        if (!_wx.Commit(cityKey, new WeatherShown(now, d, label))) return;   // 期间换了城市：只进缓存，不改界面、不发提醒
        WeatherLabel = label;
        _window?.SetWeather(now, d);
        AnnounceWeatherAlerts(cityKey, d);
    }

    /// <summary>切换城市（-1 = 自动定位）：先换上缓存，再马上联网刷新。</summary>
    public void SwitchWeatherCity(int index)
    {
        var list = WeatherCityList;
        list.Active = index >= 0 && index < list.Saved.Count ? index : -1;
        SaveCities();
        _wx.Select(CityKey(list.Current));
        foreach (var id in _wxAlerts.SwitchTo(CityKey(list.Current))) _store.Remove(id);
        if (_wx.Shown is { } c) { WeatherLabel = c.Label; _window?.SetWeather(c.Now, c.Detail); }
        else
        {
            // 新城市还没有数据：清掉上一个城市的天气，不拿它冒充（刷新失败也不会一直显示错的）
            WeatherLabel = list.Current?.Label ?? "";
            _window?.ClearWeather();
        }
        Log($"天气城市 → {(list.Current == null ? "自动定位" : "已保存城市 #" + (list.Active + 1))}");  // 不把地名写进日志
        RefreshWeatherNow();
    }

    public void RefreshWeatherNow() => _weatherWake.TrySetResult();

    /// <summary>城市搜索：几个写法并行查 Open-Meteo 地理编码，再按 <see cref="CitySearch.Rank"/> 去重排序（省名直接给内置的主要城市）。</summary>
    public static async Task<List<WeatherCity>> SearchCities(string name, CancellationToken ct)
    {
        CitySearch.Simplify = Simplified;
        var queries = CitySearch.Queries(name);
        // GeoNames 有些中文名只有繁体（「東京」「臺中」），补一个繁体写法
        if (CitySearch.AllCjk(name.Trim()) && Traditional(name.Trim()) is { } tw && !queries.Contains(tw)) queries.Add(tw);
        Exception? last = null;
        var pages = await Task.WhenAll(queries.Select(async q =>
        {
            try { return CitySearch.Parse(await Http.GetStringAsync(CitySearch.GeocodeUrl(q), ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { last = ex; return new List<CityHit>(); }
        }));
        var found = pages.SelectMany(p => p).ToList();
        var ranked = CitySearch.Rank(name, found, queries);
        if (ranked.Count == 0 && last != null) throw last;
        return ranked;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int LCMapStringEx(string locale, uint flags, string src, int srcLen, [System.Runtime.InteropServices.Out] char[]? dest, int destLen, IntPtr ver, IntPtr res, IntPtr param);

    /// <summary>简体转繁体（系统自带的 LCMapStringEx），失败或没变化返回 null。</summary>
    private static string? Traditional(string s) { var t = MapChinese(s, 0x04000000); return t != s ? t : null; }

    /// <summary>繁体转简体，搜索结果里的「東京」「臺灣省」显示成简体；失败原样返回。</summary>
    internal static string Simplified(string s) => s.Length == 0 ? s : MapChinese(s, 0x02000000) ?? s;

    private static string? MapChinese(string s, uint flag)
    {
        try
        {
            var buf = new char[s.Length * 2 + 2];
            var n = LCMapStringEx("zh-CN", flag, s, s.Length, buf, buf.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return n > 0 ? new string(buf, 0, n) : null;
        }
        catch { return null; }
    }

    /// <summary>加入城市列表，返回位置（已在列表里就返回原位置），满了返回 -1。</summary>
    public static int AddWeatherCity(WeatherCity c)
    {
        var i = WeatherCityList.Add(c);
        if (i >= 0) SaveCities();
        return i;
    }

    public static void RemoveWeatherCity(int i) { WeatherCityList.Remove(i); SaveCities(); }

    /// <summary>天气提醒：同一城市同一件事一天只提醒一次；只在「岛上提示 → 天气提醒」打开时弹。</summary>
    private void AnnounceWeatherAlerts(string cityKey, WeatherDetail d)
    {
        if (!Features.On(Features.WeatherAlerts)) return;
        foreach (var id in _wxAlerts.SwitchTo(cityKey)) _store.Remove(id);
        // 单独的 source（weather-alert）：按普通通知排序上列表和胶囊；普通天气数据仍不进主队列
        foreach (var card in _wxAlerts.Announce(cityKey, d.Alerts))
        {
            _store.Upsert(card);
            Log($"天气提醒: {card.Title}");
        }
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
