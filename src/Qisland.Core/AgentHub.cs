namespace Qisland.Core;

/// <summary>岛上的 Agent 规则：哪些来源能在岛上点「允许 / 拒绝」。</summary>
public static class AgentPolicy
{
    /// <summary>
    /// 只有 Codex 和 DeepSeek Harness 可以在岛上审批（它们的 hook 是我们装的、并且是你逐个同意的）。
    /// Claude Code 以及其它所有来源只显示「在等你确认」，点一下回到它自己的窗口去确认。
    /// </summary>
    public static bool CanApproveOnIsland(string source) => AgentEventParser.NormalizeSource(source) is "codex" or "deepseek-harness";

    public static string Name(string source) => AgentEventParser.NormalizeSource(source) switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "deepseek-harness" => "DeepSeek",
        "gemini-cli" => "Gemini CLI",
        "cursor" => "Cursor",
        "kimi-cli" => "Kimi CLI",
        "qwen-code" => "Qwen Code",
        "opencode" => "OpenCode",
        "doubao" => "豆包",
        "trae" => "Trae",
        "workbuddy" => "WorkBuddy",
        var s => s,
    };
}

public enum AgentStatus { Running, Waiting, Done, Error }

public sealed record AgentSession(string Key, string Source, string Session, AgentStatus Status, string Title, string Detail,
    DateTime Started, DateTime Updated, DateTime? Ended, int Events);

/// <summary>
/// 多会话列表：按「来源 + 会话 id」记每个 Agent 会话的状态和起止时间，给 AI 页用。
/// 完成 / 出错的会话保留 30 分钟，最多 12 个；只在内存里，不落盘。
/// </summary>
public sealed class AgentSessions
{
    private readonly Dictionary<string, AgentSession> _s = [];
    private readonly object _lock = new();
    public TimeSpan KeepFinished { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan StaleRunning { get; init; } = TimeSpan.FromHours(3);
    public int Max { get; init; } = 12;

    public void Ingest(IslandActivity a, DateTime now)
    {
        if (!a.Id.StartsWith("agent:", StringComparison.Ordinal)) return;
        var parts = a.Id.Split(':', 3);
        if (parts.Length < 3) return;
        var st = a.Kind switch
        {
            ActivityKind.WaitingApproval => AgentStatus.Waiting,
            ActivityKind.Error => AgentStatus.Error,
            ActivityKind.Done => AgentStatus.Done,
            _ => AgentStatus.Running,
        };
        lock (_lock)
        {
            if (_s.TryGetValue(a.Id, out var old))
            {
                // 完成以后又来了新动作：算新一轮，重新计时
                var restart = old.Ended != null && st is AgentStatus.Running or AgentStatus.Waiting;
                _s[a.Id] = old with
                {
                    Status = st, Title = a.Title, Detail = a.Body.Length > 0 ? a.Body : old.Detail, Updated = now,
                    Started = restart ? now : old.Started, Ended = st is AgentStatus.Done or AgentStatus.Error ? now : null, Events = old.Events + 1,
                };
            }
            else _s[a.Id] = new AgentSession(a.Id, parts[1], parts[2], st, a.Title, a.Body, now, now, st is AgentStatus.Done or AgentStatus.Error ? now : null, 1);
            Prune(now);
        }
    }

    private void Prune(DateTime now)
    {
        foreach (var k in _s.Where(kv => kv.Value.Ended is { } e && now - e > KeepFinished || kv.Value.Ended == null && now - kv.Value.Updated > StaleRunning).Select(kv => kv.Key).ToList())
            _s.Remove(k);
        while (_s.Count > Max) _s.Remove(_s.Values.OrderBy(x => x.Ended == null).ThenBy(x => x.Updated).First().Key);
    }

    /// <summary>等你确认的在最前，然后在跑的，最后是刚结束的（新的在前）。</summary>
    public IReadOnlyList<AgentSession> List(DateTime now)
    {
        lock (_lock)
        {
            Prune(now);
            return _s.Values.OrderBy(x => x.Status switch { AgentStatus.Waiting => 0, AgentStatus.Running => 1, AgentStatus.Error => 2, _ => 3 })
                .ThenByDescending(x => x.Updated).ToList();
        }
    }

    public static string Elapsed(AgentSession s, DateTime now)
    {
        var d = (s.Ended ?? now) - s.Started;
        return d.TotalSeconds < 60 ? $"{Math.Max(1, (int)d.TotalSeconds)} 秒" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} 分 {d.Seconds} 秒" : $"{(int)d.TotalHours} 小时 {d.Minutes} 分";
    }
}
