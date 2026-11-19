using System.IO;
using System.Text.Json;
using Qisland.Core;

namespace Qisland.App;

/// <summary>%APPDATA%\Qisland\ai.json：引擎、模型、快捷键这些开关。这里面没有任何 Key（Key 在凭据管理器）。</summary>
public sealed class AiSettings
{
    public bool TranslateOn { get; set; } = true;
    public string TranslateEngine { get; set; } = "mymemory";
    public string AskEngine { get; set; } = "deepseek";
    /// <summary>island：用自己的 Key 在岛上回答；doubao：粘贴到豆包窗口（不发送）。</summary>
    public string AskTarget { get; set; } = "doubao";
    public string TranslateHotkey { get; set; } = "Ctrl+Alt+T";
    public string AskHotkey { get; set; } = "Ctrl+Alt+K";
    public Dictionary<string, string> Models { get; set; } = [];
    public bool DoubaoQuotaOn { get; set; }
    public bool BalancesOn { get; set; } = true;

    public static string PathName => Path.Combine(App.DataDir, "ai.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AiSettings Load()
    {
        try { if (File.Exists(PathName)) return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(PathName)) ?? new(); }
        catch (Exception ex) { App.Log("ai.json 读不了，用默认设置: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try { File.WriteAllText(PathName, JsonSerializer.Serialize(this, Opts)); } catch (Exception ex) { App.Log("ai.json 保存失败: " + ex.Message); }
    }

    public string ModelFor(AiEngineInfo e) => Models.TryGetValue(e.Id, out var m) && m.Trim().Length > 0 ? m.Trim() : e.DefaultModel;

    public static string KeyName(AiEngineInfo e) => "ai/" + e.Id;

    public static bool Ready(AiEngineInfo e) => !e.NeedsKey || SecretStore.Has(KeyName(e));
}
