using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>
/// 没有 hook 的桌面 Agent（豆包、Trae、WorkBuddy）：它们完成任务、等你确认、出错时会弹 Windows 通知，
/// 岛把这条通知接过来，按文字判断状态（复用 DSH 的关键词），显示成对应 Agent 的一条活动。
/// 只读通知中心里已经弹出的内容，不碰这些软件本身。
/// </summary>
public static class AgentToasts
{
    private static readonly Regex TraeWord = new(@"(^|[^a-z])trae([^a-z]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ChatApp? Detect(string? appName, string? appUserModelId)
    {
        var text = (appName ?? "") + " " + (appUserModelId ?? "");
        if (text.Contains("豆包", StringComparison.Ordinal) || text.Contains("Doubao", StringComparison.OrdinalIgnoreCase)) return ChatApp.Doubao;
        if (text.Contains("WorkBuddy", StringComparison.OrdinalIgnoreCase)) return ChatApp.WorkBuddy;
        if (TraeWord.IsMatch(text)) return ChatApp.Trae;
        return null;
    }

    public static string? Source(ChatApp app) => app switch
    {
        ChatApp.Doubao => "doubao",
        ChatApp.Trae => "trae",
        ChatApp.WorkBuddy => "workbuddy",
        _ => null,
    };

    public static string? Name(string source) => source switch
    {
        "doubao" => "豆包",
        "trae" => "Trae",
        "workbuddy" => "WorkBuddy",
        _ => null,
    };

    public static IslandActivity? FromToast(ChatApp app, string? title, string? body)
    {
        if (Source(app) is not { } source) return null;
        var name = Name(source)!;
        var headline = (title ?? "").Trim();
        var preview = (body ?? "").Trim();
        if (headline.Length == 0 && preview.Length == 0) return null;
        var kind = DeepSeekHarnessSignals.ClassifyText(headline + " " + preview);
        if (kind == ActivityKind.Info) kind = ActivityKind.Done; // Agent 发通知，多半是一轮跑完了在叫你
        var word = kind switch
        {
            ActivityKind.WaitingApproval => "等你确认",
            ActivityKind.Error => "出错了",
            ActivityKind.Running => "进行中",
            _ => "完成了",
        };
        var shownTitle = headline.Length == 0 || headline.Equals(name, StringComparison.OrdinalIgnoreCase) ? $"{name} {word}" : headline;
        var shownBody = preview.Length == 0 ? word : preview;
        var prio = kind switch { ActivityKind.WaitingApproval => 101, ActivityKind.Error => 91, ActivityKind.Running => 51, _ => 21 };
        return new IslandActivity(
            Id: $"agent:{source}:toast",
            Source: source,
            Title: shownTitle,
            Body: shownBody,
            Kind: kind,
            Priority: prio,
            Icon: "\uE943",
            DurationSeconds: kind == ActivityKind.WaitingApproval ? 600 : 20,
            ResetTtl: true);
    }
}
