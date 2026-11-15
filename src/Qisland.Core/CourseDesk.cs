using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// CourseDesk 快照（雨课堂上课 / 随堂题，学习通签到，两边的作业截止）。
/// 桌面端目前没有本地 HTTP，快照由 docs 里的补丁写出，或由上岛 API 直接推送。
/// </summary>
public sealed class CourseDeskProvider : IActivityProvider
{
    private List<IslandActivity> _items = new();
    public string Id => "coursedesk";
    public string DisplayName => "CourseDesk";
    public bool Enabled { get; set; } = true;

    public void ApplySnapshot(string json)
    {
        _items = ReadSnapshot(json).ToList();
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled ? ActivityPriority.Rank(_items).Take(6).ToList() : Array.Empty<IslandActivity>();

    public static IReadOnlyList<IslandActivity> ReadSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<IslandActivity>();
        var list = new List<IslandActivity>();
        if (root.TryGetProperty("yuketang", out var yuketang)) ReadYuketang(yuketang, list);
        if (root.TryGetProperty("chaoxing", out var chaoxing)) ReadChaoxing(chaoxing, list);
        if (root.TryGetProperty("homework", out var homework)) ReadHomework(homework, "", list);
        return list;
    }

    private static void ReadYuketang(JsonElement node, List<IslandActivity> list)
    {
        if (node.TryGetProperty("onLesson", out var lessons) && lessons.ValueKind == JsonValueKind.Array)
        {
            foreach (var lesson in lessons.EnumerateArray())
            {
                var id = Text(lesson, "lessonId");
                var course = Text(lesson, "courseName");
                if (id.Length == 0 && course.Length == 0) continue;
                list.Add(Make("course:lesson:" + (id.Length == 0 ? course : id),
                    string.IsNullOrEmpty(course) ? "正在上课" : "正在上课 · " + course,
                    Text(lesson, "classroomName"), 86, ActivityKind.Running));
            }
        }
        if (node.TryGetProperty("lesson", out var session) && session.ValueKind == JsonValueKind.Object
            && session.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array)
        {
            var course = Text(session, "courseName");
            foreach (var problem in problems.EnumerateArray())
            {
                var state = Text(problem, "submitState");
                if (state is "submitted" or "skipped") continue;
                var id = Text(problem, "problemId");
                if (id.Length == 0) continue;
                var body = Text(problem, "body");
                list.Add(Make("course:quiz:" + id,
                    string.IsNullOrEmpty(course) ? "随堂题" : "随堂题 · " + course,
                    body, 88, ActivityKind.WaitingApproval));
            }
        }
        if (node.TryGetProperty("homework", out var homework)) ReadHomework(homework, "雨课堂", list);
    }

    private static void ReadChaoxing(JsonElement node, List<IslandActivity> list)
    {
        if (node.TryGetProperty("actives", out var actives) && actives.ValueKind == JsonValueKind.Array)
        {
            foreach (var active in actives.EnumerateArray())
            {
                if (!Bool(active, "ongoing")) continue;
                var id = Text(active, "id");
                var title = Text(active, "title");
                var course = Text(active, "courseName");
                list.Add(Make("course:sign:" + (id.Length == 0 ? title : id),
                    "签到 · " + (title.Length == 0 ? course : title),
                    course, 92, ActivityKind.WaitingApproval));
            }
        }
        if (node.TryGetProperty("homework", out var homework)) ReadHomework(homework, "学习通", list);
    }

    private static void ReadHomework(JsonElement array, string platform, List<IslandActivity> list)
    {
        if (array.ValueKind != JsonValueKind.Array) return;
        foreach (var item in array.EnumerateArray())
        {
            var hours = Number(item, "leftHours");
            if (hours is null or > 72) continue;
            var title = Text(item, "title");
            if (title.Length == 0) continue;
            var id = Text(item, "id");
            var course = Text(item, "courseName");
            var left = hours <= 24 ? $"{hours:0.#} 小时内" : $"{hours:0.#} 小时";
            var priority = hours <= 24 ? 80 : 58;
            list.Add(Make("course:hw:" + (id.Length == 0 ? title : id),
                "作业 · " + title,
                Join(platform, course, Text(item, "leftText").Length == 0 ? left : Text(item, "leftText")),
                priority,
                hours <= 24 ? ActivityKind.Warning : ActivityKind.Info));
        }
    }

    private static IslandActivity Make(string id, string title, string body, int priority, ActivityKind kind)
        => new(id, "coursedesk", title, body, kind, priority, "\uE7BE", DurationSeconds: 240, ResetTtl: true);

    private static string Join(params string[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Trim() : "";
    }

    private static bool Bool(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.GetDouble();
    }
}

/// <summary>JARVIS（Course Workspace）以后直接推这个结构。提供方只做校验和映射。</summary>
public static class JarvisContract
{
    public static IslandActivity? Parse(JsonElement root)
    {
        var title = Text(root, "title");
        if (title.Length == 0) return null;
        var status = Text(root, "status").ToLowerInvariant();
        var kind = status switch
        {
            "waiting_approval" or "approval" => ActivityKind.WaitingApproval,
            "error" or "failed" => ActivityKind.Error,
            "done" or "completed" => ActivityKind.Done,
            "running" => ActivityKind.Running,
            _ => ActivityKind.Info,
        };
        var id = Text(root, "id");
        if (id.Length == 0) id = "jarvis:" + title;
        else if (!id.StartsWith("jarvis:", StringComparison.Ordinal)) id = "jarvis:" + id;
        var priority = kind switch
        {
            ActivityKind.WaitingApproval => 90,
            ActivityKind.Error => 80,
            ActivityKind.Running => 50,
            ActivityKind.Done => 18,
            _ => 40,
        };
        return new IslandActivity(id, "jarvis", title, Text(root, "body"), kind, priority,
            DurationSeconds: kind is ActivityKind.Done or ActivityKind.Error ? 12 : 180,
            ResetTtl: kind is ActivityKind.Done or ActivityKind.Error);
    }

    private static string Text(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return "";
        return (value.GetString() ?? "").Trim();
    }
}
