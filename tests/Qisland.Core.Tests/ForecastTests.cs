using Qisland.Core;

namespace Qisland.Core.Tests;

public class ForecastTests
{
    // 合成的 Open-Meteo 响应（坐标、时间都是虚构的中性数据）
    private const string Json = """
    {"timezone":"Asia/Shanghai",
     "current":{"temperature_2m":21.4,"relative_humidity_2m":60,"apparent_temperature":20.1,"is_day":1,"precipitation":0,"weather_code":2,"wind_speed_10m":12.5,"wind_direction_10m":90,"surface_pressure":1012},
     "hourly":{"time":["2026-05-01T09:00","2026-05-01T10:00","2026-05-01T11:00","2026-05-01T12:00"],
               "temperature_2m":[20,21,22,23],"precipitation_probability":[5,10,80,90],"weather_code":[2,3,61,63],
               "wind_speed_10m":[10,12,14,16],"is_day":[1,1,1,1],"visibility":[24000,24000,12000,8000]},
     "daily":{"time":["2026-05-01","2026-05-02","2026-05-03","2026-05-04","2026-05-05","2026-05-06","2026-05-07"],
              "weather_code":[61,3,0,1,2,80,95],"temperature_2m_max":[24,22,26,27,25,23,21],"temperature_2m_min":[16,8,12,14,15,13,12],
              "precipitation_probability_max":[90,20,0,5,10,60,80],"sunrise":["2026-05-01T05:21"],"sunset":["2026-05-01T18:47"],
              "uv_index_max":[6.5,5,7,7,6,4,3],"wind_speed_10m_max":[20,18,15,12,14,22,30]}}
    """;

    static readonly DateTime Now = new(2026, 5, 1, 9, 20, 0);

    [Fact]
    public void Parses_Current_Hourly_SevenDays()
    {
        var f = OpenMeteo.Parse(Json, Now)!;
        Assert.Equal(21.4, f.Now.Temperature);
        Assert.Equal(2, f.Now.Code);
        Assert.Equal(24, f.Now.High);
        Assert.Equal(16, f.Now.Low);
        Assert.Equal(7, f.Days.Count);
        Assert.Equal("05:21", f.Days[0].Sunrise);
        Assert.Equal("18:47", f.Days[0].Sunset);
        Assert.Equal("", f.Days[1].Sunrise);         // 缺失时为空，不抛
        Assert.Equal(4, f.Hours.Count);
        Assert.Equal(80, f.Hours[2].Rain);
        Assert.Equal(24, f.Visibility);
        Assert.Equal("东风", OpenMeteo.WindDir(f.WindDirDeg));
        Assert.Equal(3, OpenMeteo.Upcoming(f.Hours, Now, 8).Count());
    }

    [Fact]
    public void MissingCurrent_ReturnsNull()
    {
        Assert.Null(OpenMeteo.Parse("""{"daily":{}}""", Now));
    }

    [Fact]
    public void Url_IsInvariantAndAsksForSevenDays()
    {
        var u = OpenMeteo.ForecastUrl(39.9042, 116.4074);
        Assert.Contains("latitude=39.9042&longitude=116.4074", u);
        Assert.Contains("forecast_days=7", u);
        Assert.Contains("hourly=", u);
        Assert.Contains("name=%E5%8C%97%E4%BA%AC", OpenMeteo.GeocodeUrl("北京"));
    }

    [Fact]
    public void WindDir_AllQuadrants()
    {
        Assert.Equal("北风", OpenMeteo.WindDir(0));
        Assert.Equal("北风", OpenMeteo.WindDir(350));
        Assert.Equal("西南风", OpenMeteo.WindDir(225));
        Assert.Equal("西风", OpenMeteo.WindDir(-90));
    }
}

public class CitySearchTests
{
    [Theory]
    [InlineData("广州市", "广州")]
    [InlineData("津市", "津市")]       // 去后缀不足两字：保留
    [InlineData("Paris", "Paris")]
    public void BaseName_StripsAdminSuffix(string input, string expect) => Assert.Equal(expect, OpenMeteo.BaseName(input));

    [Fact]
    public void Queries_ProvinceAddsCapital_ShortNameAddsShi()
    {
        Assert.Contains("乌鲁木齐", OpenMeteo.CityQueries("新疆维吾尔自治区"));
        Assert.Contains("乌鲁木齐", OpenMeteo.CityQueries("新疆"));
        Assert.Contains("广州", OpenMeteo.CityQueries("广州市"));
        Assert.Equal("苏州", OpenMeteo.CityQueries("苏州")[0]);       // 以「州」结尾不补「市」
        Assert.Equal("无锡市", OpenMeteo.CityQueries("无锡")[0]);
        Assert.Equal(["London"], OpenMeteo.CityQueries(" London "));
    }

