using System.Globalization;
using System.Text.Json;

namespace Qisland.Core;

/// <summary>逐小时预报的一格。Rain = 降水概率（%），Wind = 10 m 风速（km/h）。</summary>
public sealed record HourWx(DateTime At, double Temp, int Code, int Rain, double Wind, bool Day = true);

/// <summary>逐日预报的一天。Sunrise / Sunset 是当地时间 HH:mm（缺失时为空）。</summary>
public sealed record DayWx(DateTime Date, int Code, double High, double Low, int Rain, double Uv, string Sunrise, string Sunset, double WindMax);

/// <summary>Open-Meteo 一次请求拿到的完整预报：实况 + 48 小时 + 7 天。</summary>
public sealed record Forecast(WeatherSnapshot Now, bool IsDay, double Pressure, double Visibility, int WindDirDeg,
    IReadOnlyList<HourWx> Hours, IReadOnlyList<DayWx> Days, string TimeZone)
{
    public DayWx? Today => Days.Count > 0 ? Days[0] : null;
    public DayWx? Tomorrow => Days.Count > 1 ? Days[1] : null;
}

/// <summary>
/// Open-Meteo（免 Key）：实况、逐小时（48 h）、逐日（7 天，含日出日落、UV、降水概率）。
/// 城市搜索用它的地理编码接口；中文输入会同时查「去掉市/县/区后缀」「补上市字」和省会，
/// 名字完全一致的排前面、再按人口排，坐标几乎重合的同名地点只留一个。
/// 思路参考岛仔 Daozai（MIT）的 weather.rs，C# 重写。
/// </summary>
public static class OpenMeteo
{
    public const int ForecastDays = 7;
    public const int MaxCities = 5;

    public static string ForecastUrl(double lat, double lon) =>
        FormattableString.Invariant($"https://api.open-meteo.com/v1/forecast?latitude={lat:0.####}&longitude={lon:0.####}") +
        "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,precipitation,weather_code,wind_speed_10m,wind_direction_10m,surface_pressure" +
        "&hourly=temperature_2m,precipitation_probability,weather_code,wind_speed_10m,is_day,visibility" +
        "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset,uv_index_max,wind_speed_10m_max" +
        $"&timezone=auto&forecast_days={ForecastDays}&forecast_hours=48";

    public static string GeocodeUrl(string name) =>
        "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=10&language=zh&format=json";

    // ───────────── 预报 ─────────────

