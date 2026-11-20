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
            var r = doc.RootElement;
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
    /// <summary>先完整解析、校验，不抛异常；调用方据此决定删除文件还是移到 failed。</summary>
    public static InboxDecoded Decode(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Contains("\"activities\"") || trimmed.Contains("\"kind\"") || trimmed.StartsWith('['))
            return QislandIngest.TryParse(text, out var drafts, out var err)
                ? new(true, drafts.Select(d => (d, (double?)null)).ToList(), null)
                : new(false, [], err);
        return PushParser.TryParse(text, out var d1, out var p, out var e)
            ? new(true, [(d1!, p)], null)
            : new(false, [], e);
    }
}
