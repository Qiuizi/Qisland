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
            Later,
            ok =>
            {
                if (ok) { App.Log($"{platform}: 退出登录的清理已完成"); store.Remove(cardId); return; }
                App.Log($"{platform}: 退出登录的清理多次重试仍失败（凭据文件被占用）");
                store.Upsert(new IslandActivity(cardId, HomeworkHub.Source, $"{platform}退出登录没有清理干净",
                    System.IO.File.Exists(file.TombPath) ? "凭据文件被别的程序占用，删不掉；已记下退出状态，重启也不会恢复登录" : "凭据文件删不掉，也写不了退出标记；重启后可能仍是登录状态，关掉占用它的程序后再退出一次",
                    ActivityKind.Error, 70), sticky: true);
            });
    }

    /// <summary>过一会儿在界面线程上执行（重试用）。</summary>
    public static void Later(TimeSpan delay, Action next)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, App.Current.Dispatcher) { Interval = delay };
        t.Tick += (_, _) => { t.Stop(); next(); };
        t.Start();
    }

    private static readonly TimeSpan[] LoadDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    /// <summary>
    /// 启动时退出标记读不到（状态未知）：文件都留着、先不登录，按退避再判断；判断得了就照结果办（能用就恢复登录，撤销了就清理）。
    /// 期间用户自己扫码登录了就停下。
    /// </summary>
    public static void DeferLoad(string platform, Func<CredentialCheck> tryLoad, Func<bool> loggedIn)
    {
        Later(LoadDelays[0], () => Retry.Run(
            () => loggedIn() || tryLoad() != CredentialCheck.Unknown,
            Later,
            ok => App.Log(ok ? $"{platform}: 退出标记已能读取，登录状态已判定" : $"{platform}: 退出标记一直读不到，先不恢复登录（文件都保留；需要时重新扫码）"),
            LoadDelays[1..]));
    }

    /// <summary>退出时就已经两样都没做成：立刻告诉用户。</summary>
    public static string? Message(string platform, LogoutOutcome o) => o == LogoutOutcome.Failed
        ? $"{platform}退出登录没有完成：凭据文件被占用删不掉，也写不了退出标记。\n现在已经退出，但重启 Qisland 后可能仍是登录状态。会在后台继续重试；也可以关掉占用它的程序后再退出一次。"
        : null;
}
