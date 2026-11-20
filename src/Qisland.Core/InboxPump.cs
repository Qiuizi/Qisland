namespace Qisland.Core;

/// <summary>
/// 收件箱分批处理：一批最多 batch 个文件，还有剩下的就排下一批（交给 schedule，比如 Dispatcher 的后台优先级），
/// 不一次性卡住界面，也不会因为启动时积压太多、后面的文件没有新的 Created 事件而一直留在那里。
/// 取不走的文件（还没写完 / 被占用）按 1s、2s、5s、10s、30s（封顶）延时重试，不用等新的文件事件；
/// 等待中的文件不占批次名额，不会挡住后面的文件；连续失败 maxAttempts 次就放弃（交给 giveUp：移走 / 记日志）。
/// 处理单个文件时抛异常（内容不合要求等）不中断这一批：交给 invalid（移到 failed / 记日志），接着处理后面的，照常排下一批。
/// 所有状态只在 schedule 的线程上动（later 到点后也是经 schedule 回来）。
/// </summary>
public sealed class InboxPump(
    Func<IEnumerable<string>> list,
    Func<string, bool> handle,
    Action<Action> schedule,
    int batch = 50,
    Action<TimeSpan, Action>? later = null,
    Func<DateTime>? clock = null,
    int maxAttempts = 8,
    Action<string, int>? giveUp = null,
    Action<string, Exception>? invalid = null)
{
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    private readonly Action<TimeSpan, Action> _later = later ?? ((d, a) => Task.Delay(d).ContinueWith(_ => schedule(a)));
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly Dictionary<string, (int Fails, DateTime Due)> _wait = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _wakeAt;
    private bool _pending;

    /// <summary>第 n 次失败后等多久再试。</summary>
    public static TimeSpan Delay(int fails) => Backoff[Math.Clamp(fails - 1, 0, Backoff.Length - 1)];

    /// <summary>正在等待重试的文件数（测试 / 日志用）。</summary>
    public int Waiting => _wait.Count;

    /// <summary>要求处理一次；已经排着一批时合并。</summary>
    public void Request()
    {
        if (_pending) return;
        _pending = true;
        schedule(Run);
    }

    private void Run()
    {
        _pending = false;
        var now = _clock();
        var tried = 0;
        var more = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in list())
        {
            seen.Add(f);
            var waiting = _wait.TryGetValue(f, out var w);
            if (waiting && w.Due > now) continue;                 // 还没到重试时间，不占名额
            if (tried == batch) { more = true; break; }
            tried++;
            bool ok;
            try { ok = handle(f); }
            catch (Exception ex)
            {
                _wait.Remove(f);
                try { if (invalid != null) invalid(f, ex); else giveUp?.Invoke(f, 0); } catch { }
                continue;
            }
            if (ok) { _wait.Remove(f); continue; }
            var fails = (waiting ? w.Fails : 0) + 1;
            if (fails >= maxAttempts) { _wait.Remove(f); giveUp?.Invoke(f, fails); continue; }
            _wait[f] = (fails, now + Delay(fails));
        }
        if (!more)                                                 // 整个目录都看过了：已经不在的文件不再等
            foreach (var gone in _wait.Keys.Where(k => !seen.Contains(k)).ToList()) _wait.Remove(gone);
        if (more) Request();
        ArmWake(now);
    }

    /// <summary>按最早到期的等待文件定一个闹钟；已有更早的闹钟就不重复定。</summary>
    private void ArmWake(DateTime now)
    {
        if (_wait.Count == 0) return;
        var next = _wait.Values.Min(v => v.Due);
        if (_wakeAt is { } at && at <= next && at > now) return;
        _wakeAt = next;
        var delay = next - now;
        _later(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, () =>
        {
            if (_wakeAt == next) _wakeAt = null;
            Request();
        });
    }
}
