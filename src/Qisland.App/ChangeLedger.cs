using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qisland.App;

/// <summary>
/// 外部改动账本：Qisland 往别的软件里写过的每一处（Codex 的 hooks.json、DSH 的桥接插件、你同意接入的 Gemini / Qwen / Cursor / OpenCode）都记在
/// %APPDATA%\Qisland\changes.json；托盘里一键还原。还原只删 Qisland 自己加的条目，你自己的配置原样保留，改前先备份。
/// 早先用脚本装的改动没有记录：启动时按特征（命令里带 Qisland.exe、依赖名 dsh-qisland-bridge）认出来补记。
/// </summary>
public static class ChangeLedger
{
    public sealed record Entry(string Id, string App, string Target, string What, DateTime At, bool Detected = false);

    /// <summary>测试用：账本文件放到临时目录。</summary>
    internal static string? PathOverride { get; set; }
    public static string PathName => PathOverride ?? Path.Combine(App.DataDir, "changes.json");
    private static string Home => Integrations.Home;
    public static string CodexHooks => Path.Combine(Home, ".codex", "hooks.json");
    public static string DshPackage => Path.Combine(Home, ".dsh", "profiles", "desktop", "package.json");
    private const string DshBridge = "dsh-qisland-bridge";
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static List<Entry> Load()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(PathName)) ?? [] : []; }
        catch { return []; }
    }

    private static void Save(List<Entry> list)
    {
        try
        {
            if (!Qisland.Core.AtomicFile.TryWrite(PathName, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(list, Pretty)), out var err)) App.Log("改动账本: " + err);
        }
        catch (Exception ex) { App.Log("改动账本: " + ex.Message); }
    }

    /// <summary>记一笔（同 id 覆盖成最新）。</summary>
    public static void Record(string id, string app, string target, string what, bool detected = false)
    {
        var list = Load();
        list.RemoveAll(e => e.Id == id);
        list.Add(new Entry(id, app, target, what, DateTime.Now, detected));
        Save(list);
        App.Log($"改动账本: 记下 {app} · {what}");
    }

    /// <summary>启动时对一遍：现在还在的改动补记，已经不在的划掉。</summary>
    public static IReadOnlyList<Entry> Sync()
    {
        var list = Load();
        var before = JsonSerializer.Serialize(list);
        var hooks = CodexHookEvents();
        if (hooks.Count > 0)
        {
            if (list.All(e => e.Id != "codex-hooks"))
                list.Add(new Entry("codex-hooks", "Codex", CodexHooks, $"hooks.json 里 {hooks.Count} 个事件指向 Qisland（{string.Join("、", hooks)}）", File.GetLastWriteTime(CodexHooks), true));
        }
        else list.RemoveAll(e => e.Id is "codex-hooks" or "codex-approve");
        if (DshBridgeInstalled())
        {
            if (list.All(e => e.Id != "dsh-bridge"))
                list.Add(new Entry("dsh-bridge", "DeepSeek Harness", DshPackage, "桌面版配置里装了 dsh-qisland-bridge 审批插件", File.GetLastWriteTime(DshPackage), true));
        }
        else list.RemoveAll(e => e.Id == "dsh-bridge");
        foreach (var t in Qisland.Core.HookPlans.All)
        {
            if (Integrations.Installed(t))
            {
                if (list.All(e => e.Id != t.Id))
                    list.Add(new Entry(t.Id, t.App, Integrations.FullPath(t), "检测到 Qisland 的接入", File.GetLastWriteTime(Integrations.FullPath(t)), true));
            }
            else list.RemoveAll(e => e.Id == t.Id);
        }
        if (JsonSerializer.Serialize(list) != before) Save(list);
        return list;
    }

    // ───────────── Codex ─────────────

    private static bool Ours(JsonNode? h) => h is JsonObject o && (o["command"]?.ToString() ?? "").Contains("Qisland.exe", StringComparison.OrdinalIgnoreCase);

    private static List<string> CodexHookEvents()
    {
        try
        {
            if (!File.Exists(CodexHooks) || JsonNode.Parse(File.ReadAllText(CodexHooks))?["hooks"] is not JsonObject ev) return [];
            return ev.Where(kv => kv.Value is JsonArray groups && groups.Any(g => g?["hooks"] is JsonArray hs && hs.Any(Ours))).Select(kv => kv.Key).ToList();
        }
        catch { return []; }
    }

    /// <summary>只删命令里带 Qisland.exe 的 hook；删空的组、删空的事件；其余原样写回。按最新内容计算，核对版本后原子替换。</summary>
    private static string RestoreCodex()
    {
        var removed = 0;
        var ok = Qisland.Core.ConfigFile.Update(CodexHooks, text =>
        {
            removed = 0;
            if (text == null) return null;
            var root = JsonNode.Parse(text)!;
            if (root["hooks"] is not JsonObject ev) return text;
            foreach (var name in ev.Select(kv => kv.Key).ToList())
            {
                if (ev[name] is not JsonArray groups) continue;
                foreach (var g in groups.ToList())
                {
                    if (g?["hooks"] is not JsonArray hs) continue;
                    foreach (var h in hs.Where(Ours).ToList()) { hs.Remove(h); removed++; }
                    if (hs.Count == 0) groups.Remove(g);
                }
                if (groups.Count == 0) ev.Remove(name);
            }
            return removed == 0 ? text : root.ToJsonString(Pretty);
        }, v => File.WriteAllText(CodexHooks + ".qisland-restore-bak", v.Text ?? ""), out var err);
        if (!ok) throw new IOException("hooks.json 在写入时一直被别的程序改动：" + err);
        return $"Codex：删掉了 {removed} 条 Qisland hook，其余保留（备份 hooks.json.qisland-restore-bak）";
    }

    // ───────────── DeepSeek Harness ─────────────

    private static bool DshBridgeInstalled()
    {
        try
        {
            if (!File.Exists(DshPackage)) return false;
            var root = JsonNode.Parse(File.ReadAllText(DshPackage));
            return root?["dependencies"]?[DshBridge] != null
                   || root?["dsh"]?["profile"]?["bundles"] is JsonArray b && b.Any(x => x?.ToString() == DshBridge);
        }
        catch { return false; }
    }

    /// <summary>从 package.json 的依赖和 bundles 里去掉桥接插件，别的插件不动；重启 DSH 后生效。按最新内容计算，核对版本后原子替换。</summary>
    private static string RestoreDsh()
    {
        var ok = Qisland.Core.ConfigFile.Update(DshPackage, text =>
        {
            if (text == null) return null;
            var root = JsonNode.Parse(text)!;
            (root["dependencies"] as JsonObject)?.Remove(DshBridge);
            if (root["dsh"]?["profile"]?["bundles"] is JsonArray b)
                foreach (var x in b.Where(x => x?.ToString() == DshBridge).ToList()) b.Remove(x);
            return root.ToJsonString(Pretty);
        }, v => File.WriteAllText(DshPackage + ".qisland-restore-bak", v.Text ?? ""), out var err);
        if (!ok) throw new IOException("package.json 在写入时一直被别的程序改动：" + err);
        return "DeepSeek Harness：去掉了 dsh-qisland-bridge（备份 package.json.qisland-restore-bak），重启 DSH 生效";
    }

    /// <summary>
    /// 一键还原：只撤销你在确认框里看到并同意的那些条目，按条目身份（Id + 目标文件）逐条对上现状；
    /// 确认之后才出现的新改动不碰，已经不在的跳过。返回每一项的结果。
    /// </summary>
    public static List<string> RestoreAll(IReadOnlyList<Entry> confirmed)
    {
        var results = new List<string>();
        var current = Sync().ToList();
        foreach (var e in confirmed)
        {
            var now = current.FirstOrDefault(c => c.Id == e.Id && string.Equals(c.Target, e.Target, StringComparison.OrdinalIgnoreCase));
            if (now == null) { results.Add($"{e.App}：这项改动已经不在了，跳过"); continue; }
            try
            {
                var r = e.Id switch
                {
                    "codex-hooks" when File.Exists(CodexHooks) => RestoreCodex(),
                    "dsh-bridge" when File.Exists(DshPackage) => RestoreDsh(),
                    _ when Qisland.Core.HookPlans.ById(e.Id) is { } t => Integrations.Remove(t, ask: false),
                    _ => null,
                };
                if (r != null) { results.Add(r); App.Log("改动账本还原: " + r); }
            }
            catch (Exception ex) { results.Add($"{e.App}：没还原成功（{ex.Message}）"); App.Log($"改动账本还原 {e.Id} 失败: {ex.Message}"); }
        }
        Sync();
        return results;
    }
}
