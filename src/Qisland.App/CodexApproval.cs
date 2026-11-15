using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Qisland.Core;

namespace Qisland.App;

/// <summary>一条等你在岛上点的 Codex 权限请求。</summary>
public sealed class PendingApproval
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Tool { get; init; }
    public required string Command { get; init; }
    public string Project { get; init; } = "";
    public DateTime At { get; init; } = DateTime.Now;
    internal TaskCompletionSource<string> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// 把 Codex PermissionRequest hook 挂起，等岛上点「允许 / 拒绝 / 回终端」。
/// 超时或岛关了都返回空决定，Codex 照常在终端里问，绝不替你默认同意。
/// </summary>
public sealed class ApprovalBroker
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(100);
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();
    private readonly Action<Action> _post;
    public event Action? Changed;

    public ApprovalBroker(Action<Action> post) => _post = post;

    public IReadOnlyList<PendingApproval> Pending => _pending.Values.OrderBy(p => p.At).ToList();

    public async Task<string> Request(string source, string body)
    {
        var p = Parse(source, body);
        _pending[p.Id] = p;
        _post(() => Changed?.Invoke());
        var winner = await Task.WhenAny(p.Done.Task, Task.Delay(Wait));
        var decision = winner == p.Done.Task ? p.Done.Task.Result : "";
        _pending.TryRemove(p.Id, out _);
        _post(() => Changed?.Invoke());
        return decision;
    }

    /// <summary>decision：allow / deny / ""（回终端里决定）。</summary>
    public void Decide(string id, string decision)
    {
        if (_pending.TryRemove(id, out var p)) p.Done.TrySetResult(decision);
        Changed?.Invoke();
    }

    public void CancelAll()
    {
        foreach (var id in _pending.Keys.ToList()) Decide(id, "");
    }

    internal static PendingApproval Parse(string source, string body)
    {
        string tool = "", cmd = "", project = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            tool = AgentEventParser.FirstString(r, "tool_name", "toolName");
            if (r.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object)
            {
                cmd = AgentEventParser.FirstString(ti, "command", "file_path", "path");
                var desc = AgentEventParser.FirstString(ti, "description");
                if (cmd.Length == 0) cmd = desc;
            }
            var cwd = AgentEventParser.FirstString(r, "cwd").TrimEnd('\\', '/');
            project = cwd.Length > 0 ? Path.GetFileName(cwd) : "";
        }
        catch (JsonException) { }
        return new PendingApproval
        {
            Id = "approve:" + Guid.NewGuid().ToString("N")[..10],
            Source = AgentEventParser.NormalizeSource(source),
            Tool = tool.Length > 0 ? tool : "工具",
            Command = cmd.Length > 0 ? cmd : "(没有附带命令)",
            Project = project,
        };
    }

    /// <summary>Codex 要的 hook 输出；空决定时什么都不输出，Codex 回到终端里问。</summary>
    public static string CodexOutput(string decision) => decision switch
    {
        "allow" => "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}",
        "deny" => "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"在 Qisland 灵动岛上被拒绝\"}}}",
        _ => "",
    };
}

/// <summary>
/// 托盘开关「在岛上批准 Codex 请求」：只改 ~/.codex/hooks.json 里指向 Qisland 的 PermissionRequest 条目，
/// 别的条目、别的 hook 一概不碰；改前备份一份。
/// </summary>
public static class CodexApproveHook
{
    public static string HooksPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "hooks.json");

    private static IEnumerable<JsonObject> OurEntries(JsonNode root)
    {
        if (root["hooks"]?["PermissionRequest"] is not JsonArray groups) yield break;
        foreach (var g in groups)
            if (g?["hooks"] is JsonArray hs)
                foreach (var h in hs)
                    if (h is JsonObject o && (o["command"]?.GetValue<string>() ?? "").Contains("Qisland.exe", StringComparison.OrdinalIgnoreCase))
                        yield return o;
    }

    /// <summary>null = 没装 Qisland 的 Codex hook，开关不可用。</summary>
    public static bool? IsOn()
    {
        try
        {
            if (!File.Exists(HooksPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(HooksPath));
            if (root == null) return null;
            var list = OurEntries(root).ToList();
            if (list.Count == 0) return null;
            return list.All(o => (o["command"]?.GetValue<string>() ?? "").Contains("--approve"));
        }
        catch { return null; }
    }

    public static bool Set(bool on)
    {
        try
        {
            var text = File.ReadAllText(HooksPath);
            var root = JsonNode.Parse(text)!;
            var list = OurEntries(root).ToList();
            if (list.Count == 0) return false;
            foreach (var o in list)
            {
                var cmd = (o["command"]?.GetValue<string>() ?? "").Replace(" --approve", "");
                o["command"] = on ? cmd + " --approve" : cmd;
                o["async"] = !on;          // 要等你点，必须同步
                o["timeout"] = on ? 120 : 5;
            }
            File.Copy(HooksPath, HooksPath + ".qisland-bak", true);
            File.WriteAllText(HooksPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return true;
        }
        catch { return false; }
    }
}
