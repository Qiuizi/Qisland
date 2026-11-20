using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 活动的唯一真相源。只在 UI 线程改动；外部线程通过 Dispatcher 投递。
/// 每次变化发一次 Changed，界面据此重排，不做增量动画簿记。
/// </summary>
public sealed class ActivityStore
{
    private readonly Dictionary<string, LiveActivity> _items = new(StringComparer.Ordinal);
    public event Action? Changed;

    public void Upsert(PushDraft d, double? progress = null)
    {
        if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Title)) return;
        var kind = d.Type switch
        {
            "error" => ActivityKind.Error,
            "warning" => d.Priority == "high" ? ActivityKind.WaitingApproval : ActivityKind.Warning,
            "success" or "done" => ActivityKind.Done,
            "approval" or "waiting" => ActivityKind.WaitingApproval,
            "progress" or "running" => ActivityKind.Running,
            _ => progress is null ? ActivityKind.Info : ActivityKind.Running,
        };
        if (d.Source is "claude-code" or "codex" or "deepseek-harness" or "doubao" or "trae" or "workbuddy" && kind == ActivityKind.Info)
            kind = ActivityKind.Running;
        var prio = d.Priority switch { "high" => 90, "low" => 20, _ => 50 };
        Upsert(new IslandActivity(d.Id, d.Source, d.Title, d.Body, kind, prio, d.Icon, d.Subtitle,
            d.DurationSeconds, d.ResetTtl, IslandActivity.SafeUri(d.OpenUri), d.AppId, ExactDuration: d.DurationExplicit), progress: progress);
    }

    public void Upsert(IslandActivity a, bool sticky = false, double? progress = null, bool quiet = false)
    {
        var isNew = !_items.TryGetValue(a.Id, out var live);
        live ??= new LiveActivity { Id = a.Id };
        var changed = ActivityMerge.Apply(live, a, isNew, sticky, progress, quiet, DateTime.Now);
        _items[a.Id] = live;
        if (changed) Changed?.Invoke();
    }

    /// <summary>用一个来源的最新快照替换它之前的所有条目。</summary>
    public void ReplaceSource(string source, IEnumerable<IslandActivity> snapshot, bool sticky = true, bool quiet = false)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var before = _items.Count;
        var dirty = false;
        foreach (var a in snapshot)
        {
            keep.Add(a.Id);
            var had = _items.TryGetValue(a.Id, out var old);
            var sig = had ? old!.Title + "\u0001" + old.Body + "\u0001" + old.Kind : null;
            UpsertQuiet(a with { Replace = true }, sticky, quiet);   // 快照就是完整状态：没给链接就是没有链接
            var now = _items[a.Id];
            if (!had || sig != now.Title + "\u0001" + now.Body + "\u0001" + now.Kind) dirty = true;
        }
        foreach (var id in _items.Values.Where(x => x.Source == source && !keep.Contains(x.Id)).Select(x => x.Id).ToList())
        {
            _items.Remove(id);
            dirty = true;
        }
        if (dirty || before != _items.Count) Changed?.Invoke();
    }

    private void UpsertQuiet(IslandActivity a, bool sticky, bool quiet)
    {
        var h = Changed;
        Changed = null;
        try { Upsert(a, sticky, quiet: quiet); } finally { Changed = h; }
    }

    public bool Remove(string id)
    {
        if (!_items.Remove(id)) return false;
        Changed?.Invoke();
        return true;
    }

    public void Expire()
    {
        var now = DateTime.Now;
        var dead = _items.Values.Where(x => x.Expires is { } e && e <= now).Select(x => x.Id).ToList();
        if (dead.Count == 0) return;
        foreach (var id in dead) _items.Remove(id);
        Changed?.Invoke();
    }

    /// <summary>排序：等待确认 &gt; 出错 &gt; 优先级 &gt; 最近更新。环境信息（天气）不进主队列。</summary>
    public IReadOnlyList<LiveActivity> Ranked() => _items.Values
        .Where(x => x.Source != "weather")
        .OrderByDescending(x => x.Kind == ActivityKind.WaitingApproval)
        .ThenByDescending(x => x.Kind == ActivityKind.Error)
        .ThenByDescending(x => x.Priority)
        .ThenByDescending(x => x.Updated)
        .ToList();

    /// <summary>能上收起胶囊的活动（去掉安静的）：没有它们时，收起的岛显示时间。</summary>
    /// <summary>专注中（见 <see cref="FocusMode"/>）只放行关键的几类，其余也只进列表。</summary>
    public IReadOnlyList<LiveActivity> Pill()
    {
        var focus = FocusMode.Active;
        return Ranked().Where(x => !x.Quiet && (!focus || FocusMode.Passes(x))).ToList();
    }

    public LiveActivity? Get(string id) => _items.TryGetValue(id, out var x) ? x : null;
}
