using System.Text.Json;

namespace Qisland.Core;

/// <summary>地理编码的一条结果，排序要用的字段都带着。</summary>
public sealed record CityHit(WeatherCity City, long Population, string Feature, string Country, string Key);

/// <summary>
/// 天气城市搜索（Open-Meteo 地理编码 = GeoNames 数据）。它只按名字精确匹配、不排序，中文名又是逐条人工维护的，所以：
/// · 同一个城市的中文名有时带「市」有时不带（「海口市」「三亚」），只查一种就漏，所以原词、补「市」、去后缀都查；
/// · 中文名只有一个写法（「東京」不认「东京」），App 层再补一个繁体词，几个常见海外城市直接用英文名查；
/// · 输入省名（「海南」「海南省」）时 GeoNames 只有同名村子，所以内置每个省的省会和几个主要城市放在最前面；
/// · 排序：中文输入时 国内大城市 → 海外大城市 → 国内小地方 → 海外小地方；
///   「大城市」= 首都 / 省会 / 地级（PPLC/PPLA/PPLA2/PPLA3）或人口过 10 万（大连这种在 GeoNames 里只标 PPL）；
///   同档里名字完全一样的在前，再按行政级别、人口；坐标几乎重合的只留一个，同名同地区的村子也只留一个。
/// </summary>
public static class CitySearch
{
    public const int Shown = 8;

    // 省 / 直辖市 / 特区 → 省会和主要城市（坐标取自 Open-Meteo 地理编码对应条目）
    private static readonly Dictionary<string, (string Name, double Lat, double Lon)[]> Provinces = new()
    {
        ["北京"] = [("北京", 39.9075, 116.3972)],
        ["天津"] = [("天津", 39.1422, 117.1767)],
        ["上海"] = [("上海", 31.2222, 121.4581)],
        ["重庆"] = [("重庆", 29.5603, 106.5577)],
        ["河北"] = [("石家庄", 38.0414, 114.4786), ("唐山", 39.6438, 118.1832), ("保定", 38.8729, 115.4625), ("秦皇岛", 39.941, 119.5894)],
        ["山西"] = [("太原", 37.8694, 112.5603), ("大同", 40.0936, 113.2914)],
        ["辽宁"] = [("沈阳", 41.7922, 123.4328), ("大连", 38.9122, 121.6022)],
        ["吉林"] = [("长春", 43.88, 125.3228), ("吉林", 43.8465, 126.5608)],
        ["黑龙江"] = [("哈尔滨", 45.75, 126.65), ("齐齐哈尔", 47.3392, 123.9615), ("大庆", 46.5833, 125.0)],
        ["江苏"] = [("南京", 32.0617, 118.7778), ("苏州", 31.3041, 120.5954), ("无锡", 31.5689, 120.2886)],
        ["浙江"] = [("杭州", 30.2936, 120.1614), ("宁波", 29.8782, 121.5494), ("温州", 27.9994, 120.6668)],
        ["安徽"] = [("合肥", 31.8639, 117.2808), ("芜湖", 31.3526, 118.4295)],
        ["福建"] = [("福州", 26.0614, 119.3061), ("厦门", 24.4798, 118.0819), ("泉州", 24.9139, 118.5858)],
        ["江西"] = [("南昌", 28.684, 115.8531), ("赣州", 25.8466, 114.9326)],
        ["山东"] = [("济南", 36.6683, 116.9972), ("青岛", 36.0649, 120.3804), ("烟台", 37.4765, 121.4408)],
        ["河南"] = [("郑州", 34.7578, 113.6486), ("洛阳", 34.6735, 112.4368)],
        ["湖北"] = [("武汉", 30.5833, 114.2667), ("宜昌", 30.7144, 111.2847)],
        ["湖南"] = [("长沙", 28.1987, 112.9709), ("张家界", 29.1294, 110.4783)],
        ["广东"] = [("广州", 23.1167, 113.25), ("深圳", 22.5455, 114.0683), ("珠海", 22.2769, 113.5678)],
        ["海南"] = [("海口", 20.0342, 110.3465), ("三亚", 18.2543, 109.5095), ("儋州", 19.185, 109.4431)],
        ["四川"] = [("成都", 30.6667, 104.0667), ("绵阳", 31.4678, 104.6817)],
        ["贵州"] = [("贵阳", 26.5833, 106.7167), ("遵义", 27.6867, 106.9072)],
        ["云南"] = [("昆明", 25.0389, 102.7183), ("大理", 25.5847, 100.2123), ("丽江", 26.8688, 100.2207)],
        ["陕西"] = [("西安", 34.2583, 108.9286), ("宝鸡", 34.3678, 107.237)],
        ["甘肃"] = [("兰州", 36.057, 103.8399), ("敦煌", 40.1667, 94.6833)],
        ["青海"] = [("西宁", 36.6255, 101.7574)],
        ["台湾"] = [("台北", 25.0531, 121.5264), ("高雄", 22.6163, 120.3133), ("台中", 24.1469, 120.6839)],
        ["内蒙古"] = [("呼和浩特", 40.8106, 111.6522), ("包头", 40.6516, 109.8439)],
        ["广西"] = [("南宁", 22.8167, 108.3167), ("桂林", 25.2802, 110.2964)],
        ["西藏"] = [("拉萨", 29.65, 91.1)],
        ["宁夏"] = [("银川", 38.4681, 106.2731)],
        ["新疆"] = [("乌鲁木齐", 43.801, 87.6005), ("喀什", 39.4672, 75.9868)],
        ["香港"] = [("香港", 22.2783, 114.1747)],
        ["澳门"] = [("澳门", 22.2006, 113.5461)],
    };

