namespace Qisland.Core;

/// <summary>一条作业 / 测验 / 考试的截止信息（来自雨课堂、学习通等，只读）。</summary>
/// <param name="Ungraded">交了但老师还没批（主观题待批改）。这类算完成，只在「已交待批」里单独列出来。</param>
public sealed record HomeworkItem(string Platform, string Course, string Title, DateTime Deadline, bool Done, string Url = "", string Kind = "作业", bool Ungraded = false,
    string CourseId = "", string ActivityId = "", string Account = "")
{
    /// <summary>截止提醒的档位：24 / 3 / 1 小时内，0 表示还早。</summary>
    public int Tier(DateTime now)
    {
        var d = Deadline - now;
        if (d <= TimeSpan.Zero) return 0;
        return d <= TimeSpan.FromHours(1) ? 1 : d <= TimeSpan.FromHours(3) ? 3 : d <= TimeSpan.FromHours(24) ? 24 : 0;
    }

    public string Key => $"{Platform}|{Course}|{Title}|{Deadline:yyyyMMddHHmm}";

    /// <summary>
    /// 提醒去重用的键，不含截止时间——学习通列表只给「剩余时间」，每次拉取反推的截止时间会差一两分钟，带分钟的键会让同一档提醒弹两次。
    /// 有平台活动 ID 时按 平台 + 账号 + 课程 + 活动 ID 区分（雨课堂同一门课的作业共用一个链接，不能按链接认）；
    /// 没有活动 ID 的（学习通每份作业链接各不相同）沿用 作业链接 / 平台+课程+标题。
    /// </summary>
    public string AlertKey => ActivityId.Length > 0 ? $"{Platform}|{Account}|{CourseId}|{ActivityId}"
        : Url.Length > 0 ? $"{Platform}|{Url}" : $"{Platform}|{Course}|{Title}";

    /// <summary>旧版本可能用过的提醒键（4.0.0 带截止分钟的键、4.0.9 及以前的链接 / 课程+标题键），升级时用来继承提醒档位。</summary>
    public IEnumerable<string> LegacyAlertKeys()
    {
        if (Url.Length > 0) yield return $"{Platform}|{Url}";
        yield return $"{Platform}|{Course}|{Title}";
        yield return Key;
    }

    public string Left(DateTime now)
    {
        var d = Deadline - now;
        if (d <= TimeSpan.Zero) return "已截止";
        if (d.TotalMinutes < 60) return $"{Math.Max(1, (int)d.TotalMinutes)} 分钟";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} 小时";
        return $"{(int)Math.Ceiling(d.TotalDays - 0.0001)} 天";
    }

    /// <summary>从毫秒或秒级时间戳转本地时间；0 或离谱的值返回 null。</summary>
    public static DateTime? FromStamp(double v)
    {
        if (v <= 0) return null;
        var ms = v > 1e11 ? v : v * 1000;
        try
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
            return t.Year is > 2015 and < 2100 ? t : null;
        }
        catch { return null; }
    }
}

/// <summary>作业截止的分档提醒（24 / 3 / 1 小时各一次）：挑下一条该提醒的、记账、清掉已经不在列表里的。</summary>
public static class HomeworkAlerts
{
    public static (HomeworkItem item, int tier)? Next(IEnumerable<HomeworkItem> pending, IReadOnlyDictionary<string, int> alerted, DateTime now)
    {
        var due = pending.Select(i => (i, t: i.Tier(now)))
            .Where(x => x.t > 0 && (!alerted.TryGetValue(x.i.AlertKey, out var had) || x.t < had))
            .OrderBy(x => x.t).FirstOrDefault();
        return due.i is null ? null : (due.i, due.t);
    }

    public static void Mark(IDictionary<string, int> alerted, HomeworkItem item, int tier) => alerted[item.AlertKey] = tier;

    /// <summary>清掉不在待办列表里的记录；返回是否有变化。</summary>
    public static bool Prune(IDictionary<string, int> alerted, IEnumerable<HomeworkItem> pending)
    {
        var list = pending.ToList();
        var migrated = false;
        // 旧版本的键搬到新键上，升级后不重复提醒；但一条旧记录只在唯一对上一份作业时才继承——
        // 雨课堂同一门课的几份作业旧键相同，谁继承都会压掉别人的提醒，干脆都不继承
        var claims = list.SelectMany(p => p.LegacyAlertKeys().Where(k => k != p.AlertKey).Distinct().Select(k => (k, p.AlertKey)))
            .GroupBy(x => x.k).ToDictionary(g => g.Key, g => g.Select(x => x.AlertKey).Distinct().Count());
        foreach (var p in list)
        {
            if (alerted.ContainsKey(p.AlertKey)) continue;
            foreach (var k in p.LegacyAlertKeys())
                if (k != p.AlertKey && claims.GetValueOrDefault(k) == 1 && alerted.TryGetValue(k, out var old)) { alerted[p.AlertKey] = old; migrated = true; break; }
        }
        var keys = list.Select(p => p.AlertKey).ToHashSet();
        var stale = alerted.Keys.Where(k => !keys.Contains(k)).ToList();
        foreach (var k in stale) alerted.Remove(k);
        return stale.Count > 0 || migrated;
    }
}
