using System.Text.Json;

namespace Qisland.Core;

/// <summary>
/// 各平台最近一次拉到的作业（内存）+ 序列化成磁盘缓存。每个平台记着这些作业属于哪个账号（<see cref="AccountId"/> 的哈希），
/// 缓存格式 {"v":2,"owners":{平台:账号},"items":[...]}；4.0.3 及以前的纯列表读进来没有账号标记，不会被任何账号认领。
/// </summary>
public sealed class HomeworkBook
{
    private readonly Dictionary<string, List<HomeworkItem>> _by = [];
    private readonly Dictionary<string, string> _owners = [];

    public IEnumerable<HomeworkItem> All => _by.Values.SelectMany(x => x);
    public IReadOnlyList<HomeworkItem> Items(string platform) => _by.TryGetValue(platform, out var l) ? l.ToList() : [];
    public void Update(string platform, IEnumerable<HomeworkItem> items) => _by[platform] = items.ToList();

    /// <summary>清掉这个平台的作业和账号标记（退出登录）。返回是否有变化。</summary>
    public bool Clear(string platform) => _by.Remove(platform) | _owners.Remove(platform);
    public void Set(string platform, List<HomeworkItem> items) => _by[platform] = items;

    /// <summary>这个平台的作业属于哪个账号（<see cref="AccountId"/>）；没有标记是空串。</summary>
    public string Owner(string platform) => _owners.GetValueOrDefault(platform, "");

    /// <summary>平台换成 account 这个账号：属于别的账号（或没有标记）的作业撤掉。返回是否有变化（要重写缓存）。</summary>
    public bool Adopt(string platform, string account)
    {
        if (Owner(platform) == account && (account.Length > 0 || !_by.ContainsKey(platform))) return false;
        _by.Remove(platform);
        if (account.Length > 0) _owners[platform] = account; else _owners.Remove(platform);
        return true;
    }

    private sealed record Doc(int V, Dictionary<string, string>? Owners, List<HomeworkItem>? Items);

    public string ToJson() => JsonSerializer.Serialize(new Doc(2, new(_owners), All.ToList()));

    public static HomeworkBook FromJson(string? json)
    {
        var b = new HomeworkBook();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return b;
            List<HomeworkItem> items;
            if (json.TrimStart().StartsWith('[')) items = JsonSerializer.Deserialize<List<HomeworkItem>>(json) ?? [];   // 旧格式：没有账号标记
            else
            {
                var d = JsonSerializer.Deserialize<Doc>(json);
                items = d?.Items ?? [];
                foreach (var (p, o) in d?.Owners ?? []) if (!string.IsNullOrEmpty(o)) b._owners[p] = o;
            }
            foreach (var g in items.GroupBy(i => i.Platform)) b._by[g.Key] = g.ToList();
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

    /// <summary>新账号登录生效：换代，并撤掉别的账号的作业、重写缓存。返回列表是否变了。</summary>
    public bool SwitchAccount(string platform, string account)
    {
        var changed = false;
        _epoch.Bump(() => changed = Adopt(platform, account));
        return changed;
    }

    /// <summary>启动时认领缓存：缓存里这个平台的作业不属于 account（或没登录）就撤掉并重写缓存。</summary>
    public bool Adopt(string platform, string account)
    {
        if (!book.Adopt(platform, account)) return false;
        writeCache(book.ToJson());
        return true;
    }

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
