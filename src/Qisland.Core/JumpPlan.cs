namespace Qisland.Core;

/// <summary>点岛上一条（或收起的胶囊）时，按什么顺序去找「它来自的那个软件」。</summary>
public enum JumpStep
{
    /// <summary>条目自带的直达链接（会话、作业网页、会议链接）。</summary>
    Uri,
    /// <summary>微信：已有窗口 → weixin:// → 快捷键。</summary>
    WeChat,
    /// <summary>按来源认识的软件：切到已有窗口，没开就启动。</summary>
    App,
    /// <summary>按系统通知 / 媒体会话的 AppUserModelId 找窗口，找不到就让系统激活这个应用。</summary>
    Aumid,
    /// <summary>命令行 Agent：切到跑着它的终端 / 编辑器窗口（标题里带工作目录名的优先）。</summary>
    Terminal,
    /// <summary>日历：系统日历应用。</summary>
    Calendar,
}

public static class JumpPlan
{
    /// <summary>跑在终端里的 Agent（没有自己的窗口）。</summary>
    public static bool IsCli(string source) => source is "claude-code" or "gemini-cli" or "kimi-cli" or "qwen-code" or "opencode" or "codex-cli" or "cursor-agent";

    /// <summary>托管命令行的程序（进程名）：终端和常见编辑器。</summary>
    public static readonly string[] TerminalHosts =
        ["WindowsTerminal", "Code", "Code - Insiders", "Cursor", "Trae", "Trae CN", "windsurf", "idea64", "pycharm64", "wezterm-gui", "alacritty", "Tabby", "Hyper", "mintty", "WarpTerminal", "powershell", "pwsh", "cmd", "OpenConsole", "conhost"];

    /// <summary>
    /// 跳转顺序：直达链接 → 微信专用 → 认识的软件 → AUMID → 终端；同一条按顺序试，前一步成功就停。
    /// knownApp：这个来源有登记的软件（Launcher 认识）。
    /// </summary>
    public static IReadOnlyList<JumpStep> For(string source, string? openUri, string? appId, bool knownApp)
    {
        var steps = new List<JumpStep>();
        if (!string.IsNullOrEmpty(openUri)) steps.Add(JumpStep.Uri);
        if (source == "wechat") { steps.Add(JumpStep.WeChat); return steps; }
        var cwd = appId != null && appId.StartsWith("cwd:", StringComparison.Ordinal);
        if (IsCli(source) || cwd) steps.Add(JumpStep.Terminal);
        if (knownApp) steps.Add(JumpStep.App);
        if (!string.IsNullOrEmpty(appId) && !cwd) steps.Add(JumpStep.Aumid);
        if (source is "calendar" or "agenda" && string.IsNullOrEmpty(openUri)) steps.Add(JumpStep.Calendar);
        return steps;
    }

    /// <summary>工作目录的最后一段（用来在终端 / 编辑器窗口标题里认它）。</summary>
    public static string Folder(string? appId)
    {
        if (appId is null || !appId.StartsWith("cwd:", StringComparison.Ordinal)) return "";
        var p = appId[4..].Trim().TrimEnd('\\', '/');
        var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
        return i >= 0 ? p[(i + 1)..] : p;
    }

    /// <summary>
    /// 在若干候选窗口（标题、进程名，按 z 序从上到下）里挑终端：标题里带目录名的优先，其次最上面的终端；
    /// 编辑器（Code、Cursor…）只有标题带目录名才算，免得切到不相干的项目。返回下标，没有合适的返回 -1。
    /// </summary>
    public static int PickTerminal(IReadOnlyList<(string title, string process)> windows, string folder)
    {
        static bool Editor(string p) => p is "Code" or "Code - Insiders" or "Cursor" or "Trae" or "Trae CN" or "windsurf" or "idea64" or "pycharm64";
        if (folder.Length > 0)
            for (var i = 0; i < windows.Count; i++)
                if (windows[i].title.Contains(folder, StringComparison.OrdinalIgnoreCase)) return i;
        for (var i = 0; i < windows.Count; i++)
            if (!Editor(windows[i].process)) return i;
        return windows.Count > 0 && folder.Length == 0 ? 0 : -1;
    }
}
