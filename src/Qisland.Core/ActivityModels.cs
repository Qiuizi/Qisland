namespace Qisland.Core;

/// <summary>岛上一条活动的状态。数值越大越靠前。</summary>
public enum ActivityKind
{
    Info = 0,
    Running = 1,
    Done = 2,
    Warning = 3,
    Error = 4,
    WaitingApproval = 5,
}

/// <summary>提供方产出的一条可上岛活动。纯数据，可在 Linux 上单测。</summary>
public sealed record IslandActivity(
    string Id,
    string Source,
    string Title,
    string Body,
    ActivityKind Kind,
    int Priority,
    string Icon = "",
    string Subtitle = "",
    int DurationSeconds = 20,
    bool ResetTtl = false);

/// <summary>每个来源一个提供方。设置页按 <see cref="Id"/> 开关。</summary>
public interface IActivityProvider
{
    string Id { get; }
    string DisplayName { get; }
    bool Enabled { get; set; }
    IReadOnlyList<IslandActivity> Snapshot();
}

/// <summary>多活动竞争时的排序：优先级高的进胶囊，其余变成圆点。</summary>
public static class ActivityPriority
{
    public static IReadOnlyList<IslandActivity> Rank(IEnumerable<IslandActivity> items)
    {
        return items
            .Where(a => !string.IsNullOrWhiteSpace(a.Id) && !string.IsNullOrWhiteSpace(a.Title))
            .GroupBy(a => a.Id, StringComparer.Ordinal)
            .Select(g => g.Last())
            .OrderByDescending(a => a.Priority)
            .ThenByDescending(a => (int)a.Kind)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>映射到 WinIslands 上岛 API 的一张卡片。</summary>
public sealed class PushDraft
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Type { get; set; } = "info";
    public string Priority { get; set; } = "normal";
    public int DurationSeconds { get; set; } = 20;
    /// <summary>为 true 时同 id 更新要重算过期时间（完成/出错这种短卡片）。</summary>
    public bool ResetTtl { get; set; }
    public string Source { get; set; } = "";
}

public static class PushMapping
{
    public static PushDraft From(IslandActivity activity)
    {
        var (type, priority) = activity.Kind switch
        {
            ActivityKind.Error => ("error", "high"),
            ActivityKind.WaitingApproval => ("warning", "high"),
            ActivityKind.Warning => ("warning", activity.Priority >= 70 ? "high" : "normal"),
            ActivityKind.Done => ("success", "low"),
            ActivityKind.Running => ("info", "normal"),
            _ => ("info", activity.Priority >= 70 ? "high" : "normal"),
        };
        if (activity.Priority >= 85) priority = "high";
        if (activity.Priority <= 15) priority = "low";
        return new PushDraft
        {
            Id = activity.Id,
            Title = activity.Title,
            Body = activity.Body,
            Subtitle = activity.Subtitle,
            Icon = string.IsNullOrEmpty(activity.Icon) ? IconFor(activity) : activity.Icon,
            Type = type,
            Priority = priority,
            DurationSeconds = Math.Max(4, activity.DurationSeconds),
            ResetTtl = activity.ResetTtl || activity.Kind is ActivityKind.Done or ActivityKind.Error,
            Source = activity.Source,
        };
    }

    public static string IconFor(IslandActivity activity) => activity.Source switch
    {
        "claude-code" or "codex" or "deepseek-harness" or "agents" => "\uE943",
        "info-station" => "\uE8A5",
        "coursedesk" => "\uE7BE",
        "jarvis" => "\uE8F1",
        "weather" => "\uE706",
        "timer" => "\uE823",
        "todo" => "\uE73E",
        "media" => "\uE8D6",
        _ => activity.Kind == ActivityKind.WaitingApproval ? "\uE7BA" : "\uE7F4",
    };
}
