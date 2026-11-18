using System.Globalization;

namespace Qisland.Core;

/// <summary>
/// 天气地点，来自 %APPDATA%\Qisland\weather.txt 的第一行有效内容（# 开头是注释）：
/// · 空 / 没有文件：交给 wttr.in 按出口 IP 自动定位，显示名用它返回的地名；
/// · 一个词：既是显示名也是查询词，例如 <c>Shanghai</c>；
/// · 两段：<c>显示名 查询</c>，查询可以是城市英文名或 <c>纬度,经度</c>，例如 <c>上海 31.23,121.47</c>。
/// </summary>
public sealed record WeatherPlace(string Display, string Query)
{
    public static readonly WeatherPlace Auto = new("", "");

    public static WeatherPlace Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Auto;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            var parts = l.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 1 ? new(parts[0], parts[0]) : new(parts[0], parts[1]);
        }
        return Auto;
    }

    public string WttrUrl => "https://wttr.in/" + Uri.EscapeDataString(Query).Replace("%2C", ",") + "?format=j1&lang=zh";

    /// <summary>查询词本身是「纬度,经度」时给出坐标（Open-Meteo 兜底要用）。</summary>
    public (double Lat, double Lon)? Coordinates
    {
        get
        {
            var p = Query.Split(',');
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) && lat is >= -90 and <= 90
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) && lon is >= -180 and <= 180
                ? (lat, lon) : null;
        }
    }

    public static string OpenMeteoUrl(double lat, double lon) =>
        string.Create(CultureInfo.InvariantCulture,
            $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.##}&longitude={lon:0.##}&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1");
}