    public static Forecast? Parse(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.Object) return null;
        if (Num(cur, "temperature_2m") is not { } temp) return null;
        var code = (int)(Num(cur, "weather_code") ?? 2);
        var days = new List<DayWx>();
        if (root.TryGetProperty("daily", out var daily) && Arr(daily, "time") is { } dt)
            for (var i = 0; i < dt.GetArrayLength() && i < ForecastDays; i++)
            {
                if (!DateTime.TryParseExact(dt[i].GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                days.Add(new DayWx(date, (int)(At(daily, "weather_code", i) ?? 2),
                    At(daily, "temperature_2m_max", i) ?? temp, At(daily, "temperature_2m_min", i) ?? temp,
                    (int)(At(daily, "precipitation_probability_max", i) ?? 0), At(daily, "uv_index_max", i) ?? 0,
                    Hm(Str(daily, "sunrise", i)), Hm(Str(daily, "sunset", i)), At(daily, "wind_speed_10m_max", i) ?? 0));
            }
        var hours = new List<HourWx>();
        double vis = 0;
        if (root.TryGetProperty("hourly", out var hourly) && Arr(hourly, "time") is { } ht)
            for (var i = 0; i < ht.GetArrayLength(); i++)
            {
                if (!DateTime.TryParseExact(ht[i].GetString(), "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                var h = new HourWx(at, At(hourly, "temperature_2m", i) ?? temp, (int)(At(hourly, "weather_code", i) ?? code),
                    (int)(At(hourly, "precipitation_probability", i) ?? 0), At(hourly, "wind_speed_10m", i) ?? 0,
                    (At(hourly, "is_day", i) ?? 1) != 0);
                if (vis == 0 && at <= now && at > now.AddHours(-1)) vis = (At(hourly, "visibility", i) ?? 0) / 1000;
                hours.Add(h);
            }
        var today = days.Count > 0 ? days[0] : null;
        var snap = new WeatherSnapshot(temp, code, Num(cur, "apparent_temperature") ?? temp, Num(cur, "relative_humidity_2m") ?? 0,
            Num(cur, "wind_speed_10m") ?? 0, Num(cur, "precipitation") ?? 0, today?.High ?? temp, today?.Low ?? temp);
        var tz = root.TryGetProperty("timezone", out var z) ? z.GetString() ?? "" : "";
        return new Forecast(snap, (Num(cur, "is_day") ?? 1) != 0, Num(cur, "surface_pressure") ?? 0, vis,
            (int)(Num(cur, "wind_direction_10m") ?? 0), hours, days, tz);
    }

    /// <summary>16 方位的中文风向（气象上「北风」= 从北边吹来）。</summary>
    public static string WindDir(int deg)
    {
        string[] n = ["北风", "东北风", "东风", "东南风", "南风", "西南风", "西风", "西北风"];
        var i = (int)Math.Round(((deg % 360) + 360) % 360 / 45.0) % 8;
        return n[i];
    }

    /// <summary>当前时刻之后的若干小时（含当前这一小时之后的整点）。</summary>
    public static IEnumerable<HourWx> Upcoming(IReadOnlyList<HourWx> hours, DateTime now, int count) =>
        hours.Where(h => h.At > now).Take(count);

    // ───────────── 城市搜索 ─────────────

    private static readonly (string Province, string Capital)[] Capitals =
    [
        ("北京", "北京"), ("天津", "天津"), ("上海", "上海"), ("重庆", "重庆"), ("河北", "石家庄"), ("山西", "太原"),
        ("辽宁", "沈阳"), ("吉林", "长春"), ("黑龙江", "哈尔滨"), ("江苏", "南京"), ("浙江", "杭州"), ("安徽", "合肥"),
        ("福建", "福州"), ("江西", "南昌"), ("山东", "济南"), ("河南", "郑州"), ("湖北", "武汉"), ("湖南", "长沙"),
        ("广东", "广州"), ("海南", "海口"), ("四川", "成都"), ("贵州", "贵阳"), ("云南", "昆明"), ("陕西", "西安"),
        ("甘肃", "兰州"), ("青海", "西宁"), ("台湾", "台北"), ("内蒙古", "呼和浩特"), ("广西", "南宁"), ("西藏", "拉萨"),
        ("宁夏", "银川"), ("新疆", "乌鲁木齐"), ("香港", "香港"), ("澳门", "澳门"),
    ];

    private static bool AllCjk(string s) => s.Length > 0 && s.All(c => c is >= '\u4e00' and <= '\u9fff');

    /// <summary>去掉「市 / 县 / 区 / 省」后缀；去掉后不足两个字就保留原名（例如「津市」）。</summary>
    public static string BaseName(string name)
    {
        if (!AllCjk(name) || name.Length < 3) return name;
        return name[^1] is '市' or '县' or '区' or '省' ? name[..^1] : name;
    }

    public static string? ProvinceCapital(string name)
    {
        var s = name;
        foreach (var suf in new[] { "维吾尔自治区", "壮族自治区", "回族自治区", "自治区", "特别行政区", "省", "市" })
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length) { s = s[..^suf.Length]; break; }
        foreach (var (p, c) in Capitals) if (p == s) return c;
        return null;
    }

    /// <summary>一个输入要查哪几个词。</summary>
    public static List<string> CityQueries(string input)
    {
        var name = input.Trim();
        var q = new List<string> { name };
        if (name.Length < 2 || !AllCjk(name)) return q;
        var b = BaseName(name);
        if (ProvinceCapital(name) is { } cap && cap != b && !q.Contains(cap)) q.Add(cap);
        if (b != name) { if (!q.Contains(b)) q.Add(b); }
        else if (!name.EndsWith('州')) q.Insert(0, name + "市");
        return q;
    }

    private static bool SkipFeature(string code) =>
        new[] { "AIR", "RSTN", "BUS", "PRT", "HTL", "SCH", "HSP" }.Any(p => code.StartsWith(p, StringComparison.Ordinal));

    public static List<(WeatherCity City, long Population)> ParseCities(string json)
    {
        var list = new List<(WeatherCity, long)>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var rs) || rs.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in rs.EnumerateArray())
        {
            if (SkipFeature(r.TryGetProperty("feature_code", out var fc) ? fc.GetString() ?? "" : "")) continue;
            var label = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
            if (Num(r, "latitude") is not { } lat || Num(r, "longitude") is not { } lon) continue;
            var areas = new List<string>();
            foreach (var k in new[] { "admin2", "admin1", "country" })
                if (r.TryGetProperty(k, out var a) && a.GetString() is { Length: > 0 } s && s != label && !areas.Contains(s)) areas.Add(s);
            var c = new WeatherCity(label, lat, lon, string.Join(" · ", areas));
            if (c.Valid) list.Add((c, (long)(Num(r, "population") ?? 0)));
        }
        return list;
    }

    /// <summary>名字一致的排前面，再按人口；同名且坐标几乎重合的只留一个。</summary>
    public static List<WeatherCity> RankCities(string input, IEnumerable<(WeatherCity City, long Population)> found)
    {
        var name = input.Trim();
        var b = BaseName(name);
        bool Exact(WeatherCity c) => c.Label == name || c.Label == b || BaseName(c.Label) == b;
        var ranked = new List<WeatherCity>();
        foreach (var (c, _) in found.OrderByDescending(f => Exact(f.City)).ThenByDescending(f => f.Population))
            if (!ranked.Any(r => r.Label == c.Label && Math.Abs(r.Lat - c.Lat) < 0.05 && Math.Abs(r.Lon - c.Lon) < 0.05)) ranked.Add(c);
        return ranked;
    }

    // ───────────── JSON 小工具 ─────────────

    private static double? Num(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    private static JsonElement? Arr(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v : null;
    private static double? At(JsonElement e, string n, int i) =>
        Arr(e, n) is { } a && i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : null;
    private static string Str(JsonElement e, string n, int i) =>
        Arr(e, n) is { } a && i < a.GetArrayLength() && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : "";
    /// <summary>timezone=auto 时日出日落就是当地时间「yyyy-MM-ddTHH:mm」，截出 HH:mm。</summary>
    private static string Hm(string iso) => iso.Length >= 16 ? iso.Substring(11, 5) : "";
}

/// <summary>一个保存的天气城市。Region 只用于在列表里区分同名地点。</summary>
public sealed record WeatherCity(string Label, double Lat, double Lon, string Region = "")
{
    public bool Valid => Label.Trim().Length > 0 && double.IsFinite(Lat) && double.IsFinite(Lon) && Lat is >= -90 and <= 90 && Lon is >= -180 and <= 180;
    public string Query => string.Create(CultureInfo.InvariantCulture, $"{Lat:0.####},{Lon:0.####}");
    public bool Same(WeatherCity o) => o.Label == Label && Math.Abs(o.Lat - Lat) < 0.05 && Math.Abs(o.Lon - Lon) < 0.05;
}

/// <summary>
/// 已保存城市（最多 5 个）+ 当前选中哪个（-1 = 自动定位 / weather.txt）。
/// 存在本机 %APPDATA%\Qisland\weather-cities.json，不进仓库、不上传。
/// </summary>
public sealed class WeatherCities
{
    public List<WeatherCity> Saved { get; set; } = [];
    public int Active { get; set; } = -1;

    public WeatherCity? Current => Active >= 0 && Active < Saved.Count ? Saved[Active] : null;

    /// <summary>加入列表（去重；满了不加）。返回它在列表里的位置，加不进去返回 -1。</summary>
    public int Add(WeatherCity c)
    {
        if (!c.Valid) return -1;
        var i = Saved.FindIndex(s => s.Same(c));
        if (i >= 0) return i;
        if (Saved.Count >= OpenMeteo.MaxCities) return -1;
        Saved.Add(c);
        return Saved.Count - 1;
    }

    public void Remove(int i)
    {
        if (i < 0 || i >= Saved.Count) return;
        Saved.RemoveAt(i);
        if (Active == i) Active = -1;
        else if (Active > i) Active--;
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static WeatherCities FromJson(string? json)
    {
        try
        {
            var w = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<WeatherCities>(json);
            if (w == null) return new();
            w.Saved = w.Saved.Where(c => c != null && c.Valid).Take(OpenMeteo.MaxCities).ToList();
            if (w.Active >= w.Saved.Count) w.Active = -1;
            return w;
        }
        catch { return new(); }
    }
}

/// <summary>一条天气提醒。Key 用来去重（同一件事一天只提醒一次）。</summary>
public sealed record WeatherAlert(string Key, string Title, string Body, bool Severe);

/// <summary>
/// 本地推算的天气提醒（Open-Meteo 没有官方预警，岛仔也没有）：
/// 快下雨了（现在没下、未来 2 小时有降水）、雷阵雨、大风、高温、寒冷、明天大幅降温、要下雪。
/// 只用已经拿到的预报，不额外联网。
/// </summary>
public static class WeatherAlerts
{
    public static bool IsRain(int code) => code is >= 51 and <= 67 or >= 80 and <= 82 or >= 95;
    public static bool IsSnow(int code) => code is >= 71 and <= 77 or 85 or 86;

    public static List<WeatherAlert> Evaluate(WeatherSnapshot now, IReadOnlyList<HourWx> hours, IReadOnlyList<DayWx> days, DateTime at)
    {
        var list = new List<WeatherAlert>();
        var day = at.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var next2 = hours.Where(h => h.At > at.AddMinutes(-30) && h.At <= at.AddHours(2)).ToList();
        var next6 = hours.Where(h => h.At > at && h.At <= at.AddHours(6)).ToList();
        var next12 = hours.Where(h => h.At > at && h.At <= at.AddHours(12)).ToList();

        if (!IsRain(now.Code) && now.Precipitation < 0.1)
        {
            var wet = next2.FirstOrDefault(h => h.At > at && (IsRain(h.Code) || h.Rain >= 70));
            if (wet != null)
            {
                var mins = Math.Max(10, (int)Math.Round((wet.At - at).TotalMinutes / 10) * 10);
                var what = wet.Code >= 95 ? "雷阵雨" : wet.Code is >= 80 and <= 82 ? "阵雨" : "雨";
                list.Add(new($"rain:{day}:{wet.At.Hour:00}", $"约 {mins} 分钟后有{what}", $"{wet.At.Hour} 点前后降水概率 {wet.Rain}%，出门带伞", false));
            }
        }
        if (next6.FirstOrDefault(h => h.Code >= 95) is { } storm && now.Code < 95)
            list.Add(new($"thunder:{day}", "6 小时内有雷阵雨", $"{storm.At.Hour} 点前后可能打雷，注意避开空旷处", true));
        if (!IsSnow(now.Code) && next12.FirstOrDefault(h => IsSnow(h.Code)) is { } snow)
            list.Add(new($"snow:{day}", "今天要下雪", $"{snow.At.Hour} 点前后有雪，路面可能湿滑", false));
        var wind = Math.Max(now.WindSpeed, next12.Select(h => h.Wind).DefaultIfEmpty(0).Max());
        if (wind >= 50)
            list.Add(new($"wind:{day}", "大风", $"风速最高约 {wind:0} km/h（{Beaufort(wind)} 级），注意高空坠物", wind >= 62));
        if (days.Count > 0)
        {
            var t = days[0];
            if (t.High >= 35) list.Add(new($"heat:{day}", "高温", $"今天最高 {t.High:0}°，注意防暑补水", t.High >= 38));
            if (t.Low <= -10) list.Add(new($"cold:{day}", "严寒", $"今天最低 {t.Low:0}°，注意保暖", t.Low <= -20));
            if (days.Count > 1 && t.Low - days[1].Low >= 8)
                list.Add(new($"drop:{day}", $"明天降温 {t.Low - days[1].Low:0}°", $"明天最低 {days[1].Low:0}°，记得加衣", false));
        }
        return list;
    }

    /// <summary>km/h → 蒲福风级。</summary>
    public static int Beaufort(double kmh)
    {
        double[] edges = [1, 6, 12, 20, 29, 39, 50, 62, 75, 89, 103, 118];
        var b = 0;
        while (b < edges.Length && kmh >= edges[b]) b++;
        return b;
    }
}
