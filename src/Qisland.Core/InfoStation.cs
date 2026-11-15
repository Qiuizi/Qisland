using System.Globalization;
using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// Info Station 本地 HTTP 的读取结果。
/// 对接的是 <c>GET /api/items?view=featured</c>、<c>/api/briefs/today</c>、<c>/api/deadlines</c>。
/// </summary>
public sealed class InfoStationProvider : IActivityProvider
{
    private readonly List<IslandActivity> _items = new();
    public int MinScore { get; set; } = 80;
    public string Id => "info-station";
    public string DisplayName => "信息站";
    public bool Enabled { get; set; } = true;

    public void ApplyItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array && !root.TryGetProperty("items", out _)) return;
        _items.RemoveAll(a => a.Id.StartsWith("info:item:", StringComparison.Ordinal));
        foreach (var item in ReadItems(json, MinScore)) _items.Add(item);
    }

    public void ApplyBrief(string json, DateOnly today)
    {
        _items.RemoveAll(a => a.Id.StartsWith("info:brief:", StringComparison.Ordinal));
        var brief = ReadBrief(json, today);
        if (brief is not null) _items.Add(brief);
    }

    public void ApplyDeadlines(string json)
    {
        _items.RemoveAll(a => a.Id.StartsWith("info:deadline:", StringComparison.Ordinal));
        foreach (var item in ReadDeadlines(json)) _items.Add(item);
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled ? ActivityPriority.Rank(_items).Take(6).ToList() : Array.Empty<IslandActivity>();

    public static IReadOnlyList<IslandActivity> ReadItems(string json, int minScore)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("items", out var items) ? items : default;
        if (array.ValueKind != JsonValueKind.Array) return Array.Empty<IslandActivity>();
        var list = new List<IslandActivity>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Flag(item, "is_read")) continue;
            var score = Number(item, "score") ?? Number(item, "rank_score") ?? 0;
            if (score < minScore) continue;
            var id = First(item, "id");
            var title = First(item, "title");
            if (title.Length == 0) continue;
            var source = First(item, "source_name", "source");
            list.Add(new IslandActivity(
                Id: "info:item:" + (id.Length == 0 ? title : id),
                Source: "info-station",
                Title: title,
                Body: Join(source, First(item, "summary"), score.ToString("0", CultureInfo.InvariantCulture) + " 分"),
                Kind: ActivityKind.Info,
                Priority: score >= 90 ? 62 : 48,
                DurationSeconds: 180,
                ResetTtl: true));
        }
        return list;
    }

    public static IslandActivity? ReadBrief(string json, DateOnly today)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var dateText = First(root, "date");
        var generated = First(root, "generated_at", "created_at");
        var day = dateText.Length >= 10 ? dateText[..10] : generated.Length >= 10 ? generated[..10] : "";
        if (!string.Equals(day, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            return null;
        var greeting = First(root, "greeting");
        if (greeting.Length == 0) greeting = "今日简报已生成";
        return new IslandActivity(
            Id: "info:brief:" + day,
            Source: "info-station",
            Title: "每日简报已就绪",
            Body: greeting,
            Kind: ActivityKind.Info,
            Priority: 36,
            DurationSeconds: 240,
            ResetTtl: true);
    }

    public static IReadOnlyList<IslandActivity> ReadDeadlines(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("deadlines", out var items) ? items : default;
        if (array.ValueKind != JsonValueKind.Array) return Array.Empty<IslandActivity>();
        var list = new List<IslandActivity>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var days = Number(item, "days_left");
            if (days is > 7) continue;
            var title = First(item, "title");
            if (title.Length == 0) continue;
            var id = First(item, "id");
            var left = days is null ? "" : days <= 1 ? "今天或明天" : $"{days:0.#} 天后";
            var priority = days is null ? 44 : days <= 1 ? 84 : days <= 3 ? 72 : 46;
            list.Add(new IslandActivity(
                Id: "info:deadline:" + (id.Length == 0 ? title : id),
                Source: "info-station",
                Title: title,
                Body: Join(First(item, "source_name"), left, First(item, "deadline_note")),
                Kind: days is <= 1 ? ActivityKind.Warning : ActivityKind.Info,
                Priority: priority,
                DurationSeconds: 300,
                ResetTtl: true));
        }
        return list;
    }

    private static string Join(params string[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string First(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            var text = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return "";
    }

    private static double? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.GetDouble();
    }

    private static bool Flag(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.GetInt32() != 0,
            _ => false,
        };
    }
}
