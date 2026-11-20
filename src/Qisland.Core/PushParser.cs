using System.Globalization;
using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// <c>POST /v1/island/push</c> 和收件箱里单条推送的解析。
/// 字段类型不对（比如 duration 给了 1.5、超出范围、给了对象）不再抛异常，返回 false 和原因：HTTP 回 400，收件箱移到 failed。
/// 文字字段给了别的类型照旧当没给（兼容老调用方）；数字字段接受数字或数字字符串。
/// </summary>
public static class PushParser
{
    public static bool TryParse(string body, out PushDraft? draft, out double? progress, out string? error)
    {
        draft = null; progress = null; error = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return TryParse(doc.RootElement, out draft, out progress, out error);
        }
        catch (JsonException) { error = InboxDecoded.NotJsonError; return false; }
    }

    /// <summary>已经解析好的 JSON（<see cref="IslandPayload"/> 只解析一次）。</summary>
    public static bool TryParse(JsonElement r, out PushDraft? draft, out double? progress, out string? error)
    {
        draft = null; progress = null; error = null;
        try
        {
            if (r.ValueKind != JsonValueKind.Object) { error = "需要 JSON 对象"; return false; }
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var title = S("title");
            if (title.Length == 0) { error = "需要 title"; return false; }

            int? duration = null;
            foreach (var n in new[] { "duration_seconds", "duration", "durationSeconds" })
            {
                if (!r.TryGetProperty(n, out var v) || v.ValueKind == JsonValueKind.Null) continue;
                if (!Int(v, out var secs) || secs < 0) { error = $"{n} 要是非负整数秒"; return false; }
                duration ??= secs;
            }
            if (r.TryGetProperty("progress", out var p) && p.ValueKind != JsonValueKind.Null)
            {
                if (!Num(p, out var pv) || double.IsNaN(pv) || double.IsInfinity(pv)) { error = "progress 要是数字（0–1 或 0–100）"; return false; }
                progress = Math.Clamp(pv > 1 ? pv / 100 : pv, 0, 1);
            }
            var id = S("id");
            draft = new PushDraft
            {
                Id = id.Length > 0 ? id : "push:" + Guid.NewGuid().ToString("N")[..8],
                Title = title,
                Body = S("body"),
                Subtitle = S("subtitle"),
                Icon = S("icon"),
                Type = S("type").ToLowerInvariant(),
                Priority = S("priority").ToLowerInvariant(),
                Source = S("source").Length > 0 ? S("source") : "push",
                DurationSeconds = duration ?? 12,
                // 调用方明确给了时长：进度卡也按它来，不再抬到 600 秒
                DurationExplicit = duration != null,
                ResetTtl = true,
                OpenUri = S("open_uri").Length > 0 ? S("open_uri") : S("url"),
            };
            return true;
        }
        catch (JsonException) { error = InboxDecoded.NotJsonError; return false; }
    }

    private static bool Int(JsonElement v, out int x)
    {
        x = 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out x),
            JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x),
            _ => false,
        };
    }

    private static bool Num(JsonElement v, out double x)
    {
        x = 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out x),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out x),
            _ => false,
        };
    }
}

/// <summary>
/// 收件箱里一个文件的内容：认出是哪种格式，解析成要上岛的卡片；不合要求 Ok = false。
/// NotJson：根本不是完整的 JSON，可能是还没写完，调用方先按退避重试，一直这样再放弃。
/// </summary>
public sealed record InboxDecoded(bool Ok, IReadOnlyList<(PushDraft Draft, double? Progress)> Cards, string? Error)
{
    public const string NotJsonError = "不是合法的 JSON";
    public bool NotJson => !Ok && Error == NotJsonError;
}

public static class InboxDecoder
{
    /// <summary>收件箱：自动判定格式（同 <see cref="IslandPayload.Decode"/> 的 Auto）。</summary>
    public static InboxDecoded Decode(string text) => IslandPayload.Decode(text, PayloadRoute.Auto);
}

/// <summary>消息从哪条路进来：收件箱自动判定；HTTP 的 /v1/island/push 和 /v1/qisland/ingest 各自指定。</summary>
public enum PayloadRoute { Auto, Push, Ingest }

/// <summary>
/// 收件箱和 HTTP 接口共用的唯一入口：JSON 只解析一次，按顶层字段选解析器，结果格式相同，两条路不会走岔。
/// 自动判定：顶层是数组，或顶层对象有 kind / activities 属性 → 接入格式（QislandIngest）；否则 → 普通推送。
/// 只看顶层属性名，不看正文：标题、正文里出现 "kind" 不影响。
/// </summary>
public static class IslandPayload
{
    public static InboxDecoded Decode(string text, PayloadRoute route)
    {
        if (route == PayloadRoute.Ingest && string.IsNullOrWhiteSpace(text)) return new(true, [], null);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return new(false, [], InboxDecoded.NotJsonError); }
        using (doc)
        {
            var root = doc.RootElement;
            var ingest = route switch
            {
                PayloadRoute.Ingest => true,
                PayloadRoute.Push => false,
                _ => IsIngest(root),
            };
            if (ingest)
                return QislandIngest.TryParse(root, out var drafts, out var err)
                    ? new(true, drafts.Select(d => (d, (double?)null)).ToList(), null)
                    : new(false, [], err);
            return PushParser.TryParse(root, out var d1, out var p, out var e)
                ? new(true, [(d1!, p)], null)
                : new(false, [], e);
        }
    }

    /// <summary>按顶层结构判定是不是接入格式。</summary>
    public static bool IsIngest(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array
        || (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("kind", out _) || root.TryGetProperty("activities", out _)));
}
