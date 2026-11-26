using System.IO;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 把所有额度（Codex、豆包、本机 AI 编程工具）汇到一起：
/// · 70% / 90% 提醒：每个工具的每个窗口、每个重置周期每档只弹一次，状态存在 quota-alerts.txt，重启不重复弹；
/// · 收起胶囊上的精简额度环：取最紧的那一个。
/// </summary>
public sealed class QuotaWatch
{
    public sealed record Item(string Tool, string Label, double Used, DateTime? ResetLocal);

    private readonly ActivityStore _store;
    private readonly QuotaAlerts _alerts = new();
    private static string StateFile => Path.Combine(App.DataDir, "quota-alerts.txt");

    public CodexQuota? Codex { get; set; }
    public DoubaoQuotaService? Doubao { get; set; }
    public AiUsageService? Usage { get; set; }
    public event Action? Changed;

    public QuotaWatch(ActivityStore store)
    {
        _store = store;
        try { if (File.Exists(StateFile)) _alerts.Load(File.ReadAllText(StateFile)); } catch { }
    }

    public List<Item> All()
    {
        var list = new List<Item>();
        if (Codex is { Fresh: true } q)
        {
            if (q.Primary is { } a) list.Add(new("codex", "5 小时", a.UsedPercent, a.ResetsAt == DateTime.MinValue ? null : a.ResetsAt));
            if (q.Secondary is { } w) list.Add(new("codex", "本周", w.UsedPercent, w.ResetsAt == DateTime.MinValue ? null : w.ResetsAt));
        }
        if (Doubao is { Connected: true, Fresh: true } db)
            foreach (var w in db.Last!.Windows.Take(2)) list.Add(new("doubao", w.Label, w.UsedPercent, w.ResetsAt));
        if (Usage != null)
            foreach (var s in Usage.Fresh())
                foreach (var w in s.Windows) list.Add(new(s.Tool, w.Label, w.UsedPercent, w.ResetsAt?.ToLocalTime()));
        return list;
    }

    /// <summary>最紧的一个（收起胶囊上的小环用）。</summary>
    public Item? Tightest() => All().OrderByDescending(i => i.Used).FirstOrDefault();

    /// <summary>任何一个额度来源刷新后调用。</summary>
    public void OnQuota()
    {
        var dirty = false;
        foreach (var i in All())
        {
            var level = _alerts.Check(i.Tool, i.Label, i.Used, i.ResetLocal);
            if (level == 0) continue;
            dirty = true;
            if (!Features.On(Features.QuotaAlerts)) continue;   // 关着提醒也照样记档，打开后不会把旧的一次性补弹
            var name = AiUsage.Name(i.Tool);
            var reset = i.ResetLocal is { } r ? ResetText(r) : "";
            App.Log($"额度提醒: {name} {i.Label} 过 {level}%");
            _store.Upsert(new IslandActivity($"quota:{i.Tool}:{i.Label}", "quota", $"{name} {i.Label}已用 {i.Used:0}%",
                level >= 90 ? (reset.Length > 0 ? $"快用完了 · {reset}" : "快用完了") : (reset.Length > 0 ? reset : "注意节奏"),
                level >= 90 ? ActivityKind.Warning : ActivityKind.Info, level >= 90 ? 74 : 64, "\uE9D2", DurationSeconds: level >= 90 ? 12 : 8, ResetTtl: true),
                progress: Math.Clamp(i.Used / 100, 0, 1));
        }
        if (dirty) { try { File.WriteAllText(StateFile, _alerts.Serialize()); } catch { } }
        Changed?.Invoke();
    }

    public static string ResetText(DateTime local)
    {
        string[] week = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
        return local.Date == DateTime.Today ? $"{local:HH:mm} 重置"
            : local.Date == DateTime.Today.AddDays(1) ? $"明天 {local:HH:mm} 重置"
            : local - DateTime.Now < TimeSpan.FromDays(7) ? $"{week[(int)local.DayOfWeek]} {local:HH:mm} 重置"
            : $"{local:M月d日} 重置";
    }
}
