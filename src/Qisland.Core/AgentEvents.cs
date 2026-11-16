using System.Text.Json;

namespace Qisland.Core;

/// <summary>把 Claude Code hook、Codex notify、以及规范化 JSON 收成一条会话活动。</summary>
public static class AgentEventParser
{
    public static IslandActivity? Parse(string json, string? defaultSource = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc.RootElement, defaultSource);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IslandActivity? Parse(JsonElement root, string? defaultSource = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var source = FirstString(root, "source", "agent", "provider");
        var hook = FirstString(root, "hook_event_name", "hookEventName");
        var codexType = FirstString(root, "type");
        if (string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(defaultSource) && !string.IsNullOrEmpty(hook))
            source = defaultSource; // 走专用入口的 hook（如 /v1/agent/codex），来源以入口为准
        if (string.IsNullOrEmpty(source))
        {
            if (!string.IsNullOrEmpty(hook)) source = "claude-code";
            else if (LooksLikeCodex(codexType)) source = "codex";
            else source = defaultSource ?? "";
        }

        source = NormalizeSource(source);
        if (source is not ("claude-code" or "codex" or "deepseek-harness" or "doubao" or "trae" or "workbuddy")) return null;

        // 会话刚打开、模型请求这类事件不代表在干活，不上岛
        if (hook is "SessionStart" or "PreModelRequest" or "PostModelRequest" or "PreCompact" or "PostCompact") return null;
        var kind = Classify(source, hook, codexType, root);
        var session = FirstString(root, "session_id", "sessionId", "thread-id", "thread_id", "turn-id", "id");
        if (string.IsNullOrEmpty(session)) session = "default";
        var tool = FirstString(root, "tool_name", "toolName", "command");
        if (root.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object)
        {
            var cmd = FirstString(ti, "command", "description", "file_path", "path");
            if (cmd.Length > 0) tool = tool.Length > 0 ? tool + " · " + OneLine(cmd) : OneLine(cmd);
        }
        var detail = FirstString(root, "detail", "message", "last-assistant-message", "last_assistant_message", "error", "reason");
        if (string.IsNullOrEmpty(detail) && hook == "UserPromptSubmit") detail = OneLine(FirstString(root, "prompt"));
        if (string.IsNullOrEmpty(detail) && !string.IsNullOrEmpty(tool)) detail = tool;
        detail = OneLine(detail);
        var cwd = FirstString(root, "cwd");
        if (!string.IsNullOrEmpty(cwd))
        {
            var name = cwd.TrimEnd('\\', '/');
            var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
            detail = string.IsNullOrEmpty(detail) ? name : detail + " · " + name;
        }

        var quota = ReadQuota(root);
        var subtitle = quota ?? "";
        var title = FirstString(root, "title");
        if (string.IsNullOrEmpty(title)) title = DefaultTitle(source, kind);

        var priority = kind switch
        {
            ActivityKind.WaitingApproval => 100,
            ActivityKind.Error => 90,
            ActivityKind.Running => 55,
            ActivityKind.Done => 20,
            _ => 40,
        };
        var seconds = kind switch
        {
            ActivityKind.WaitingApproval => 4 * 60 * 60,
            ActivityKind.Running => 4 * 60 * 60,
            ActivityKind.Error => 25,
            ActivityKind.Done => hook is "SessionEnd" ? 2 : 45,
            _ => 30,
        };

        return new IslandActivity(
            Id: "agent:" + source + ":" + session,
            Source: source,
            Title: title,
            Body: Trim(detail, 180),
            Kind: kind,
            Priority: priority,
            Subtitle: subtitle,
            DurationSeconds: seconds,
            ResetTtl: kind is ActivityKind.Done or ActivityKind.Error);
    }

    public static string NormalizeSource(string? source)
    {
        var s = (source ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        return s switch
        {
            "claude" or "claude-code" or "claudecode" => "claude-code",
            "codex" or "codex-cli" => "codex",
            "deepseek" or "deepseek-harness" or "dsh" or "dsh-desktop" => "deepseek-harness",
            "doubao" or "豆包" => "doubao",
            "trae" or "trae-cn" => "trae",
            "workbuddy" or "work-buddy" => "workbuddy",
            _ => s,
        };
    }

    private static bool LooksLikeCodex(string type)
    {
        if (string.IsNullOrEmpty(type)) return false;
        var t = type.ToLowerInvariant();
        return t.StartsWith("agent-") || t.Contains("approval") || t is "turn-started" or "turn-failed" or "task-complete";
    }

    private static ActivityKind Classify(string source, string hook, string codexType, JsonElement root)
    {
        var status = FirstString(root, "status", "state", "event").ToLowerInvariant().Replace('-', '_');
        if (status is "waiting_approval" or "awaiting_approval" or "needs_approval" or "approval" or "permission")
            return ActivityKind.WaitingApproval;
        if (status is "error" or "failed" or "failure") return ActivityKind.Error;
        if (status is "done" or "completed" or "complete" or "idle" or "stopped") return ActivityKind.Done;
        if (status is "running" or "working" or "busy") return ActivityKind.Running;

        if (source == "claude-code" || !string.IsNullOrEmpty(hook))
        {
            var name = hook.Trim();
            if (name.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase)) return ActivityKind.WaitingApproval;
            if (name.Equals("Notification", StringComparison.OrdinalIgnoreCase)) return ActivityKind.WaitingApproval;
            if (name.Contains("Failure", StringComparison.OrdinalIgnoreCase) || HasError(root)) return ActivityKind.Error;
            if (name is "Stop" or "SessionEnd" or "StopFailure" or "Interrupt") return ActivityKind.Done;
            return ActivityKind.Running;
        }

        var type = codexType.ToLowerInvariant();
        if (type.Contains("approval")) return ActivityKind.WaitingApproval;
        if (type.Contains("fail") || type.Contains("error")) return ActivityKind.Error;
        if (type.Contains("complete") || type.Contains("done")) return ActivityKind.Done;
        return ActivityKind.Running;
    }

