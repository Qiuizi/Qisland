using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qisland.Core;

public enum HookStyle
{
    /// <summary>Gemini CLI / Qwen Code：settings.json 里 hooks.{事件}[{matcher, hooks:[{type,command}]}]。</summary>
    Nested,
    /// <summary>Cursor：hooks.json，{version:1, hooks:{事件:[{command}]}}。</summary>
    Cursor,
    /// <summary>OpenCode：在插件目录里放一个独立的 qisland.js。</summary>
    PluginFile,
}

/// <summary>一个可以接到岛上的第三方 Agent。路径相对用户目录。</summary>
public sealed record HookTarget(string Id, string Source, string App, string RelativePath, HookStyle Style, string[] Events, bool Experimental, string Note);

/// <summary>
/// 往别的 Agent 里接 hook 的纯 JSON 变换（装 / 卸 / 检查），App 层负责弹窗征得同意、备份、记账本。
/// 规则：只加 / 只删命令里带 Qisland.exe 的条目，别的配置一个字不动；所有接入都只上报状态，从不替你批准任何操作。
/// </summary>
public static class HookPlans
{
    public static readonly IReadOnlyList<HookTarget> All =
    [
        new("gemini-hooks", "gemini-cli", "Gemini CLI", ".gemini/settings.json", HookStyle.Nested,
            ["BeforeAgent", "AfterTool", "Notification", "AfterAgent", "SessionEnd"], false,
            "只上报进度；需要你确认的操作仍在 Gemini 终端里确认"),
        new("qwen-hooks", "qwen-code", "Qwen Code", ".qwen/settings.json", HookStyle.Nested,
            ["UserPromptSubmit", "PostToolUse", "Notification", "Stop", "SessionEnd"], true,
            "Qwen Code 的 hooks 还是实验功能，版本不同可能不生效"),
        new("cursor-hooks", "cursor", "Cursor", ".cursor/hooks.json", HookStyle.Cursor,
            ["beforeSubmitPrompt", "afterFileEdit", "afterShellExecution", "stop"], false,
            "只用不拦截的事件；不接管 Cursor 的命令确认"),
        new("opencode-plugin", "opencode", "OpenCode", ".config/opencode/plugins/qisland.js", HookStyle.PluginFile,
            ["session.idle", "session.error", "permission.asked", "tool.execute.before"], true,
            "新建一个插件文件，只发状态到 127.0.0.1；删掉这个文件就卸载"),
    ];

    public static HookTarget? ById(string id) => All.FirstOrDefault(t => t.Id == id);

    public static string Command(string exe, string source) => $"\"{exe}\" hook {source}";

    public static bool IsOurs(JsonNode? h) => h is JsonObject o && (o["command"]?.ToString() ?? "").Contains("Qisland.exe", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject ParseRoot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonNode.Parse(json, documentOptions: Lenient) as JsonObject ?? throw new FormatException("配置文件最外层不是 JSON 对象，不敢改");
    }

    /// <summary>已经装上的事件（命令里带 Qisland.exe）。</summary>
    public static IReadOnlyList<string> InstalledEvents(HookTarget t, string? json)
    {
        if (t.Style == HookStyle.PluginFile) return json != null && json.Contains(PluginMarker) ? t.Events : [];
        try
        {
            if (ParseRoot(json)["hooks"] is not JsonObject ev) return [];
            return ev.Where(kv => kv.Value is JsonArray arr && (t.Style == HookStyle.Cursor
                    ? arr.Any(IsOurs)
                    : arr.Any(g => g?["hooks"] is JsonArray hs && hs.Any(IsOurs))))
                .Select(kv => kv.Key).ToList();
        }
        catch { return []; }
    }

    /// <summary>加上我们的 hook（已有的先去掉再加，等于更新路径）；返回新的文件内容。</summary>
    public static string Apply(HookTarget t, string? json, string exe)
    {
        if (t.Style == HookStyle.PluginFile) return OpenCodePlugin(9840);
        var root = ParseRoot(Remove(t, json, out _));
        if (t.Style == HookStyle.Cursor && root["version"] == null) root["version"] = 1;
        if (root["hooks"] is not JsonObject ev) { ev = []; root["hooks"] = ev; }
        var cmd = Command(exe, t.Source);
        foreach (var e in t.Events)
        {
            if (ev[e] is not JsonArray arr) { arr = []; ev[e] = arr; }
            if (t.Style == HookStyle.Cursor) arr.Add(new JsonObject { ["command"] = cmd });
            else arr.Add(new JsonObject
            {
                ["matcher"] = "*",
                ["hooks"] = new JsonArray(new JsonObject { ["name"] = "qisland", ["type"] = "command", ["command"] = cmd, ["timeout"] = 5000 }),
            });
        }
        return root.ToJsonString(Pretty);
    }

