namespace Qisland.Core;

/// <summary>岛上正在显示的一条活动。</summary>
public sealed class LiveActivity
{
    public required string Id { get; init; }
    public string Source { get; set; } = "push";
    /// <summary>内部动作分发键（点击跳转、复制…）；内部活动等于 Source，外部活动见 <see cref="ExternalActivity"/>。</summary>
    public string Dispatch { get => _dispatch ?? Source; set => _dispatch = value; }
    private string? _dispatch;
    /// <summary>由外部入口（本机 HTTP / 收件箱）写入；外部只能改、删 External 的条目。</summary>
    public bool External { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public ActivityKind Kind { get; set; }
    public int Priority { get; set; }
    public double? Progress { get; set; }
    public DateTime Updated { get; set; } = DateTime.Now;
    public DateTime? Expires { get; set; }
    /// <summary>由轮询型提供方维护（天气、信息站等），不靠过期时间消失。</summary>
    public bool Sticky { get; set; }
    /// <summary>点这一条时优先打开的链接（Codex 会话、作业网页…）。</summary>
    public string? OpenUri { get; set; }
    /// <summary>点击跳转的线索（AUMID / cwd:目录），见 <see cref="IslandActivity.AppId"/>。</summary>
    public string? AppId { get; set; }
    /// <summary>
    /// 安静的：只在展开后的列表里出现，不占收起的胶囊（不顶掉时间）、也不弹出来偷看。
    /// 给「几天后的截止」「登录快过期」「有新版本」这类不急、但会一直挂着的提示用。
    /// </summary>
    public bool Quiet { get; set; }
}

/// <summary>一次更新怎么并进已有的活动（ActivityStore 用；纯逻辑，可单测）。</summary>
public static class ActivityMerge
{
    /// <summary>运行中的会话没有自然终点：没明确给时长时至少挂这么久，靠后续事件刷新。</summary>
    public const int RunningFloorSeconds = 600;

    /// <summary>把 a 合进 live，返回显示内容是否变了。</summary>
    public static bool Apply(LiveActivity live, IslandActivity a, bool isNew, bool sticky, double? progress, bool quiet, DateTime now)
    {
        var body = a.Body.Length > 0 ? a.Body : a.Subtitle;
        var changed = isNew || live.Title != a.Title || live.Body != body || live.Kind != a.Kind || live.Progress != progress;
        live.Source = string.IsNullOrEmpty(a.Source) ? "push" : a.Source;
        live.Dispatch = a.Dispatch ?? live.Source;
        live.Title = a.Title;
        live.Body = body;
        live.Kind = a.Kind;
        live.Priority = a.Priority;
        live.Progress = progress;
        if (live.Quiet != quiet) { live.Quiet = quiet; changed = true; }
        // 显示的内容和点击目标一起更新：Replace 时链接按这次给的来（null 即清掉）
        if (a.Replace || a.OpenUri != null || isNew) { if (live.OpenUri != a.OpenUri) changed = true; live.OpenUri = a.OpenUri; }
        if (a.Replace || a.AppId != null || isNew) live.AppId = a.AppId;
        if (changed) live.Updated = now;
        var wasSticky = live.Sticky;
        live.Sticky = sticky;
        if (sticky) live.Expires = null;   // 转成常驻：不再带着之前的过期时间
        else if (isNew || a.ResetTtl || changed || wasSticky || live.Expires == null)
        {
            var secs = a.Kind == ActivityKind.Running && !a.ExactDuration ? Math.Max(a.DurationSeconds, RunningFloorSeconds) : Math.Max(a.DurationSeconds, 4);
            live.Expires = now.AddSeconds(secs);
        }
        return changed;
    }
}

/// <summary>日程提醒：每个日程（每一次）一个活动 id，提醒之间不会互相继承内容或链接。</summary>
public static class CalendarReminder
{
    public const string IdPrefix = "cal:remind:";
    public static string ActivityId(CalEvent e) => IdPrefix + e.Key;
}
