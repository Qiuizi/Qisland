namespace Qisland.Core;

/// <summary>可以切换的媒体来源（切换按钮的计数和轮换用同一份）。</summary>
public static class MediaSources
{
    /// <summary>网易云只在日志里（没开 SMTC）时的伪来源 id。</summary>
    public const string NcmLog = "qisland:ncm-log";

    /// <summary>系统媒体会话 + （网易云只在日志里时）网易云伪来源。</summary>
    public static List<string> Candidates(IReadOnlyList<string> sessionIds, bool ncmLogOnly)
    {
        var list = sessionIds.ToList();
        if (ncmLogOnly) list.Add(NcmLog);
        return list;
    }

    /// <summary>界面上显示的来源数（≥ 2 才出切换按钮），就是候选的个数。</summary>
    public static int Count(IReadOnlyList<string> sessionIds, bool ncmLogOnly) => Math.Max(1, Candidates(sessionIds, ncmLogOnly).Count);

    /// <summary>当前来源的下一个；不到两个返回 null。</summary>
    public static string? Next(IReadOnlyList<string> candidates, string? current)
    {
        if (candidates.Count < 2) return null;
        var i = current == null ? -1 : candidates.ToList().IndexOf(current);
        return candidates[(i + 1 + candidates.Count) % candidates.Count];
    }
}
