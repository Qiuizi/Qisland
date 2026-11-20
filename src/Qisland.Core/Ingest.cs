using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// <c>POST /v1/qisland/ingest</c> 的入口。认 Claude/Codex 原始 hook、规范化会话、
/// Info Station / CourseDesk / JARVIS / DeepSeek 窗口与 Toast。
/// </summary>
public static class QislandIngest
{
    public static IReadOnlyList<PushDraft> Parse(string json, DateOnly? today = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<PushDraft>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Array.Empty<PushDraft>();
            var activities = ReadActivities(root, today ?? DateOnly.FromDateTime(DateTime.Now));
            return activities.Select(PushMapping.From).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<PushDraft>();
        }
    }

    private static IReadOnlyList<IslandActivity> ReadActivities(JsonElement root, DateOnly today)
    {
        var kind = Text(root, "kind").ToLowerInvariant();
        var payload = root.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        if (kind is "agent" or "claude" or "codex")
            return One(AgentEventParser.Parse(payload.GetRawText(), kind == "codex" ? "codex" : kind == "claude" ? "claude-code" : null));
        if (kind is "deepseek-harness" or "deepseek")
        {
            if (payload.TryGetProperty("process_running", out _) || payload.TryGetProperty("window_title", out _))
            {
                var running = payload.TryGetProperty("process_running", out var flag) && flag.ValueKind == JsonValueKind.True;
                var privacy = payload.TryGetProperty("privacy", out var p) && p.ValueKind == JsonValueKind.True;
                return One(DeepSeekHarnessSignals.FromWindow(running, Text(payload, "window_title"), privacy));
            }
            return One(AgentEventParser.Parse(payload.GetRawText(), "deepseek-harness"));
        }
        if (kind is "deepseek-toast")
            return One(DeepSeekHarnessSignals.FromToast(Text(payload, "title"), Text(payload, "body"),
                payload.TryGetProperty("privacy", out var priv) && priv.ValueKind == JsonValueKind.True));
        if (kind is "info-station" or "infostation")
            return InfoBundle(payload, today);
        if (kind is "coursedesk" or "course")
            return CourseDeskProvider.ReadSnapshot(payload.GetRawText());
        if (kind is "jarvis")
            return One(JarvisContract.Parse(payload));
        if (kind is "notification")
        {
            var app = NotificationMirror.Detect(Text(payload, "app"), Text(payload, "aumid"));
            if (app is null or ChatApp.DeepSeekHarness) return Array.Empty<IslandActivity>();
            var decision = NotificationMirror.Present(app.Value, Text(payload, "title"), Text(payload, "body"),
                new NotificationPolicy(true, true, true, payload.TryGetProperty("privacy", out var pr) && pr.ValueKind == JsonValueKind.True));
            if (!decision.Show) return Array.Empty<IslandActivity>();
            var chat = app == ChatApp.WeChat ? "wechat" : "qq";
            return new[]
            {
                new IslandActivity(chat + ":" + decision.Title, chat, decision.Title, decision.Body,
                    ActivityKind.Info, 75, DurationSeconds: 8, ResetTtl: true),
            };
        }

        if (root.TryGetProperty("hook_event_name", out _) || root.TryGetProperty("hookEventName", out _))
            return One(AgentEventParser.Parse(root.GetRawText()));
        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && AgentEventParser.Parse(root.GetRawText()) is { } codex)
            return new[] { codex };
        var source = AgentEventParser.NormalizeSource(Text(root, "source"));
        if (source is "claude-code" or "codex" or "deepseek-harness" or "doubao" or "trae" or "workbuddy")
            return One(AgentEventParser.Parse(root.GetRawText()));
        if (root.TryGetProperty("yuketang", out _) || root.TryGetProperty("chaoxing", out _))
            return CourseDeskProvider.ReadSnapshot(root.GetRawText());
        if (root.TryGetProperty("items", out _) || root.TryGetProperty("sections", out _) || root.TryGetProperty("deadlines", out _))
            return InfoBundle(root, today);
        if (Text(root, "title").Length > 0)
        {
            var id = Text(root, "id");
            if (id.Length == 0) id = "push:" + Guid.NewGuid().ToString("N");
            return new[]
            {
                new IslandActivity(id, Text(root, "source").Length == 0 ? "push" : Text(root, "source"),
                    Text(root, "title"), Text(root, "body"), ActivityKind.Info, 40,
                    DurationSeconds: 30, ResetTtl: true,
                    OpenUri: IslandActivity.SafeUri(Text(root, "open_uri").Length > 0 ? Text(root, "open_uri") : Text(root, "url")),
                    AppId: Text(root, "app_id").Length > 0 ? Text(root, "app_id") : null),
            };
        }
        return Array.Empty<IslandActivity>();
    }

    private static IReadOnlyList<IslandActivity> InfoBundle(JsonElement root, DateOnly today)
    {
        var list = new List<IslandActivity>();
        if (root.TryGetProperty("items", out _))
            list.AddRange(InfoStationProvider.ReadItems(root.GetRawText(), 80));
        if (root.TryGetProperty("greeting", out _) || root.TryGetProperty("sections", out _))
        {
            var brief = InfoStationProvider.ReadBrief(root.GetRawText(), today);
            if (brief is not null) list.Add(brief);
        }
        if (root.TryGetProperty("deadlines", out var deadlines))
            list.AddRange(InfoStationProvider.ReadDeadlines(deadlines.GetRawText()));
        return list;
    }

    private static IReadOnlyList<IslandActivity> One(IslandActivity? activity)
        => activity is null ? Array.Empty<IslandActivity>() : new[] { activity };

    private static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        return (value.GetString() ?? "").Trim();
    }
}