    [Fact]
    public void Cities_SkipAirports_RankExactThenPopulation_Dedupe()
    {
        const string json = """
        {"results":[
          {"name":"北京首都国际机场","latitude":40.08,"longitude":116.58,"feature_code":"AIRP","country":"中国"},
          {"name":"北京路","latitude":23.1,"longitude":113.2,"feature_code":"PPLX","admin1":"广东","country":"中国","population":0},
          {"name":"北京","latitude":39.9075,"longitude":116.39723,"feature_code":"PPLC","admin1":"北京","country":"中国","population":18960744},
          {"name":"北京","latitude":39.91,"longitude":116.40,"feature_code":"PPLA","country":"中国","population":100}
        ]}
        """;
        var found = OpenMeteo.ParseCities(json);
        Assert.Equal(3, found.Count);                       // 机场被跳过
        var ranked = OpenMeteo.RankCities("北京市", found);
        Assert.Equal(2, ranked.Count);                      // 两个几乎重合的「北京」只留一个
        Assert.Equal("北京", ranked[0].Label);
        Assert.Equal("中国", ranked[0].Region);              // admin1 与名字相同时不重复显示
        Assert.Equal("北京路", ranked[1].Label);
    }

    [Fact]
    public void SavedCities_MaxFive_RemoveFixesActive_RoundTrip()
    {
        var w = new WeatherCities();
        for (var i = 0; i < 7; i++) w.Add(new WeatherCity("C" + i, i, i));
        Assert.Equal(5, w.Saved.Count);
        Assert.Equal(-1, w.Add(new WeatherCity("X", 50, 50)));
        Assert.Equal(1, w.Add(new WeatherCity("C1", 1.01, 1.01)));   // 同一个地点不重复加
        w.Active = 3;
        w.Remove(1);
        Assert.Equal(2, w.Active);
        w.Remove(2);
        Assert.Equal(-1, w.Active);
        var back = WeatherCities.FromJson(w.ToJson());
        Assert.Equal(w.Saved.Count, back.Saved.Count);
        Assert.Empty(WeatherCities.FromJson("not json").Saved);
        Assert.Equal(-1, WeatherCities.FromJson("""{"Saved":[],"Active":4}""").Active);
        Assert.False(new WeatherCity("", 0, 0).Valid);
        Assert.False(new WeatherCity("A", 91, 0).Valid);
    }
}

public class WeatherAlertTests
{
    static readonly DateTime Now = new(2026, 7, 10, 14, 5, 0);
    static WeatherSnapshot Snap(int code = 1, double wind = 8, double precip = 0) => new(30, code, 32, 50, wind, precip, 33, 24);
    static HourWx H(int addH, int code = 1, int rain = 0, double wind = 10) => new(Now.Date.AddHours(Now.Hour + addH), 30, code, rain, wind);
    static DayWx D(double hi, double lo) => new(Now.Date, 1, hi, lo, 0, 5, "", "", 10);

    [Fact]
    public void RainSoon_WhenDryNow_AndRainWithinTwoHours()
    {
        var a = WeatherAlerts.Evaluate(Snap(), [H(1, 61, 80), H(2, 61, 90)], [D(33, 24)], Now);
        var r = Assert.Single(a);
        Assert.StartsWith("rain:", r.Key);
        Assert.Contains("分钟后有雨", r.Title);
    }

    [Fact]
    public void NoRainAlert_WhenAlreadyRaining_OrFarAway()
    {
        Assert.Empty(WeatherAlerts.Evaluate(Snap(61), [H(1, 61, 80)], [], Now));
        Assert.Empty(WeatherAlerts.Evaluate(Snap(), [H(4, 61, 80)], [], Now));
    }

    [Fact]
    public void Thunder_Wind_Heat_Drop()
    {
        var a = WeatherAlerts.Evaluate(Snap(wind: 20), [H(3, 95, 60, 55)], [D(36, 24), D(28, 15)], Now);
        Assert.Contains(a, x => x.Key.StartsWith("thunder:") && x.Severe);
        Assert.Contains(a, x => x.Key.StartsWith("wind:") && x.Body.Contains("7 级"));
        Assert.Contains(a, x => x.Key.StartsWith("heat:"));
        Assert.Contains(a, x => x.Title == "明天降温 9°");
    }