    private static bool HasError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var err)) return false;
        return err.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(err.GetString()),
            JsonValueKind.Object or JsonValueKind.Array => true,
            _ => false,
        };
    }

    private static string? ReadQuota(JsonElement root)
    {
        if (!root.TryGetProperty("quota", out var quota) || quota.ValueKind != JsonValueKind.Object) return null;
        if (!TryNumber(quota, "used", out var used)) return null;
        if (!TryNumber(quota, "limit", out var limit) && !TryNumber(quota, "max", out limit)) return null;
        var unit = FirstString(quota, "unit", "window");
        var text = $"{used:0}/{limit:0}";
        return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
    }

    private static string DefaultTitle(string source, ActivityKind kind)
    {
        var name = source switch
        {
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            "deepseek-harness" => "DeepSeek Harness",
            _ => AgentToasts.Name(source) ?? source,
        };
        return kind switch
        {
            ActivityKind.WaitingApproval => name + " 等待确认",
            ActivityKind.Error => name + " 出错",
            ActivityKind.Done => name + " 已完成",
            ActivityKind.Running => name + " 进行中",
            _ => name,
        };
    }

    public static string FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            }
        }
        return "";
    }

    private static bool TryNumber(JsonElement root, string name, out double number)
    {
        number = 0;
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return false;
        number = value.GetDouble();
        return true;
    }

    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static string Trim(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// 本地可读的额度文件。只认显式 JSON（used + limit）。
/// 不读取 DeepSeek Harness 的 IndexedDB / LevelDB。
/// </summary>
public static class AgentQuotaReader
{
    public static IReadOnlyList<IslandActivity> ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Array.Empty<IslandActivity>();
        var lowered = path.Replace('\\', '/').ToLowerInvariant();
        if (lowered.Contains("indexeddb") || lowered.Contains("leveldb") || lowered.Contains("/@deepseek-ai/"))
            return Array.Empty<IslandActivity>();
        try
        {
            return ReadJson(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return Array.Empty<IslandActivity>();
        }
        catch (JsonException)
        {
            return Array.Empty<IslandActivity>();
        }
    }

    public static IReadOnlyList<IslandActivity> ReadJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var found = new List<IslandActivity>();
        Walk(doc.RootElement, "", found);
        return found;
    }

    private static void Walk(JsonElement node, string path, List<IslandActivity> found)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (TryPair(node, out var used, out var limit))
        {
            var source = GuessSource(path);
            if (source is not null && found.All(a => a.Source != source))
            {
                var unit = AgentEventParser.FirstString(node, "unit", "window");
                found.Add(new IslandActivity(
                    Id: "agent:quota:" + source,
                    Source: source,
                    Title: (source == "claude-code" ? "Claude" : "Codex") + " 额度",
                    Body: string.IsNullOrEmpty(unit) ? $"{used:0}/{limit:0}" : $"{used:0}/{limit:0} {unit}",
                    Kind: ActivityKind.Info,
                    Priority: 15,
                    DurationSeconds: 600));
            }
        }
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
                Walk(prop.Value, path + "/" + prop.Name, found);
        }
    }

    private static bool TryPair(JsonElement node, out double used, out double limit)
    {
        used = limit = 0;
        if (!node.TryGetProperty("used", out var u) || u.ValueKind != JsonValueKind.Number) return false;
        if (!node.TryGetProperty("limit", out var l) && !node.TryGetProperty("max", out l)) return false;
        if (l.ValueKind != JsonValueKind.Number) return false;
        used = u.GetDouble();
        limit = l.GetDouble();
        return limit > 0 && used >= 0 && used <= limit * 5;
    }

    private static string? GuessSource(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.Contains("claude")) return "claude-code";
        if (p.Contains("codex")) return "codex";
        if (p.Contains("deepseek") || p.Contains("dsh")) return null;
        return null;
    }
}

/// <summary>Claude / Codex 会话，以及可选的额度条。</summary>
public sealed class AgentSessionProvider : IActivityProvider
{
    private readonly Dictionary<string, IslandActivity> _items = new(StringComparer.Ordinal);

    public string Id => "agents";
    public string DisplayName => "AI 编程助手";
    public bool Enabled { get; set; } = true;

    public void Apply(string json)
    {
        var activity = AgentEventParser.Parse(json);
        if (activity is null) return;
        _items[activity.Id] = activity;
    }

    public void ApplyQuota(IEnumerable<IslandActivity> quota)
    {
        foreach (var item in quota) _items[item.Id] = item;
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled ? ActivityPriority.Rank(_items.Values) : Array.Empty<IslandActivity>();
}
