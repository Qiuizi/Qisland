namespace Qisland.Core;

public enum ChatApp
{
    WeChat,
    QQ,
    DeepSeekHarness,
    Doubao,
    Trae,
    WorkBuddy,
}

public sealed record NotificationPolicy(bool Enabled, bool WeChat, bool QQ, bool Privacy);

public sealed record NotificationDecision(bool Show, string Title, string Body);

/// <summary>微信 / QQ / DeepSeek Harness 的 Toast 识别与隐私处理。匹配规则可单测，不碰 WinRT。</summary>
public static class NotificationMirror
{
    public static ChatApp? Detect(string? appName, string? appUserModelId)
    {
        if (DeepSeekHarnessSignals.IsHarnessToast(appName, appUserModelId)) return ChatApp.DeepSeekHarness;
        if (AgentToasts.Detect(appName, appUserModelId) is { } agent) return agent;
        var text = (appName ?? "") + " " + (appUserModelId ?? "");
        if (text.Contains("WeChat", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Weixin", StringComparison.OrdinalIgnoreCase)
            || text.Contains("微信", StringComparison.Ordinal))
            return ChatApp.WeChat;
        if (text.Contains("QQ", StringComparison.OrdinalIgnoreCase)) return ChatApp.QQ;
        return null;
    }

    public static NotificationDecision Present(ChatApp app, string? title, string? body, NotificationPolicy policy)
    {
        if (!policy.Enabled) return new NotificationDecision(false, "", "");
        if (app == ChatApp.WeChat && !policy.WeChat) return new NotificationDecision(false, "", "");
        if (app == ChatApp.QQ && !policy.QQ) return new NotificationDecision(false, "", "");
        var fallback = app switch
        {
            ChatApp.WeChat => "微信",
            ChatApp.QQ => "QQ",
            _ => "DeepSeek Harness",
        };
        if (policy.Privacy)
            return new NotificationDecision(true, fallback, "新消息");
        var sender = string.IsNullOrWhiteSpace(title) ? fallback : title.Trim();
        var preview = string.IsNullOrWhiteSpace(body) ? "新消息" : body.Trim();
        return new NotificationDecision(true, sender, preview);
    }
}

/// <summary>最近一条镜像通知。真正的监听在 WPF 宿主里，这里只保留开关和快照。</summary>
public sealed class NotificationProvider : IActivityProvider
{
    private IslandActivity? _latest;
    public string Id => "notifications";
    public string DisplayName => "微信 / QQ";
    public bool Enabled { get; set; } = true;
    public bool WeChat { get; set; } = true;
    public bool QQ { get; set; } = true;
    public bool Privacy { get; set; }

    public bool Accept(string? appName, string? appUserModelId, string? title, string? body)
    {
        var app = NotificationMirror.Detect(appName, appUserModelId);
        if (app is null or ChatApp.DeepSeekHarness) return false;
        var decision = NotificationMirror.Present(app.Value, title, body, new NotificationPolicy(Enabled, WeChat, QQ, Privacy));
        if (!decision.Show) return false;
        var source = app == ChatApp.WeChat ? "wechat" : "qq";
        _latest = new IslandActivity(
            Id: source + ":" + decision.Title,
            Source: source,
            Title: decision.Title,
            Body: decision.Body,
            Kind: ActivityKind.Info,
            Priority: 75,
            DurationSeconds: 8,
            ResetTtl: true);
        return true;
    }

    public IReadOnlyList<IslandActivity> Snapshot()
        => Enabled && _latest is not null ? new[] { _latest } : Array.Empty<IslandActivity>();
}
