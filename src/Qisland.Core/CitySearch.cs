using System.Text.Json;

namespace Qisland.Core;

/// <summary>地理编码的一条结果，排序要用的字段都带着。</summary>
public sealed record CityHit(WeatherCity City, long Population, string Feature, string Country, string Key);

/// <summary>
/// 天气城市搜索。国内（省会、地级市、海南等省直辖县市）走内置离线表，不经过 Open-Meteo 地名库，
/// 所以搜「海南」会直接出海口、三亚、儋州，而不是日本海南市或同名村子。
/// 表里没有的（东京、伦敦，或更小的县乡）再查 Open-Meteo 地理编码（GeoNames），预报仍用 Open-Meteo。
/// </summary>
public static class CitySearch
{
    public const int Shown = 8;

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

    private sealed record LocalCity(string Name, string Province, double Lat, double Lon, int Pop, bool Capital, bool Major, string Ascii);

    private static readonly IReadOnlyList<LocalCity> Cities = Load();
    private static readonly HashSet<string> ProvinceNames = Cities.Select(c => c.Province).ToHashSet();

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
        var s = Simplify(input.Trim());
        foreach (var suf in Suffixes)
            if (s.EndsWith(suf, StringComparison.Ordinal) && s.Length > suf.Length) { s = s[..^suf.Length]; break; }
        return ProvinceNames.Contains(s) ? s : null;
    }

    /// <summary>省名对应的内置城市（省会、重点城市在前）；不是省名返回空。</summary>
    public static List<WeatherCity> ProvinceCities(string input)
    {
        if (Province(input) is not { } p) return [];
        return Ranked(Cities.Where(c => c.Province == p)).Take(Shown).Select(ToCity).ToList();
    }

    /// <summary>国内离线表搜索。有结果就不必再查 Open-Meteo 地名接口。</summary>
    public static List<WeatherCity> Local(string input)
    {
        var raw = Simplify(input.Trim());
        if (raw.Length == 0 || Cities.Count == 0) return [];
        if (Province(raw) is not null) return ProvinceCities(raw);

        var baseName = BaseName(raw);
        var ascii = Fold(raw);
        var exact = new List<LocalCity>();
        var prefix = new List<LocalCity>();
        var inside = new List<LocalCity>();
        foreach (var c in Cities)
        {
            var ea = Fold(c.Ascii);
            if (c.Name == raw || c.Name == baseName || (ascii.Length > 0 && ascii == ea))
                exact.Add(c);
            else if ((baseName.Length >= 2 && c.Name.StartsWith(baseName, StringComparison.Ordinal))
                     || (ascii.Length >= 3 && ea.StartsWith(ascii, StringComparison.Ordinal)))
                prefix.Add(c);
            else if ((baseName.Length >= 2 && c.Name.Contains(baseName, StringComparison.Ordinal))
                     || (ascii.Length >= 3 && ea.Contains(ascii, StringComparison.Ordinal)))
                inside.Add(c);
        }
        return Ranked(exact).Concat(Ranked(prefix)).Concat(Ranked(inside)).Take(Shown).Select(ToCity).ToList();
    }

    /// <summary>离线表里距给定坐标最近的城市；超过 <paramref name="maxKm"/> 视为没有。</summary>
    public static WeatherCity? Nearest(double lat, double lon, double maxKm = 80)
    {
        LocalCity? best = null;
        var bestKm = maxKm;
        foreach (var c in Cities)
        {
            var km = HaversineKm(lat, lon, c.Lat, c.Lon);
            if (km < bestKm) { bestKm = km; best = c; }
        }
        return best is null ? null : ToCity(best);
    }

    /// <summary>
    /// 自动定位时要不要用离线表的中文名替换 wttr 给的地名：
    /// 显示名为空，或就是这个城市的中文名 / 英文名时才换，不覆盖更具体的区县名。
    /// </summary>
    public static bool Refines(string label, WeatherCity city)
    {
        if (string.IsNullOrWhiteSpace(label)) return true;
        var b = BaseName(Simplify(label.Trim()));
        if (b.Equals(city.Label, StringComparison.OrdinalIgnoreCase)) return true;
        var a = Fold(label);
        if (a.Length < 3) return false;
        return Cities.Any(c => c.Name == city.Label && Math.Abs(c.Lat - city.Lat) < 0.05 && Fold(c.Ascii) == a);
    }

    /// <summary>一个输入要查哪几个词（App 层另外再补繁体写法）。国内命中离线表时不会走到这里。</summary>
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
    private static bool MajorHit(CityHit h) => Level(h.Feature) <= 3 || h.Population >= 100_000;

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
        int Tier(CityHit h) => cjk ? (MajorHit(h) ? 0 : 2) + (Chinese(h.Country) ? 0 : 1) : (MajorHit(h) ? 0 : 1);
        var ranked = ProvinceCities(name);
        var keys = new HashSet<string>();
        foreach (var h in found.Where(h => !cjk || Exact(h) || MajorHit(h))
                     .OrderBy(Tier).ThenByDescending(Exact).ThenByDescending(h => h.Population).ThenBy(h => Level(h.Feature)))
        {
            if (ranked.Any(r => Math.Abs(r.Lat - h.City.Lat) < 0.05 && Math.Abs(r.Lon - h.City.Lon) < 0.05)) continue;
            if (!keys.Add(h.Key)) continue;   // 同名同地区的村子（GeoNames 里常有两条）只留一个
            ranked.Add(h.City);
        }
        return ranked.Take(Shown).ToList();
    }

    private static IEnumerable<LocalCity> Ranked(IEnumerable<LocalCity> xs) =>
        xs.OrderBy(c => c.Capital ? 0 : 1).ThenBy(c => c.Major ? 0 : 1).ThenByDescending(c => c.Pop).ThenBy(c => c.Name, StringComparer.Ordinal);

    private static WeatherCity ToCity(LocalCity c) => new(c.Name, c.Lat, c.Lon, LocalRegion(c));

    private static string LocalRegion(LocalCity c)
    {
        if (c.Province is "香港" or "澳门") return "中国";
        if (c.Province == "台湾") return "中国台湾";
        if (c.Name == c.Province) return "中国";   // 直辖市：不写「北京 · 北京 · 中国」
        return c.Province + " · 中国";
    }

    private static string Fold(string s) =>
        s.Trim().ToLowerInvariant().Replace("'", "", StringComparison.Ordinal).Replace("’", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * r * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    private static IReadOnlyList<LocalCity> Load()
    {
        try
        {
            using var s = typeof(CitySearch).Assembly.GetManifestResourceStream("Qisland.Core.data.china-cities.json");
            if (s == null) return [];
            using var doc = JsonDocument.Parse(s);
            if (!doc.RootElement.TryGetProperty("cities", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
            var list = new List<LocalCity>();
            foreach (var e in arr.EnumerateArray())
            {
                var n = Str(e, "n");
                var p = Str(e, "p");
                if (n.Length == 0 || p.Length == 0) continue;
                if (Num(e, "lat") is not { } lat || Num(e, "lon") is not { } lon) continue;
                list.Add(new LocalCity(n, p, lat, lon, (int)(Num(e, "pop") ?? 0),
                    (int)(Num(e, "cap") ?? 0) != 0, (int)(Num(e, "pri") ?? 0) != 0, Str(e, "e")));
            }
            return list;
        }
        catch { return []; }
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static double? Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
