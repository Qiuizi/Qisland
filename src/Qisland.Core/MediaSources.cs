namespace Qisland.Core;

/// <summary>
/// 可以切换的媒体来源（切换按钮的计数、轮换、选中用同一份候选键）。
/// 系统媒体会话（GSMTC）的 SourceAppUserModelId 标识的是程序，不是会话：同一个浏览器的两个窗口 / 标签页、
/// 同一个播放器开了两个实例，会各有一个会话但 id 相同，系统媒体浮窗里也是两条。所以不按来源去重（那样第二个永远切不到），
/// 而是给每个会话一个唯一键：某个 id 第一次出现就用 id 本身（和以前一样），第 n 次出现是「id#n」，按 GetSessions 的顺序。
/// </summary>
public static class MediaSources
{
    /// <summary>网易云只在日志里（没开 SMTC）时的伪来源 id。</summary>
    public const string NcmLog = "qisland:ncm-log";

    /// <summary>系统媒体会话的键 + （网易云只在日志里时）网易云伪来源。</summary>
    public static List<string> Candidates(IReadOnlyList<string> sessionIds, bool ncmLogOnly)
    {
        var list = Keys(sessionIds);
        if (ncmLogOnly) list.Add(NcmLog);
        return list;
    }

    /// <summary>每个会话的唯一键（同 id 的第 n 个是 id#n）。</summary>
    public static List<string> Keys(IReadOnlyList<string> sessionIds)
    {
        var seen = new Dictionary<string, int>();
        var keys = new List<string>(sessionIds.Count);
        foreach (var id in sessionIds)
        {
            var n = seen.GetValueOrDefault(id) + 1;
            seen[id] = n;
            keys.Add(n == 1 ? id : $"{id}#{n}");
        }
        return keys;
    }

    /// <summary>界面上显示的来源数（≥ 2 才出切换按钮），就是候选的个数。</summary>
    public static int Count(IReadOnlyList<string> sessionIds, bool ncmLogOnly) => Math.Max(1, Candidates(sessionIds, ncmLogOnly).Count);

    /// <summary>第 index 个会话的候选键。</summary>
    public static string KeyAt(IReadOnlyList<string> sessionIds, int index) => Keys(sessionIds)[index];

    /// <summary>候选键对应第几个会话；找不到（会话关了、或是网易云伪来源）-1。</summary>
    public static int Resolve(IReadOnlyList<string> sessionIds, string key) => Keys(sessionIds).IndexOf(key);

    /// <summary>当前来源的下一个；不到两个返回 null。</summary>
    public static string? Next(IReadOnlyList<string> candidates, string? current)
    {
        if (candidates.Count < 2) return null;
        var i = current == null ? -1 : candidates.ToList().IndexOf(current);
        return candidates[(i + 1 + candidates.Count) % candidates.Count];
    }
}
