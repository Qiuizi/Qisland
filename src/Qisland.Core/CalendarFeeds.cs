namespace Qisland.Core;

/// <summary>
/// 订阅列表 + 每个订阅最近一次拉到的 .ics。拉取是异步的：开始时领一张「票」，
/// 回来时票对不上（期间退订了、或退订后又重新订阅）就丢掉结果，退订的日历不会被正在进行的刷新加回来。
/// </summary>
public sealed class CalendarFeeds
{
    private readonly List<string> _urls = [];
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _epoch = new(StringComparer.Ordinal);
    private long _next;

    public readonly record struct Ticket(string Url, long Epoch);

    public IReadOnlyList<string> Urls => _urls;
    public IReadOnlyDictionary<string, string> Cache => _cache;
    public int Count => _urls.Count;
    public bool Contains(string url) => _urls.Contains(url);

    /// <summary>新订阅（或读盘恢复）；ics 为 null 表示还没有缓存。</summary>
    public bool Add(string url, string? ics)
    {
        if (_urls.Contains(url)) { if (ics != null) _cache[url] = ics; return false; }
        _urls.Add(url);
        _epoch[url] = ++_next;
        if (ics != null) _cache[url] = ics;
        return true;
    }

    public bool Remove(string url)
    {
        if (!_urls.Remove(url)) return false;
        _cache.Remove(url);
        _epoch.Remove(url);
        return true;
    }

    public Ticket BeginFetch(string url) => new(url, _epoch.GetValueOrDefault(url, -1));

    /// <summary>拉取回来了：订阅还在且还是同一次订阅才收下。</summary>
    public bool Apply(Ticket t, string ics)
    {
        if (!_urls.Contains(t.Url) || _epoch.GetValueOrDefault(t.Url, -2) != t.Epoch) return false;
        _cache[t.Url] = ics;
        return true;
    }

    /// <summary>当前订阅着的日历内容（按订阅顺序；退订的不在里面）。</summary>
    public List<string> Sources() => _urls.Where(_cache.ContainsKey).Select(u => _cache[u]).ToList();

    /// <summary>当前订阅内容及来源 URL，用来给每个日程带上稳定的订阅身份。</summary>
    public List<(string Url, string Ics)> SourcesWithUrls() =>
        _urls.Where(_cache.ContainsKey).Select(u => (u, _cache[u])).ToList();
}

/// <summary>代数：后台重建开始时取一个号，结束时不是最新的号就丢掉结果（晚到的旧结果不覆盖新的）。</summary>
public sealed class Generation
{
    private long _n;
    public long Next() => Interlocked.Increment(ref _n);
    public bool IsCurrent(long g) => Interlocked.Read(ref _n) == g;
}
