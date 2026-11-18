using Qisland.Core;
using System.IO;
using System.Text.Json;

namespace Qisland.App;

/// <summary>
/// 作业汇总：雨课堂、学习通各自定时把「未完成且没截止」的作业交给这里，
/// 这里负责合并、缓存（重启后先显示上次的结果），并在 24 小时内截止时上岛提醒。
/// </summary>
public sealed class HomeworkHub
{
    public const string Source = "homework";
    private static string CachePath => Path.Combine(App.DataDir, "homework-cache.json");
    private readonly ActivityStore _store;
    private readonly Dictionary<string, List<HomeworkItem>> _byPlatform = [];
    private readonly HashSet<string> _live = [];
    public event Action? Changed;

    public HomeworkHub(ActivityStore store, bool load = true)
    {
        _store = store;
        if (load) try
        {
            if (File.Exists(CachePath))
                foreach (var g in (JsonSerializer.Deserialize<List<HomeworkItem>>(File.ReadAllText(CachePath)) ?? []).GroupBy(i => i.Platform))
                    _byPlatform[g.Key] = g.ToList();
        }
        catch { }
    }

    /// <summary>未完成、未截止的作业，按截止时间排序。</summary>
    public IReadOnlyList<HomeworkItem> Pending()
    {
        var now = DateTime.Now;
        return _byPlatform.Values.SelectMany(x => x).Where(i => !i.Done && i.Deadline > now).OrderBy(i => i.Deadline).ToList();
    }

    public void Update(string platform, IReadOnlyList<HomeworkItem> items)
    {
        _byPlatform[platform] = items.ToList();
        try { File.WriteAllText(CachePath, JsonSerializer.Serialize(_byPlatform.Values.SelectMany(x => x).ToList())); } catch { }
        Tick();
        Changed?.Invoke();
    }

    /// <summary>截图演示用：不写缓存、不上岛。</summary>
    public static HomeworkHub Demo(ActivityStore store)
    {
        var h = new HomeworkHub(store, load: false);
        var n = DateTime.Now;
        h._byPlatform["雨课堂"] = [
            new("雨课堂", "概率论与数理统计", "第 9 章 习题课后测", n.AddHours(2.4), false),
            new("雨课堂", "计算机组成原理", "实验三 报告提交", n.AddHours(20), false),
        ];
        h._byPlatform["学习通"] = [new("学习通", "形势与政策", "专题四 讨论作业", n.AddDays(3).AddHours(1), false)];
        return h;
    }

    public void Clear(string platform)
    {
        if (_byPlatform.Remove(platform)) Update(platform, []);
    }

    /// <summary>交了但老师还没批的（雨课堂主观题待批改、学习通「待批阅」），截止前后几天内的。</summary>
    public IReadOnlyList<HomeworkItem> Submitted()
    {
        var since = DateTime.Now.AddDays(-2);
        return _byPlatform.Values.SelectMany(x => x).Where(i => i.Done && i.Ungraded && i.Deadline > since).OrderBy(i => i.Deadline).ToList();
    }

    // 每项作业已经提醒到哪一档（24 / 3 / 1 小时），存盘，重启后不重复弹
    private static string AlertsPath => Path.Combine(App.DataDir, "homework-alerts.json");
    private Dictionary<string, int>? _alerted;
    private string? _alertKey;
    private Dictionary<string, int> Alerted
    {
        get
        {
            if (_alerted != null) return _alerted;
            try { _alerted = File.Exists(AlertsPath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(AlertsPath)) : null; } catch { }
            return _alerted ??= [];
        }
    }

    /// <summary>
    /// 每分钟调用。课业雷达：
    /// · 不常驻：收起的岛平时就显示时间，作业只在展开的主页卡片里；
    /// · 每项没交的作业跨进 24 / 3 / 1 小时这三档时各弹一次临时提醒（12 / 30 秒后自己消失），一分钟最多一条；
    /// · 交了（包括已交待批）的作业不算未完成，不提醒。
    /// </summary>
    public void Tick()
    {
        var now = DateTime.Now;
        var pending = Pending();
        // 不再常驻：24 小时内的作业只在展开的主页作业卡片里列着，收起的岛照常显示时间。
        // 旧版本挂上去的常驻活动（以及已经交了的那条的提醒）一律撤掉
        foreach (var gone in _live.ToList()) _store.Remove(gone);
        _live.Clear();
        if (_alertKey != null && !pending.Any(p => p.Key == _alertKey)) { _store.Remove("hw:alert"); _alertKey = null; }

        // 分档提醒：找最急的一条还没在这一档提醒过的
        var alerted = Alerted;
        var stale = alerted.Keys.Where(k => !pending.Any(p => p.Key == k)).ToList();
        foreach (var k in stale) alerted.Remove(k);
        if (stale.Count > 0) try { File.WriteAllText(AlertsPath, JsonSerializer.Serialize(alerted)); } catch { }
        var due = pending.Select(i => (i, t: i.Tier(now))).Where(x => x.t > 0 && (!alerted.TryGetValue(x.i.Key, out var had) || x.t < had)).OrderBy(x => x.t).FirstOrDefault();
        if (due.i is { } hw)
        {
            alerted[hw.Key] = due.t;
            _alertKey = hw.Key;
            try { File.WriteAllText(AlertsPath, JsonSerializer.Serialize(alerted)); } catch { }
            var head = due.t switch { 1 => "作业 1 小时内截止", 3 => "作业 3 小时内截止", _ => "作业明天前截止" };
            _store.Upsert(new IslandActivity("hw:alert", Source, $"{head} · {hw.Title}", $"{hw.Course} · {hw.Deadline:M/d HH:mm} · 还剩 {hw.Left(now)}",
                due.t == 1 ? ActivityKind.Error : ActivityKind.Warning, 88, DurationSeconds: due.t == 24 ? 12 : 30, ResetTtl: true, OpenUri: IslandActivity.SafeUri(hw.Url)));
            App.Log($"作业提醒: {due.t} 小时档");
        }
    }

    /// <summary>登录快过期（3 天内）时常驻一条小提示，过期前就去重新扫码，别等作业突然读不到。</summary>
    public static void WarnExpiring(ActivityStore store, string id, string name, DateTime? expires)
    {
        var now = DateTime.Now;
        if (expires is not { } e || e - now > TimeSpan.FromDays(3) || e <= now)
        {
            if (store.Get(id) != null) store.Remove(id);
            return;
        }
        var left = e - now;
        var text = left.TotalHours < 24 ? $"{Math.Max(1, (int)left.TotalHours)} 小时后过期" : $"{(int)Math.Ceiling(left.TotalDays)} 天后过期";
        store.Upsert(new IslandActivity(id, Source, $"{name}登录{text}", "托盘菜单 → 作业 DDL → 重新扫码登录", ActivityKind.Warning, 40), sticky: true, quiet: true);
    }
}
