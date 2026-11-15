namespace Qisland.Core;

/// <summary>
/// DeepSeek Harness 桌面版（Electron）的状态推断。
/// 只看进程是否在、主窗口标题、以及系统 Toast 文案。不打开 LevelDB / IndexedDB。
/// </summary>
public static class DeepSeekHarnessSignals
{
    public const string AppName = "DeepSeek Harness";

    public static bool IsHarnessProcess(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var compact = processName.Replace(" ", "", StringComparison.Ordinal);
        if (compact.Contains("DeepSeekHarness", StringComparison.OrdinalIgnoreCase)) return true;
        return processName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)
               && processName.Contains("Harness", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHarnessToast(string? appName, string? appUserModelId)
    {
        var text = (appName ?? "") + " " + (appUserModelId ?? "");
        if (text.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("dsh-desktop", StringComparison.OrdinalIgnoreCase)) return true;
        var compact = text.Replace(" ", "", StringComparison.Ordinal);
        return compact.Contains("DeepSeekHarness", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>进程没开就没有活动。标题里的关键词决定等待确认 / 出错 / 完成 / 进行中。</summary>
    public static IslandActivity? FromWindow(bool processRunning, string? windowTitle, bool privacy = false)
    {
        if (!processRunning) return null;
        var title = (windowTitle ?? "").Trim();
        var kind = ClassifyText(title);
        if (kind == ActivityKind.Info && IsBareTitle(title)) kind = ActivityKind.Running;
        var body = privacy || string.IsNullOrEmpty(title) || IsBareTitle(title)
            ? StatusLabel(kind)
            : title;
        return new IslandActivity(
            Id: "agent:deepseek-harness:window",
            Source: "deepseek-harness",
            Title: DefaultTitle(kind),
            Body: body,
            Kind: kind,
            Priority: PriorityFor(kind),
            Icon: "\uE943",
            DurationSeconds: kind is ActivityKind.Done or ActivityKind.Error ? 12 : 4 * 60 * 60,
            ResetTtl: kind is ActivityKind.Done or ActivityKind.Error);
    }

    public static IslandActivity? FromToast(string? title, string? body, bool privacy = false)
    {
        var headline = (title ?? "").Trim();
        var preview = (body ?? "").Trim();
        if (headline.Length == 0 && preview.Length == 0) return null;
        var kind = ClassifyText(headline + " " + preview);
        var shownTitle = privacy || headline.Length == 0 ? DefaultTitle(kind) : headline;
        var shownBody = privacy ? StatusLabel(kind) : (preview.Length == 0 ? StatusLabel(kind) : preview);
        return new IslandActivity(
            Id: "agent:deepseek-harness:toast",
            Source: "deepseek-harness",
            Title: shownTitle,
            Body: shownBody,
            Kind: kind,
            Priority: PriorityFor(kind) + 1,
            Icon: "\uE7BA",
            DurationSeconds: kind == ActivityKind.WaitingApproval ? 600 : 20,
            ResetTtl: true);
    }

    public static ActivityKind ClassifyText(string? text)
    {
        var value = text ?? "";
        if (ContainsAny(value, "等待确认", "等待批准", "需要批准", "需要确认", "待审批", "待确认", "请批准",
                "approval", "waiting for you", "needs approval", "awaiting"))
            return ActivityKind.WaitingApproval;
        if (ContainsAny(value, "出错", "失败", "异常", "error", "failed", "failure"))
            return ActivityKind.Error;
        if (ContainsAny(value, "已完成", "任务完成", "completed", "complete", "all done"))
            return ActivityKind.Done;
        if (ContainsAny(value, "进行中", "正在", "running", "working"))
            return ActivityKind.Running;
        return ActivityKind.Info;
    }

    private static bool IsBareTitle(string title)
        => title.Length == 0
           || title.Equals(AppName, StringComparison.OrdinalIgnoreCase)
           || title.Equals("DeepSeekHarness", StringComparison.OrdinalIgnoreCase);

    private static string DefaultTitle(ActivityKind kind) => kind switch
    {
        ActivityKind.WaitingApproval => "DeepSeek Harness 等待确认",
        ActivityKind.Error => "DeepSeek Harness 出错",
        ActivityKind.Done => "DeepSeek Harness 已完成",
        _ => "DeepSeek Harness",
    };

    private static string StatusLabel(ActivityKind kind) => kind switch
    {
        ActivityKind.WaitingApproval => "等待确认",
        ActivityKind.Error => "出错",
        ActivityKind.Done => "已完成",
        ActivityKind.Running => "进行中",
        _ => "有新动态",
    };

    private static int PriorityFor(ActivityKind kind) => kind switch
    {
        ActivityKind.WaitingApproval => 100,
        ActivityKind.Error => 90,
        ActivityKind.Running => 50,
        ActivityKind.Done => 20,
        _ => 35,
    };

    private static bool ContainsAny(string text, params string[] words)
    {
        foreach (var word in words)
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>窗口标题一条，Toast 一条。进程退出后窗口条消失。</summary>
public sealed class DeepSeekHarnessProvider : IActivityProvider
{
    private IslandActivity? _window;
    private IslandActivity? _toast;

    public string Id => "deepseek-harness";
    public string DisplayName => "DeepSeek Harness";
    public bool Enabled { get; set; } = true;

    public void ApplyWindow(bool processRunning, string? windowTitle, bool privacy)
    {
        _window = DeepSeekHarnessSignals.FromWindow(processRunning, windowTitle, privacy);
    }

    public void ApplyToast(string? title, string? body, bool privacy)
    {
        var toast = DeepSeekHarnessSignals.FromToast(title, body, privacy);
        if (toast is not null) _toast = toast;
    }

    public IReadOnlyList<IslandActivity> Snapshot()
    {
        if (!Enabled) return Array.Empty<IslandActivity>();
        var list = new List<IslandActivity>(2);
        if (_window is not null) list.Add(_window);
        if (_toast is not null) list.Add(_toast);
        return list;
    }
}
