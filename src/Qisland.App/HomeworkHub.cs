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

    /// <summary>每分钟调用：24 小时内截止的作业作为活动上岛，3 小时内升级为警告（会弹一下）。</summary>
    public void Tick()
    {
        var now = DateTime.Now;
        var soon = Pending().Where(i => i.Deadline - now <= TimeSpan.FromHours(24)).Take(3).ToList();
        var ids = new HashSet<string>();
        foreach (var i in soon)
        {
            var id = "hw:" + i.Key.GetHashCode().ToString("x");
            ids.Add(id);
            var urgent = i.Deadline - now <= TimeSpan.FromHours(3);
            var body = $"{i.Course} · {i.Deadline:M/d HH:mm} 截止 · 还剩 {i.Left(now)}";
            _store.Upsert(new IslandActivity(id, Source, i.Title, body, urgent ? ActivityKind.Warning : ActivityKind.Info, urgent ? 78 : 45,
                OpenUri: IslandActivity.SafeUri(i.Url)), sticky: true);
        }
        foreach (var gone in _live.Where(x => !ids.Contains(x)).ToList()) _store.Remove(gone);
        _live.Clear();
        _live.UnionWith(ids);
    }
}