    // ── 条目身份：一键还原 / 移除前把「你确认时看到的每一条」冻结下来，之后只删这些；新加或改过的条目身份不同，保留 ──

    private static readonly JsonSerializerOptions Canon = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>一条 hook 的身份：事件名 + 所在组的 matcher + 这条 hook 对象的紧凑 JSON（任何一处变了身份就不同）。</summary>
    public static string HookIdentity(string evt, string matcher, JsonNode hook) =>
        evt + "\u001f" + matcher + "\u001f" + hook.ToJsonString(Canon);

    /// <summary>插件文件的身份：内容的 SHA256。</summary>
    public static string FileIdentity(string? text) =>
        "file\u001f" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    /// <summary>现在文件里我们的每一条（可能重复，按出现次数记）。</summary>
    public static List<string> Identities(HookTarget t, string? json)
    {
        if (t.Style == HookStyle.PluginFile) return json != null && json.Contains(PluginMarker) ? [FileIdentity(json)] : [];
        return Identities(t.Style, json);
    }

    /// <summary>Nested（Gemini / Qwen / Codex：事件 → 组 → hooks）或 Cursor（事件 → hooks）风格里我们的每一条。</summary>
    public static List<string> Identities(HookStyle style, string? json)
    {
        var ids = new List<string>();
        try
        {
            if (ParseRoot(json)["hooks"] is not JsonObject ev) return ids;
            foreach (var (name, v) in ev)
            {
                if (v is not JsonArray arr) continue;
                if (style == HookStyle.Cursor) ids.AddRange(arr.Where(IsOurs).Select(h => HookIdentity(name, "", h!)));
                else foreach (var g in arr)
                    if (g?["hooks"] is JsonArray hs)
                    {
                        var matcher = g["matcher"]?.ToString() ?? "";
                        ids.AddRange(hs.Where(IsOurs).Select(h => HookIdentity(name, matcher, h!)));
                    }
            }
        }
        catch { }
        return ids;
    }

    /// <summary>冻结的身份按次数用掉：同一身份冻结了几条就最多删几条，确认后又加的同样一条也保留。</summary>
    public sealed class IdentityBudget(IEnumerable<string> frozen)
    {
        private readonly Dictionary<string, int> _left = frozen.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        public bool Take(string id)
        {
            if (!_left.TryGetValue(id, out var n) || n <= 0) return false;
            _left[id] = n - 1;
            return true;
        }
    }

    /// <summary>只删我们的条目，删空的组和事件；别的原样。</summary>
    public static string Remove(HookTarget t, string? json, out int removed) => Remove(t, json, null, out removed) ?? "";   // only 为 null 时不会返回 null

    /// <summary>
    /// only 不为 null 时只删身份在 only 里的条目（按次数），确认之后新加或改过的我们的条目也保留。
    /// PluginFile 返回 null 表示删文件（only 不为 null 时内容的身份必须对得上），返回原文表示不动。
    /// </summary>
    public static string? Remove(HookTarget t, string? json, IReadOnlyCollection<string>? only, out int removed)
    {
        removed = 0;
        if (t.Style == HookStyle.PluginFile)
        {
            if (json?.Contains(PluginMarker) != true) return json;
            if (only != null && !only.Contains(FileIdentity(json))) return json;
            removed = 1;
            return only == null ? "" : null;
        }
        return Remove(t.Style, json, only, out removed);
    }

    public static string Remove(HookStyle style, string? json, IReadOnlyCollection<string>? only, out int removed)
    {
        removed = 0;
        var budget = only == null ? null : new IdentityBudget(only);
        bool Drop(string evt, string matcher, JsonNode? h) => IsOurs(h) && (budget == null || budget.Take(HookIdentity(evt, matcher, h!)));
        var root = ParseRoot(json);
        if (root["hooks"] is not JsonObject ev) return root.ToJsonString(Pretty);
        foreach (var name in ev.Select(kv => kv.Key).ToList())
        {
            if (ev[name] is not JsonArray arr) continue;
            if (style == HookStyle.Cursor)
                foreach (var h in arr.Where(h => Drop(name, "", h)).ToList()) { arr.Remove(h); removed++; }
            else
                foreach (var g in arr.ToList())
                {
                    if (g?["hooks"] is not JsonArray hs) continue;
                    var matcher = g["matcher"]?.ToString() ?? "";
                    foreach (var h in hs.Where(h => Drop(name, matcher, h)).ToList()) { hs.Remove(h); removed++; }
                    if (hs.Count == 0) arr.Remove(g);
                }
            if (arr.Count == 0) ev.Remove(name);
        }
        if (ev.Count == 0) root.Remove("hooks");
        return root.ToJsonString(Pretty);
    }