    // 中文名在 GeoNames 里没有简体写法的常见海外城市：用英文名查（返回的名字仍是中文）
    private static readonly Dictionary<string, string> Abroad = new()
    {
        ["东京"] = "Tokyo", ["大阪"] = "Osaka", ["京都"] = "Kyoto", ["札幌"] = "Sapporo", ["名古屋"] = "Nagoya", ["福冈"] = "Fukuoka", ["冲绳"] = "Naha",
        ["首尔"] = "Seoul", ["釜山"] = "Busan", ["济州"] = "Jeju", ["曼谷"] = "Bangkok", ["清迈"] = "Chiang Mai", ["普吉"] = "Phuket",
        ["吉隆坡"] = "Kuala Lumpur", ["河内"] = "Hanoi", ["胡志明市"] = "Ho Chi Minh City", ["岘港"] = "Da Nang", ["巴厘岛"] = "Denpasar", ["雅加达"] = "Jakarta", ["马尼拉"] = "Manila",
        ["迪拜"] = "Dubai", ["伦敦"] = "London", ["巴黎"] = "Paris", ["柏林"] = "Berlin", ["罗马"] = "Rome", ["米兰"] = "Milan", ["马德里"] = "Madrid", ["巴塞罗那"] = "Barcelona",
        ["阿姆斯特丹"] = "Amsterdam", ["苏黎世"] = "Zurich", ["维也纳"] = "Vienna", ["莫斯科"] = "Moscow", ["伊斯坦布尔"] = "Istanbul",
        ["纽约"] = "New York", ["洛杉矶"] = "Los Angeles", ["旧金山"] = "San Francisco", ["西雅图"] = "Seattle", ["芝加哥"] = "Chicago", ["波士顿"] = "Boston", ["华盛顿"] = "Washington",
        ["多伦多"] = "Toronto", ["温哥华"] = "Vancouver", ["悉尼"] = "Sydney", ["墨尔本"] = "Melbourne", ["奥克兰"] = "Auckland",
    };

    private static readonly string[] Suffixes = ["维吾尔自治区", "壮族自治区", "回族自治区", "自治区", "特别行政区", "省", "市"];

    public static bool AllCjk(string s) => s.Length > 0 && s.All(c => c is >= '\u4e00' and <= '\u9fff');
    private static bool Chinese(string country) => country is "CN" or "HK" or "MO" or "TW";

    /// <summary>去掉「市 / 县 / 区 / 省」后缀；去掉后不足两个字就保留原名（例如「津市」）。</summary>
    public static string BaseName(string name)
    {
        if (!AllCjk(name) || name.Length < 3) return name;
        return name[^1] is '市' or '县' or '区' or '省' ? name[..^1] : name;
    }

