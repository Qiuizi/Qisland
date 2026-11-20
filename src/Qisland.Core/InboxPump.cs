namespace Qisland.Core;

/// <summary>
/// 收件箱分批处理：一批最多 batch 个文件，还有剩下的就排下一批（交给 schedule，比如 Dispatcher 的后台优先级），
/// 不一次性卡住界面，也不会因为启动时积压太多、后面的文件没有新的 Created 事件而一直留在那里。
/// 一批里一个都没取走（都被占用）时不立刻重排，等文件监视下一次叫，免得空转。
/// </summary>
public sealed class InboxPump(Func<IEnumerable<string>> list, Func<string, bool> handle, Action<Action> schedule, int batch = 50)
{
    private bool _pending;

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
        var files = list().Take(batch + 1).ToList();
        var taken = 0;
        foreach (var f in files.Take(batch))
            if (handle(f)) taken++;
        if (files.Count > batch && taken > 0) Request();
    }
}
