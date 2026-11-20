namespace Qisland.Core;

/// <summary>
/// 登录会话的 Cookie 罐 + 代数。退出登录 / 换账号都换一代；请求发出时记下代数，
/// 回来时代数不对（期间退出或换了号）就什么都不动：不合并 Set-Cookie、不改登录状态、不写 DPAPI 文件。
/// 写盘和退出登录的删文件在同一把锁里排队，退出登录之后不会再有旧会话写回磁盘。
/// </summary>
public sealed class SessionJar
{
    private readonly object _lock = new();
    private readonly object _disk = new();
    private CookieSet _cookies = new();
    private long _gen = 1;

    public long Generation { get { lock (_lock) return _gen; } }
    public bool IsCurrent(long gen) { lock (_lock) return gen == _gen; }

    public string? this[string name] { get { lock (_lock) return _cookies[name]; } }
    public DateTimeOffset? ExpiresOf(string name) { lock (_lock) return _cookies.ExpiresOf(name); }
    public bool CanAuthenticate(string sessionCookie, DateTimeOffset now) { lock (_lock) return _cookies.CanAuthenticate(sessionCookie, now); }

    /// <summary>当前 Cookie 的副本（序列化 / 写盘用）。</summary>
    public CookieSet Snapshot() { lock (_lock) return Copy(_cookies); }

    private static CookieSet Copy(CookieSet c)
    {
        var s = new CookieSet();
        foreach (var (n, e) in c.All) s.Set(n, e.Value, e.Expires);
        return s;
    }

    /// <summary>请求头；会话已经换代时返回 false（旧请求链不能再拿新会话的 Cookie 发请求）。</summary>
    public bool TryHeader(long gen, DateTimeOffset now, out string header)
    {
        lock (_lock)
        {
            header = gen == _gen ? _cookies.Header(now) : "";
            return gen == _gen;
        }
    }

    public sealed record AbsorbResult(bool Stale, bool Changed, bool Deleted, bool Rotated, DateTimeOffset? Expires, IReadOnlyList<string> Names);

    /// <summary>合并一次响应的 Set-Cookie；gen 不是当前代就丢弃。</summary>
    public AbsorbResult Absorb(long gen, IEnumerable<string> setCookies, DateTimeOffset now, string sessionCookie = "sessionid")
    {
        var names = new List<string>();
        lock (_lock)
        {
            if (gen != _gen) return new(true, false, false, false, null, names);
            bool changed = false, deleted = false;
            var before = _cookies[sessionCookie];
            foreach (var v in setCookies)
            {
                var (c, d) = _cookies.Apply(v, now);
                var n = v.Split('=', 2)[0].Trim();
                names.Add(n);
                changed |= c;
                if (n == sessionCookie) deleted |= d;
            }
            var rotated = before != null && _cookies[sessionCookie] is { } after && after != before;
            return new(false, changed, deleted, rotated, _cookies.ExpiresOf(sessionCookie), names);
        }
    }

    /// <summary>换成新登录的 Cookie（扫码登录 / 换账号 / 读盘），开启新的一代。返回新代数。</summary>
    public long Replace(CookieSet next)
    {
        lock (_lock) { _cookies = Copy(next); return ++_gen; }
    }

    /// <summary>退出登录：先换代（在途请求立刻作废），再在写盘锁里执行 wipe（删凭据文件），排在正在进行的保存之后。</summary>
    public long Clear(Action? wipe = null)
    {
        long g;
        lock (_lock) { _cookies = new CookieSet(); g = ++_gen; }
        if (wipe != null) lock (_disk) wipe();
        return g;
    }

    /// <summary>gen 仍是当前代才写盘（在写盘锁里检查，和退出登录的删除不会交错）。</summary>
    public bool TryPersist(long gen, Action<CookieSet> write)
    {
        lock (_disk)
        {
            CookieSet snap;
            lock (_lock) { if (gen != _gen) return false; snap = Copy(_cookies); }
            write(snap);
            return true;
        }
    }
}

/// <summary>
/// 会话代数（不带 Cookie 的版本，给 Cookie 放在 CookieContainer 里的平台用，比如学习通）。
/// 退出登录 / 换账号 <see cref="Bump"/>；刷新开始时取 <see cref="Current"/>，提交时 <see cref="TryCommit"/> 检查还是不是这一代。
/// 提交和退出登录的清理在同一把锁里排队：退出登录之后不会再有旧会话的写盘。
/// </summary>
public sealed class SessionEpoch
{
    private readonly object _lock = new();
    private readonly object _disk = new();
    private long _gen = 1;

    public long Current { get { lock (_lock) return _gen; } }
    public bool IsCurrent(long gen) { lock (_lock) return gen == _gen; }

    /// <summary>换一代（在途的请求立刻作废），再在写盘锁里执行 wipe（排在正在进行的提交之后）。</summary>
    public long Bump(Action? wipe = null)
    {
        long g;
        lock (_lock) g = ++_gen;
        if (wipe != null) lock (_disk) wipe();
        return g;
    }

    /// <summary>gen 仍是当前代才执行 commit（在写盘锁里检查）。</summary>
    public bool TryCommit(long gen, Action commit)
    {
        lock (_disk)
        {
            if (!IsCurrent(gen)) return false;
            commit();
            return true;
        }
    }
}