    /// <summary>输入是省 / 直辖市 / 特区名（带不带「省」「自治区」都行）时返回它的简称。</summary>
    public static string? Province(string input)
    {
        var s = input.Trim();
        foreach (var suf in Suffixes)
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length) { s = s[..^suf.Length]; break; }
        return Provinces.ContainsKey(s) ? s : null;
    }

    /// <summary>省名对应的内置城市（省会在前）；不是省名返回空。</summary>
    public static List<WeatherCity> ProvinceCities(string input)
    {
        if (Province(input) is not { } p) return [];
        var region = p switch
        {
            "香港" or "澳门" => "中国",
            "台湾" => "中国台湾",
            "北京" or "天津" or "上海" or "重庆" => p + "市 · 中国",
            _ => p + " · 中国",
        };
        return Provinces[p].Select(c => new WeatherCity(c.Name, c.Lat, c.Lon, region)).ToList();
    }

    /// <summary>一个输入要查哪几个词（App 层另外再补繁体写法）。</summary>
    public static List<string> Queries(string input)
    {
        var name = input.Trim();
        var q = new List<string> { name };
        void Add(string s) { if (s.Length > 0 && !q.Contains(s)) q.Add(s); }
        if (!AllCjk(name)) return q;
        if (Province(name) is { } prov) { Add(prov); return q; }   // 省名：主要城市是内置的，再查一下同名地点就够了
        var b = BaseName(name);
        Add(b);
        if (name.Length >= 2 && !name.EndsWith('市')) Add(b + "市");
        if (Abroad.TryGetValue(b, out var en)) Add(en);
        return q;
    }

    public static string GeocodeUrl(string name) =>
        "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=20&language=zh&format=json";

    private static readonly string[] Skip = ["AIR", "RSTN", "BUS", "PRT", "HTL", "SCH", "HSP", "MT", "HLL", "PRK", "ISL", "STM", "LK", "BAY", "CAPE"];

    /// <summary>App 层设成「繁体 → 简体」（GeoNames 里不少中文名、地区名只有繁体），Core 里默认不转。</summary>
    public static Func<string, string> Simplify { get; set; } = s => s;

    public static List<CityHit> Parse(string json)
    {
        var list = new List<CityHit>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var rs) || rs.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in rs.EnumerateArray())
        {
            var fc = Str(r, "feature_code");
            if (Skip.Any(p => fc.StartsWith(p, StringComparison.Ordinal))) continue;
            if (Num(r, "latitude") is not { } lat || Num(r, "longitude") is not { } lon) continue;
            var country = Str(r, "country_code");
            var label = Simplify(Str(r, "name").Trim());
            if (Chinese(country)) label = BaseName(label);   // 「海口市」「大连市」→ 和「三亚」「北京」一个写法
            var admin2 = Simplify(Clean(Str(r, "admin2")));
            var admin1 = Simplify(Clean(Str(r, "admin1")));
            var c = new WeatherCity(label, lat, lon, Region(label, admin2, admin1, Simplify(Str(r, "country"))));
            if (c.Valid) list.Add(new CityHit(c, (long)(Num(r, "population") ?? 0), fc, country, label + "|" + admin1 + "|" + admin2 + "|" + country));
        }
        return list;
    }

    // 「臺灣省 or 台灣省」这种 GeoNames 原样带来的写法只留一个
    private static string Clean(string s) { var i = s.IndexOf(" or ", StringComparison.Ordinal); return (i > 0 ? s[..i] : s).Trim(); }

    /// <summary>地区说明：区县 · 省 · 国家，和地名同名的、彼此重复的去掉（不出现「北京 · 北京 · 中国」）。</summary>
    public static string Region(string label, string admin2, string admin1, string country)
    {
        var parts = new List<string>();
        var b = BaseName(label);
        void Add(string s, bool dropSameAsLabel)
        {
            if (s.Length == 0 || s == label) return;
            if (dropSameAsLabel && BaseName(s) == b) return;
            if (parts.Any(p => BaseName(p) == BaseName(s))) return;
            parts.Add(s);
        }
        Add(admin2, true);
        Add(admin1, false);   // 「北京」的省级就是「北京市」，留着说明是直辖市
        Add(country, false);
        return string.Join(" · ", parts);
    }

    private static int Level(string fc) => fc switch { "PPLC" => 0, "PPLA" => 1, "PPLA2" => 2, "PPLA3" => 3, "PPLA4" => 5, "PCLI" or "PCLS" => 1, _ => 6 };
    private static bool Major(CityHit h) => Level(h.Feature) <= 3 || h.Population >= 100_000;

    /// <summary>
    /// 省名内置城市在前，再按上面的档位排；去重后最多 <see cref="Shown"/> 条。
    /// queries = 实际查过的词（含繁体、英文名），名字和其中任何一个一致都算「名字完全一样」。
    /// 中文输入时，名字对不上又不是大城市的（接口按前缀补出来的「上海市场社」「Tokyo Disney Resort」）不要。
    /// </summary>
    public static List<WeatherCity> Rank(string input, IEnumerable<CityHit> found, IEnumerable<string>? queries = null)
    {
        var name = input.Trim();
        var cjk = AllCjk(name);
        var names = new HashSet<string>(new[] { name }.Concat(queries ?? []).Select(q => BaseName(Simplify(q.Trim()))), StringComparer.OrdinalIgnoreCase);
        bool Exact(CityHit h) => names.Contains(BaseName(h.City.Label));
        int Tier(CityHit h) => cjk ? (Major(h) ? 0 : 2) + (Chinese(h.Country) ? 0 : 1) : (Major(h) ? 0 : 1);
        var ranked = ProvinceCities(name);
        var keys = new HashSet<string>();
        foreach (var h in found.Where(h => !cjk || Exact(h) || Major(h))
                     .OrderBy(Tier).ThenByDescending(Exact).ThenByDescending(h => h.Population).ThenBy(h => Level(h.Feature)))
        {
            if (ranked.Any(r => Math.Abs(r.Lat - h.City.Lat) < 0.05 && Math.Abs(r.Lon - h.City.Lon) < 0.05)) continue;
            if (!keys.Add(h.Key)) continue;   // 同名同地区的村子（GeoNames 里常有两条）只留一个
            ranked.Add(h.City);
        }
        return ranked.Take(Shown).ToList();
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
