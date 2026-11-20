using System.Text.Json;

namespace Qisland.Core;

/// <summary>各平台最近一次拉到的作业（内存）+ 序列化成磁盘缓存。</summary>
public sealed class HomeworkBook
{
    private readonly Dictionary<string, List<HomeworkItem>> _by = [];

    public IEnumerable<HomeworkItem> All => _by.Values.SelectMany(x => x);
    public IReadOnlyList<HomeworkItem> Items(string platform) => _by.TryGetValue(platform, out var l) ? l.ToList() : [];
    public void Update(string platform, IEnumerable<HomeworkItem> items) => _by[platform] = items.ToList();
    public bool Clear(string platform) => _by.Remove(platform);
    public void Set(string platform, List<HomeworkItem> items) => _by[platform] = items;

    public string ToJson() => JsonSerializer.Serialize(All.ToList());

    public static HomeworkBook FromJson(string? json)
    {
        var b = new HomeworkBook();
        try
        {
            if (!string.IsNullOrWhiteSpace(json))
                foreach (var g in (JsonSerializer.Deserialize<List<HomeworkItem>>(json) ?? []).GroupBy(i => i.Platform))
                    b._by[g.Key] = g.ToList();
        }
        catch { }
        return b;
    }
}

/// <summary>
/// 一个平台的「拉作业 → 提交结果」流程：刷新开始时领票（会话代数），提交时把作业放进列表、写磁盘缓存、存 Cookie；
/// 退出登录 / 换账号换代，旧票的结果什么都不动。退出登录的清理和正在进行的提交在同一把锁里排队。
/// </summary>
public sealed class HomeworkSync(HomeworkBook book, Action<string> writeCache)
{
    private readonly SessionEpoch _epoch = new();

    /// <summary>刷新开始：记下当前会话。</summary>
    public long Begin() => _epoch.Current;

    public bool IsCurrent(long ticket) => _epoch.IsCurrent(ticket);

    /// <summary>新登录 / 换账号：换代，之前在途的刷新全部作废。</summary>
    public long NewSession() => _epoch.Bump();

    /// <summary>刷新拿到了结果：会话没变才更新列表、写缓存、存 Cookie。返回是否采用。</summary>
    public bool Commit(long ticket, string platform, IReadOnlyList<HomeworkItem> items, Action? persist = null) =>
        _epoch.TryCommit(ticket, () =>
        {
            persist?.Invoke();
            book.Update(platform, items);
            writeCache(book.ToJson());
        });

    /// <summary>退出登录：先换代，再（排在正在进行的提交之后）删凭据、清掉这个平台的作业并写缓存。</summary>
    public void Logout(string platform, Action? wipe = null) =>
        _epoch.Bump(() =>
        {
            wipe?.Invoke();
            if (book.Clear(platform)) writeCache(book.ToJson());
        });
}
