using Qisland.Core;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 退出登录后的收尾：凭据文件 / 作业缓存没删干净时按 1s、2s、5s、10s、30s、30s 退避重试；
/// 期间有退出标记兜底（重启也不会恢复登录）。最后还是不行就在岛上常驻一条错误提示。
/// 重试期间又登录了就停下，不会删掉新登录的凭据。
/// </summary>
internal static class LogoutCleanup
{
    public static void Start(string platform, LoginFile file, LogoutOutcome first, Func<bool> flushCache, Func<bool> stillLoggedOut, ActivityStore store, string cardId)
    {
        var cacheOk = flushCache();
        if (first == LogoutOutcome.Removed && cacheOk) { store.Remove(cardId); return; }
        App.Log($"{platform}: 退出登录没清理干净（凭据：{first}，作业缓存：{(cacheOk ? "已更新" : "写入失败")}），稍后重试");
        Retry.Run(
            () => !stillLoggedOut() || (file.Cleanup() && flushCache()),
            (delay, next) =>
            {
                var t = new DispatcherTimer { Interval = delay };
                t.Tick += (_, _) => { t.Stop(); next(); };
                t.Start();
            },
            ok =>
            {
                if (ok) { App.Log($"{platform}: 退出登录的清理已完成"); store.Remove(cardId); return; }
                App.Log($"{platform}: 退出登录的清理多次重试仍失败（凭据文件被占用）");
                store.Upsert(new IslandActivity(cardId, HomeworkHub.Source, $"{platform}退出登录没有清理干净",
                    System.IO.File.Exists(file.TombPath) ? "凭据文件被别的程序占用，删不掉；已记下退出状态，重启也不会恢复登录" : "凭据文件删不掉，也写不了退出标记；重启后可能仍是登录状态，关掉占用它的程序后再退出一次",
                    ActivityKind.Error, 70), sticky: true);
            });
    }

    /// <summary>退出时就已经两样都没做成：立刻告诉用户。</summary>
    public static string? Message(string platform, LogoutOutcome o) => o == LogoutOutcome.Failed
        ? $"{platform}退出登录没有完成：凭据文件被占用删不掉，也写不了退出标记。\n现在已经退出，但重启 Qisland 后可能仍是登录状态。会在后台继续重试；也可以关掉占用它的程序后再退出一次。"
        : null;
}