    /// <summary>给确认弹窗用的一句话：改哪个文件、加什么。</summary>
    public static string Describe(HookTarget t, string fullPath, string exe) => t.Style switch
    {
        HookStyle.PluginFile => $"新建文件：{fullPath}\n内容：一个 OpenCode 插件，会话空闲 / 出错 / 等你确认 / 调用工具时，把来源、会话 id、事件名和项目文件夹名发到 http://127.0.0.1:9840（不发代码、不发对话）；设了推送 Token 时按 QISLAND_TOKEN → api-token.txt 现读并带上，插件文件里不保存 Token。",
        _ => $"修改文件：{fullPath}\n改动：在 \"hooks\" 里给 {string.Join("、", t.Events)} 这 {t.Events.Length} 个事件各加一条命令：\n{Command(exe, t.Source)}\n别的配置不动；改前备份为同目录下的 .qisland-bak-时间 文件。",
    };

    public const string PluginMarker = "Qisland island bridge";

    public static string OpenCodePlugin(int port) => $$"""
        // {{PluginMarker}} — 由 Qisland 在你同意后写入；删掉本文件即可卸载。
        // 只把会话状态（来源、会话 id、事件名、项目文件夹名）发给本机 127.0.0.1:{{port}}，不发代码和对话内容。
        // Token 和其他内置接入同一规则：{{ApiTokenRule.EnvToken}} → {{ApiTokenRule.EnvFile}} → %APPDATA%\{{ApiTokenRule.DataFolder}}\{{ApiTokenRule.FileName}}；插件文件里不保存 Token。
        import { readFileSync } from "node:fs";
        import { join } from "node:path";
        const URL = "http://127.0.0.1:{{port}}/v1/agent/opencode";
        {{ApiTokenRule.JsLookup}}
        const warned = new Set();
        const warn = (status) => {
          if (warned.has(status)) return;
          warned.add(status);
          const why = status === 401 ? "Token 没带或不对（" + "{{ApiTokenRule.EnvToken}} / {{ApiTokenRule.FileName}}）"
            : status === 503 ? "Qisland 的 Token 文件无效或忙" : "";
          try { console.error("[Qisland] 上报被拒绝 HTTP " + status + (why ? "：" + why : "")); } catch {}
        };
        const send = (hook, sessionID, cwd) => {
          try {
            const headers = { "content-type": "application/json" };
            const token = qislandToken();
            if (token) headers["X-Qisland-Token"] = token;
            fetch(URL, { method: "POST", headers,
              body: JSON.stringify({ source: "opencode", hook_event_name: hook, session_id: sessionID || "default", cwd: cwd || "" }),
              signal: AbortSignal.timeout(800) })
              .then((res) => { if (!res.ok) warn(res.status); })
              .catch(() => {});
          } catch {}
        };
        export const QislandPlugin = async ({ directory }) => ({
          event: async ({ event }) => {
            const t = event && event.type;
            if (t === "session.idle" || t === "session.error" || t === "permission.asked" || t === "permission.updated")
              send(t, event.properties && (event.properties.sessionID || event.properties.sessionId), directory);
          },
          "tool.execute.before": async (input) => { send("tool.execute.before", input && input.sessionID, directory); },
        });
        """.Replace("\n", "\r\n");

    /// <summary>hook 中转（Qisland.exe hook x）写回 stdout 的内容：Cursor 的 beforeSubmitPrompt 要明确放行，其余空对象。从不输出「允许权限」。</summary>
    public static string RelayOutput(string source, string body)
    {
        if (AgentEventParser.NormalizeSource(source) == "cursor" && body.Contains("beforeSubmitPrompt", StringComparison.Ordinal)) return "{\"continue\":true}";
        return "{}";
    }

    /// <summary>Kimi CLI 等没有稳定 hook 的：给手动接入说明，不自动改任何文件。</summary>
    public static string ManualHowTo(string exe) =>
        $"在终端任务结束时让脚本执行：\n  echo {{\"source\":\"kimi-cli\",\"status\":\"done\",\"title\":\"Kimi 完成\"}} | \"{exe}\" hook kimi-cli\n或用 scripts\\qisland-push.ps1 推送。Qisland 不会自动修改 Kimi 的配置。";
}
