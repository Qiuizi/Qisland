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
    bool ResetTtl = false,
    string? OpenUri = null,
    string? AppId = null,
    bool Replace = false,
    bool ExactDuration = false,
    string? Dispatch = null)
{
    // Dispatch：内部动作分发用的键（点击跳转、复制、打开下载…）。内部提供方不填，等于 Source；
    //   外部推送的 Source 只是显示用的来源标签，Dispatch 由 ActivityStore 在外部入口按 ExternalActivity 规则决定。
    // Replace：这次更新是这一条的完整状态——OpenUri / AppId 为 null 就是「清掉」，不沿用上一条的链接。
    //   快照型来源（日程、课表…）和复用同一 id 的提醒都该用它；Agent 会话这类逐步补全信息的事件不用。
    // ExactDuration：调用方明确给了时长（Island API 的 duration_seconds），运行中的卡片也按它过期，不再至少挂 10 分钟。

    // AppId：点这一条时用来找软件的线索——系统通知的 AppUserModelId，或「cwd:目录」（命令行 Agent 跑在哪个终端 / 编辑器里）。只在内存里，不落盘、不写日志。

    /// <summary>点这一条时优先打开的链接（http(s) / codex:// 等）；只接受白名单里的协议。</summary>
    public static string? SafeUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        uri = uri.Trim();
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return null;
        return u.Scheme is "http" or "https" or "codex" or "weixin" or "mailto" or "ms-actioncenter" ? uri : null;
    }
}

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
    /// <summary>调用方明确给了 duration_seconds（见 <see cref="IslandActivity.ExactDuration"/>）。</summary>
    public bool DurationExplicit { get; set; }
    public string Source { get; set; } = "";
    /// <summary>内部动作分发键；null 表示按 Source（见 <see cref="IslandActivity.Dispatch"/>）。</summary>
    public string? Dispatch { get; set; }
    public string? OpenUri { get; set; }
    public string? AppId { get; set; }
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
            Dispatch = activity.Dispatch,
            OpenUri = activity.OpenUri,
            AppId = activity.AppId,
        };
    }

    public static string IconFor(IslandActivity activity) => activity.Source switch
    {
        "claude-code" or "codex" or "deepseek-harness" or "doubao" or "trae" or "workbuddy" or "agents" => "\uE943",
        "info-station" => "\uE8A5",
        "coursedesk" => "\uE7BE",
        "jarvis" => "\uE8F1",
        "weather" or "weather-alert" => "\uE706",
        "timer" => "\uE823",
        "todo" => "\uE73E",
        "media" => "\uE8D6",
        _ => activity.Kind == ActivityKind.WaitingApproval ? "\uE7BA" : "\uE7F4",
    };
}

/// <summary>
/// 外部活动（本机 HTTP、收件箱）的命名空间和动作分发规则。
/// - 调用方自己起名的卡片（普通推送）统一放进 <c>ext:</c> 命名空间，永远撞不上内部 id（update、todo:remind:…、download:done:…）；
/// - 认得出的接入协议（Agent 会话、JARVIS、信息站、课程快照、消息镜像）由解析器生成自己的前缀，写入时由 ActivityStore 校验归属；
/// - 来源标签（Source）只用于显示；内部动作按 Dispatch 分发，普通推送一律是 <c>push</c>。
/// </summary>
public static class ExternalActivity
{
    public const string IdPrefix = "ext:";
    public const string PushDispatch = "push";

    /// <summary>接入协议的来源：解析器从固定词表里给出，保留各自的跳转（终端、codex:// 链接、微信…）。</summary>
    private static readonly HashSet<string> ProtocolSources = new(StringComparer.Ordinal)
    {
        "claude-code", "codex", "deepseek-harness", "doubao", "trae", "workbuddy",
        "jarvis", "coursedesk", "info-station", "wechat", "qq",
    };

    public static string Namespace(string id) =>
        id.StartsWith(IdPrefix, StringComparison.Ordinal) ? id : IdPrefix + id;

    public static string DispatchFor(string? dispatch, string source) =>
        dispatch ?? (ProtocolSources.Contains(source) ? source : PushDispatch);
}
