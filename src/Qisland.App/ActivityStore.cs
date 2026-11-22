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

    /// <summary>内部代码用 PushDraft 写一条（演示数据等）。外部入口必须走 <see cref="UpsertExternal(PushDraft, double?)"/>。</summary>
    public void Upsert(PushDraft d, double? progress = null)
    {
        if (ToActivity(d, progress, d.Dispatch) is { } a) Upsert(a, progress: progress);
    }

    /// <summary>
    /// 外部入口（本机 HTTP、收件箱）写一条：同 id 已有内部活动时拒绝（返回 false），不改内容、不改归属；
    /// 来源标签只做显示，内部动作分发键按 <see cref="ExternalActivity.DispatchFor"/> 决定。
    /// </summary>
    public bool UpsertExternal(PushDraft d, double? progress = null)
    {
        var a = ToActivity(d, progress, ExternalActivity.DispatchFor(d.Dispatch, string.IsNullOrEmpty(d.Source) ? "push" : d.Source));
        return a != null && UpsertExternal(a, progress);
    }

    /// <summary>外部 Agent 事件等已经成形的活动；归属规则同上。</summary>
    public bool UpsertExternal(IslandActivity a, double? progress = null)
    {
        if (_items.TryGetValue(a.Id, out var existing) && !existing.External) return false;
        var external = a with { Dispatch = ExternalActivity.DispatchFor(a.Dispatch, string.IsNullOrEmpty(a.Source) ? "push" : a.Source) };
        UpsertCore(external, sticky: false, progress, quiet: false, isExternal: true);
        return true;
    }

    /// <summary>外部入口删一条：只删外部写入的条目，内部活动原样保留。</summary>
    public bool RemoveExternal(string id, bool notify = true) =>
        _items.TryGetValue(id, out var live) && live.External && Remove(id, notify);

    /// <summary>
    /// 按存储里记录的外部归属撤回：候选 ID 依次试，删掉第一条确实由外部写入的，返回它的 ID；一条都没有返回 null。
    /// 普通推送存的是 ext: 前缀，Agent 事件存的是协议自己的 ID，所以调用方会同时给出原样和加前缀两种写法。
    /// notify=false 时只提交删除、不触发 Changed，调用方随后用 <see cref="RaiseChanged"/> 异步刷新界面。
    /// </summary>
    public string? RemoveExternalFirst(IEnumerable<string> candidates, bool notify = true)
    {
        foreach (var id in candidates)
            if (RemoveExternal(id, notify)) return id;
        return null;
    }

    /// <summary>配合 <c>notify: false</c> 的删除：把界面刷新补上（应在 UI 线程异步调用）。</summary>
    public void RaiseChanged() => Changed?.Invoke();

    private static IslandActivity? ToActivity(PushDraft d, double? progress, string? dispatch)
    {
        if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Title)) return null;
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
        return new IslandActivity(d.Id, d.Source, d.Title, d.Body, kind, prio, d.Icon, d.Subtitle,
            d.DurationSeconds, d.ResetTtl, IslandActivity.SafeUri(d.OpenUri), d.AppId, ExactDuration: d.DurationExplicit,
            Dispatch: dispatch);
    }

    /// <summary>内部提供方写一条：内部永远有权威，同 id 若是外部写的就收回为内部活动。</summary>
    public void Upsert(IslandActivity a, bool sticky = false, double? progress = null, bool quiet = false) =>
        UpsertCore(a, sticky, progress, quiet, isExternal: false);

    private void UpsertCore(IslandActivity a, bool sticky, double? progress, bool quiet, bool isExternal)
    {
        var isNew = !_items.TryGetValue(a.Id, out var live);
        live ??= new LiveActivity { Id = a.Id };
        var changed = ActivityMerge.Apply(live, a, isNew, sticky, progress, quiet, DateTime.Now);
        live.External = isExternal;
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
        // 只收回自己（内部）写的条目：外部卡片打着同样的来源标签也不会被快照替换掉
        foreach (var id in _items.Values.Where(x => !x.External && x.Source == source && !keep.Contains(x.Id)).Select(x => x.Id).ToList())
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

    public bool Remove(string id, bool notify = true)
    {
        if (!_items.Remove(id)) return false;
        if (notify) Changed?.Invoke();
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

    /// <summary>排序：等待确认 &gt; 出错 &gt; 优先级 &gt; 最近更新。环境信息（天气数据）不进主队列，天气提醒（weather-alert）照常进。</summary>
    public IReadOnlyList<LiveActivity> Ranked() => ActivityRanking.Rank(_items.Values);

    /// <summary>能上收起胶囊的活动（去掉安静的）：没有它们时，收起的岛显示时间。</summary>
    /// <summary>专注中（见 <see cref="FocusMode"/>）只放行关键的几类，其余也只进列表。</summary>
    public IReadOnlyList<LiveActivity> Pill()
    {
        var focus = FocusMode.Active;
        return Ranked().Where(x => !x.Quiet && (!focus || FocusMode.Passes(x))).ToList();
    }

    public LiveActivity? Get(string id) => _items.TryGetValue(id, out var x) ? x : null;
}
