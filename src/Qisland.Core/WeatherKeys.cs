using System.Globalization;

namespace Qisland.Core;

/// <summary>天气结果和天气提醒按哪个地点归属。</summary>
public static class WeatherKeys
{
    public const string Auto = "auto";

    /// <summary>
    /// 地点身份：名字 + 坐标（保留 3 位小数，约 100 米，读回来的浮点误差不影响）。
    /// 同名但坐标不同的两个保存城市（列表里允许）键不同，缓存和响应归属都不会混。null = 自动定位。
    /// </summary>
    public static string City(WeatherCity? c) =>
        c == null ? Auto : string.Create(CultureInfo.InvariantCulture, $"{c.Label}@{Math.Round(c.Lat, 3):0.000},{Math.Round(c.Lon, 3):0.000}");
}

/// <summary>
/// 天气提醒：按「城市 + 事件」去重（同一城市同一件事一天一次），卡片 id 里带城市；
/// 切换城市时撤下别的城市还挂着的提醒卡，新城市自己的提醒照常弹。
/// </summary>
public sealed class WeatherAlertTracker
{
    private readonly HashSet<string> _seen = [];
    private readonly Dictionary<string, string> _live = [];   // 卡片 id → 城市
    private string? _current;

    public List<IslandActivity> Announce(string cityKey, IEnumerable<WeatherAlert> alerts)
    {
        _current ??= cityKey;
        var show = new List<IslandActivity>();
        if (_seen.Count > 400) _seen.Clear();
        foreach (var a in alerts)
        {
            if (!_seen.Add(cityKey + "|" + a.Key)) continue;
            var card = WeatherAlertCards.ToActivity(a, cityKey);
            _live[card.Id] = cityKey;
            show.Add(card);
        }
        return show;
    }

    /// <summary>切换到另一个城市：返回该撤下的（别的城市的）提醒卡 id。</summary>
    public List<string> SwitchTo(string cityKey)
    {
        if (_current == cityKey) return [];
        _current = cityKey;
        var gone = _live.Where(kv => kv.Value != cityKey).Select(kv => kv.Key).ToList();
        foreach (var id in gone) _live.Remove(id);
        return gone;
    }
}
