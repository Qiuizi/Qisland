namespace Qisland.Core;

/// <summary>
/// 列表 / 胶囊的排序和过滤。天气数据（source = weather）是环境信息，不进主队列；
/// 天气提醒用单独的 source（weather-alert），按普通通知排序显示。
/// </summary>
public static class ActivityRanking
{
    public const string WeatherSource = "weather";
    public const string WeatherAlertSource = "weather-alert";

    public static bool InMainQueue(LiveActivity x) => x.Source != WeatherSource;
    public static bool IsWeatherAlert(LiveActivity x) => x.Source == WeatherAlertSource;

    /// <summary>等待确认 &gt; 出错 &gt; 优先级 &gt; 最近更新。</summary>
    public static List<LiveActivity> Rank(IEnumerable<LiveActivity> items) => items
        .Where(InMainQueue)
        .OrderByDescending(x => x.Kind == ActivityKind.WaitingApproval)
        .ThenByDescending(x => x.Kind == ActivityKind.Error)
        .ThenByDescending(x => x.Priority)
        .ThenByDescending(x => x.Updated)
        .ToList();
}

/// <summary>天气提醒 → 岛上的通知卡。</summary>
public static class WeatherAlertCards
{
    public static IslandActivity ToActivity(WeatherAlert a) =>
        new("wx:alert:" + a.Key.Split(':')[0], ActivityRanking.WeatherAlertSource, a.Title, a.Body,
            ActivityKind.Warning, a.Severe ? 58 : 44, DurationSeconds: a.Severe ? 14 : 9, ResetTtl: true);
}
