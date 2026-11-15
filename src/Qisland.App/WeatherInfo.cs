using System.Globalization;
using System.Text.Json;
using Qisland.Core;

namespace Qisland.App;

public sealed record HourSlot(DateTime At, double Temp, int Code, int Rain);
public sealed record DaySlot(DateTime Date, int Code, double High, double Low, int Rain);
public sealed record WeatherDetail(
    WeatherSnapshot Now, int RainChance, double Uv, string WindDir, double Visibility,
    string Sunrise, string Sunset, IReadOnlyList<HourSlot> Hours,
    double TomorrowHigh, double TomorrowLow, int TomorrowCode, int TomorrowRain)
{
    public IReadOnlyList<DaySlot> Days { get; init; } = [];
    public double Pressure { get; init; }
    public string MoonPhase { get; init; } = "";
    public int MoonIllum { get; init; }
    public DateTime Fetched { get; init; } = DateTime.Now;
}

public static class WttrParser
{
    private static double D(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    /// <summary>wttr.in 的 WWO 天气码 → WMO 码（Theme / WeatherParser 都按 WMO 走）。</summary>
    public static int Wmo(int code) => code switch
    {
        113 => 0,
        116 => 2,
        119 or 122 => 3,
        143 or 248 or 260 => 45,
        176 or 263 or 266 or 293 or 296 or 353 => 61,
        299 or 302 or 356 => 63,
        305 or 308 or 359 => 65,
        200 or (>= 386 and <= 395) => 95,
        179 or 182 or 185 or 227 or 230 or (>= 317 and <= 338) or (>= 368 and <= 377) => 71,
        _ => 61,
    };

    private static string Clock(string s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.ToString("HH:mm") : s;

    private static string Dir(string d) => d switch
    {
        "N" => "北风", "NNE" or "NE" or "ENE" => "东北风", "E" => "东风", "ESE" or "SE" or "SSE" => "东南风",
        "S" => "南风", "SSW" or "SW" or "WSW" => "西南风", "W" => "西风", "WNW" or "NW" or "NNW" => "西北风", _ => "风",
    };

    private static string Moon(string p) => p switch
    {
        "New Moon" => "新月", "Waxing Crescent" => "蛾眉月", "First Quarter" => "上弦月", "Waxing Gibbous" => "盈凸月",
        "Full Moon" => "满月", "Waning Gibbous" => "亏凸月", "Last Quarter" => "下弦月", "Waning Crescent" => "残月", _ => p,
    };

    public static WeatherDetail? Parse(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cur = root.GetProperty("current_condition")[0];
        var days = root.GetProperty("weather");
        var today = days[0];
        var code = Wmo((int)D(cur, "weatherCode"));
        var snap = new WeatherSnapshot(D(cur, "temp_C"), code, D(cur, "FeelsLikeC"), D(cur, "humidity"),
            D(cur, "windspeedKmph"), D(cur, "precipMM"), D(today, "maxtempC"), D(today, "mintempC"));

        var hours = new List<HourSlot>();
        var rainNow = 0;
        for (var i = 0; i < days.GetArrayLength(); i++)
        {
            var day = days[i];
            var date = DateTime.TryParse(day.GetProperty("date").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : now.Date.AddDays(i);
            foreach (var h in day.GetProperty("hourly").EnumerateArray())
            {
                var at = date.AddHours(D(h, "time") / 100);
                var slot = new HourSlot(at, D(h, "tempC"), Wmo((int)D(h, "weatherCode")), (int)D(h, "chanceofrain"));
                if (at <= now && at > now.AddHours(-3)) rainNow = slot.Rain;
                if (at > now) hours.Add(slot);
            }
        }
        var astro = today.GetProperty("astronomy")[0];
        double th = 0, tl = 0; int tc = code, tr = 0;
        if (days.GetArrayLength() > 1)
        {
            var tm = days[1];
            th = D(tm, "maxtempC"); tl = D(tm, "mintempC");
            var hs = tm.GetProperty("hourly");
            var noon = hs.GetArrayLength() > 4 ? hs[4] : hs[0];
            tc = Wmo((int)D(noon, "weatherCode"));
            tr = hs.EnumerateArray().Select(h => (int)D(h, "chanceofrain")).DefaultIfEmpty(0).Max();
        }
        var dir = cur.TryGetProperty("winddir16Point", out var wd) ? Dir(wd.GetString() ?? "") : "风";
        var dayList = new List<DaySlot>();
        for (var i = 0; i < days.GetArrayLength(); i++)
        {
            var day = days[i];
            var date = DateTime.TryParse(day.GetProperty("date").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : now.Date.AddDays(i);
            var hs = day.GetProperty("hourly");
            var noon = hs.GetArrayLength() > 4 ? hs[4] : hs[0];
            dayList.Add(new DaySlot(date, Wmo((int)D(noon, "weatherCode")), D(day, "maxtempC"), D(day, "mintempC"),
                hs.EnumerateArray().Select(h => (int)D(h, "chanceofrain")).DefaultIfEmpty(0).Max()));
        }
        return new WeatherDetail(snap, rainNow, D(cur, "uvIndex"), dir, D(cur, "visibility"),
            Clock(astro.GetProperty("sunrise").GetString() ?? ""), Clock(astro.GetProperty("sunset").GetString() ?? ""),
            hours.Take(8).ToList(), th, tl, tc, tr)
        {
            Days = dayList, Pressure = D(cur, "pressure"),
            MoonPhase = Moon(astro.TryGetProperty("moon_phase", out var mp) ? mp.GetString() ?? "" : ""),
            MoonIllum = (int)D(astro, "moon_illumination"),
        };
    }
}
