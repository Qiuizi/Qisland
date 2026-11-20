using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// <c>POST /v1/qisland/ingest</c> 的入口。认 Claude/Codex 原始 hook、规范化会话、
/// Info Station / CourseDesk / JARVIS / DeepSeek 窗口与 Toast。
/// </summary>
public static class QislandIngest
{
    /// <summary>
    /// 这条 JSON 属于哪种接入格式。收件箱和 HTTP 的自动判定只看这里，避免两边各写一套规则。
    /// PlainTitle 是旧接口的「只有标题也算推送」，交给普通推送解析，才保得住进度和明确给的时长。
    /// </summary>
    internal enum Shape
    {
        Empty,
        None,
        Agent,
        DeepSeekAgent,
        DeepSeekWindow,
        DeepSeekToast,
        InfoStation,
        CourseDesk,
        Jarvis,
        Notification,
        Hook,
        AgentType,
        AgentSource,
        CourseSnapshot,
        InfoBundle,
        PlainTitle,
    }

    /// <summary>
    /// 解析；不是合法 JSON 或字段类型不对（比如 deadlines 给了字符串）返回 false 和原因，不抛异常。
    /// 空串 / 不是对象算合法但没有内容（和以前一样）。
    /// </summary>
    public static bool TryParse(string json, out IReadOnlyList<PushDraft> drafts, out string? error, DateOnly? today = null)
    {
        drafts = Array.Empty<PushDraft>();
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryParse(doc.RootElement, out drafts, out error, today);
        }
        catch (JsonException) { error = InboxDecoded.NotJsonError; return false; }
    }

    /// <summary>已经解析好的 JSON（<see cref="IslandPayload"/> 只解析一次）。</summary>
    public static bool TryParse(JsonElement root, out IReadOnlyList<PushDraft> drafts, out string? error, DateOnly? today = null)
    {
        drafts = Array.Empty<PushDraft>();
        error = null;
        try
        {
            if (root.ValueKind != JsonValueKind.Object) return true;
            var activities = ReadActivities(root, today ?? DateOnly.FromDateTime(DateTime.Now));
            drafts = activities.Select(PushMapping.From).ToList();
            return true;
        }
        catch (JsonException) { error = InboxDecoded.NotJsonError; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        {
            error = "字段类型不对：" + ex.Message;
        }
        return false;
    }

    /// <summary>同 <see cref="TryParse"/>，不合要求时返回空列表（不抛异常）。</summary>
    public static IReadOnlyList<PushDraft> Parse(string json, DateOnly? today = null) =>
        TryParse(json, out var drafts, out _, today) ? drafts : Array.Empty<PushDraft>();

    private static IReadOnlyList<IslandActivity> ReadActivities(JsonElement root, DateOnly today)
    {
        var shape = Classify(root);
        var kind = Text(root, "kind").ToLowerInvariant();
        var payload = root.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? nested
            : root;

        switch (shape)
        {
            case Shape.Agent:
                return One(AgentEventParser.Parse(payload.GetRawText(), kind == "codex" ? "codex" : kind == "claude" ? "claude-code" : null));
            case Shape.DeepSeekAgent:
                return One(AgentEventParser.Parse(payload.GetRawText(), "deepseek-harness"));
            case Shape.DeepSeekWindow:
            {
                var running = payload.TryGetProperty("process_running", out var flag) && flag.ValueKind == JsonValueKind.True;
                var privacy = payload.TryGetProperty("privacy", out var p) && p.ValueKind == JsonValueKind.True;
                return One(DeepSeekHarnessSignals.FromWindow(running, Text(payload, "window_title"), privacy));
            }
            case Shape.DeepSeekToast:
                return One(DeepSeekHarnessSignals.FromToast(Text(payload, "title"), Text(payload, "body"),
                    payload.TryGetProperty("privacy", out var priv) && priv.ValueKind == JsonValueKind.True));
            case Shape.InfoStation:
                return InfoBundle(payload, today);
            case Shape.CourseDesk:
                return CourseDeskProvider.ReadSnapshot(payload.GetRawText());
            case Shape.Jarvis:
                return One(JarvisContract.Parse(payload));
            case Shape.Notification:
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
            case Shape.Hook:
            case Shape.AgentType:
                return One(AgentEventParser.Parse(root.GetRawText()));
            case Shape.AgentSource:
                return One(AgentEventParser.Parse(root.GetRawText()));
            case Shape.CourseSnapshot:
                return CourseDeskProvider.ReadSnapshot(root.GetRawText());
            case Shape.InfoBundle:
                return InfoBundle(root, today);
            case Shape.PlainTitle:
                return One(PlainPush(root));
            default:
                return Array.Empty<IslandActivity>();
        }
    }

    /// <summary>自动判定：和 <see cref="ReadActivities"/> 用的是同一份识别结果。</summary>
    internal static Shape Classify(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return Shape.Empty;
        if (root.ValueKind != JsonValueKind.Object) return Shape.None;

        var kind = Text(root, "kind").ToLowerInvariant();
        if (kind is "agent" or "claude" or "codex") return Shape.Agent;
        if (kind is "deepseek-harness" or "deepseek")
        {
            var payload = root.TryGetProperty("payload", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            return payload.TryGetProperty("process_running", out _) || payload.TryGetProperty("window_title", out _)
                ? Shape.DeepSeekWindow
                : Shape.DeepSeekAgent;
        }
        if (kind is "deepseek-toast") return Shape.DeepSeekToast;
        if (kind is "info-station" or "infostation") return Shape.InfoStation;
        if (kind is "coursedesk" or "course") return Shape.CourseDesk;
        if (kind is "jarvis") return Shape.Jarvis;
        if (kind is "notification") return Shape.Notification;

        if (root.TryGetProperty("hook_event_name", out _) || root.TryGetProperty("hookEventName", out _)) return Shape.Hook;
        if (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && AgentEventParser.Parse(root.GetRawText()) is not null) return Shape.AgentType;
        var source = AgentEventParser.NormalizeSource(Text(root, "source"));
        if (source is "claude-code" or "codex" or "deepseek-harness" or "doubao" or "trae" or "workbuddy") return Shape.AgentSource;
        if (root.TryGetProperty("yuketang", out _) || root.TryGetProperty("chaoxing", out _)) return Shape.CourseSnapshot;
        if (root.TryGetProperty("items", out _) || root.TryGetProperty("sections", out _) || root.TryGetProperty("deadlines", out _)) return Shape.InfoBundle;
        if (Text(root, "title").Length > 0) return Shape.PlainTitle;
        return Shape.None;
    }

    /// <summary>
    /// 收件箱 / HTTP 自动判定用的「是不是接入格式」。PlainTitle（普通推送）和 None 不算。
    /// </summary>
    public static bool Recognizes(JsonElement root) => Classify(root) is not Shape.None and not Shape.PlainTitle;

    private static IslandActivity PlainPush(JsonElement root)
    {
        var id = Text(root, "id");
        if (id.Length == 0) id = "push:" + Guid.NewGuid().ToString("N");
        return new IslandActivity(id, Text(root, "source").Length == 0 ? "push" : Text(root, "source"),
            Text(root, "title"), Text(root, "body"), ActivityKind.Info, 40,
            DurationSeconds: 30, ResetTtl: true,
            OpenUri: IslandActivity.SafeUri(Text(root, "open_uri").Length > 0 ? Text(root, "open_uri") : Text(root, "url")),
            AppId: Text(root, "app_id").Length > 0 ? Text(root, "app_id") : null);
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
