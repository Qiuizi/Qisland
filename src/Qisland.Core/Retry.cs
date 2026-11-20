namespace Qisland.Core;

/// <summary>带退避的重试：先立刻试一次，失败按 delays 依次等一会儿再试，全部用完还不行报告失败。</summary>
public static class Retry
{
    public static readonly TimeSpan[] Default = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];

    public static void Run(Func<bool> attempt, Action<TimeSpan, Action> later, Action<bool> done, IReadOnlyList<TimeSpan>? delays = null)
    {
        delays ??= Default;
        void Step(int i)
        {
            bool ok;
            try { ok = attempt(); } catch { ok = false; }
            if (ok) { done(true); return; }
            if (i >= delays.Count) { done(false); return; }
            later(delays[i], () => Step(i + 1));
        }
        Step(0);
    }
}