    [Fact]
    public void KeysAreStableWithinTheDay()
    {
        var a = WeatherAlerts.Evaluate(Snap(), [H(3, 95)], [], Now);
        var b = WeatherAlerts.Evaluate(Snap(), [H(2, 95)], [], Now.AddMinutes(50));
        Assert.Equal(a.Single().Key, b.Single(x => x.Key.StartsWith("thunder")).Key);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 2)]
    [InlineData(55, 7)]
    [InlineData(120, 12)]
    public void Beaufort(double kmh, int expect) => Assert.Equal(expect, WeatherAlerts.Beaufort(kmh));
}

public class HealthTests
{
    static readonly HealthSettings S = new();

    [Fact]
    public void Window_RespectsHoursAndLunch()
    {
        Assert.False(S.InWindow(new DateTime(2026, 1, 5, 8, 59, 0)));
        Assert.True(S.InWindow(new DateTime(2026, 1, 5, 9, 0, 0)));
        Assert.False(S.InWindow(new DateTime(2026, 1, 5, 12, 30, 0)));
        Assert.True(S.InWindow(new DateTime(2026, 1, 5, 13, 30, 0)));
        Assert.False(S.InWindow(new DateTime(2026, 1, 5, 22, 0, 0)));
        Assert.True((S with { LunchQuiet = false }).InWindow(new DateTime(2026, 1, 5, 12, 30, 0)));
    }

    [Fact]
    public void WaterDue_AfterInterval_NotAfterGoal()
    {
        var day = new DateTime(2026, 1, 5);
        var log = new WaterLog();
        log.Roll(day.AddHours(9));
        Assert.False(HealthClock.WaterDue(S, log, DateTime.MinValue, day.AddHours(9).AddMinutes(30)));
        Assert.True(HealthClock.WaterDue(S, log, DateTime.MinValue, day.AddHours(9).AddMinutes(45)));
        log.Drink(250, day.AddHours(10));
        Assert.False(HealthClock.WaterDue(S, log, DateTime.MinValue, day.AddHours(10).AddMinutes(40)));
        Assert.True(HealthClock.WaterDue(S, log, DateTime.MinValue, day.AddHours(10).AddMinutes(46)));
        Assert.False(HealthClock.WaterDue(S, log, day.AddHours(10).AddMinutes(46), day.AddHours(11)));   // 刚提醒过
        log.Drink(2000, day.AddHours(11));
        Assert.False(HealthClock.WaterDue(S, log, DateTime.MinValue, day.AddHours(15)));
    }

    [Fact]
    public void WaterLog_RollsOverAndRoundTrips()
    {
        var t = new DateTime(2026, 1, 5, 10, 15, 0);
        var log = new WaterLog();
        log.Drink(250, t); log.Drink(250, t.AddHours(2)); log.Drink(-100, t.AddHours(2));
        Assert.Equal(400, log.TotalMl);
        Assert.Equal(150, log.Hours[12]);
        var back = WaterLog.Parse(log.Serialize(), t.AddHours(3));
        Assert.Equal(400, back.TotalMl);
        Assert.Equal(250, back.Hours[10]);
        Assert.Equal(new DateTime(2026, 1, 5, 12, 15, 0), back.LastDrink);
        Assert.Equal(0, WaterLog.Parse(log.Serialize(), t.AddDays(1)).TotalMl);   // 第二天清零
        Assert.Equal(0.2, back.Progress(2000), 3);
    }

    [Fact]
    public void Sit_CountsContinuousUse_BreakResets()
    {
        var t0 = new DateTime(2026, 1, 5, 14, 0, 0);
        var since = HealthClock.TrackActivity(S, DateTime.MinValue, TimeSpan.Zero, t0);
        Assert.Equal(t0, since);
        since = HealthClock.TrackActivity(S, since, TimeSpan.FromMinutes(2), t0.AddMinutes(30));
        Assert.Equal(t0, since);                                      // 2 分钟没碰不算休息
        Assert.True(HealthClock.SitDue(S, since, DateTime.MinValue, TimeSpan.Zero, t0.AddMinutes(60)));
        Assert.False(HealthClock.SitDue(S, since, DateTime.MinValue, TimeSpan.FromMinutes(2), t0.AddMinutes(60)));   // 正空闲着：不打扰
        Assert.False(HealthClock.SitDue(S, since, t0.AddMinutes(60), TimeSpan.Zero, t0.AddMinutes(90)));           // 刚提醒过
        since = HealthClock.TrackActivity(S, since, TimeSpan.FromMinutes(6), t0.AddMinutes(70));
        Assert.Equal(t0.AddMinutes(70), since);                       // 起身 6 分钟：重新计时
    }
}
