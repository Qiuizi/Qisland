namespace Qisland.Core;

/// <summary>一条作业 / 测验 / 考试的截止信息（来自雨课堂、学习通等，只读）。</summary>
/// <param name="Ungraded">交了但老师还没批（主观题待批改）。这类算完成，只在「已交待批」里单独列出来。</param>
public sealed record HomeworkItem(string Platform, string Course, string Title, DateTime Deadline, bool Done, string Url = "", string Kind = "作业", bool Ungraded = false)
{
    /// <summary>截止提醒的档位：24 / 3 / 1 小时内，0 表示还早。</summary>
    public int Tier(DateTime now)
    {
        var d = Deadline - now;
        if (d <= TimeSpan.Zero) return 0;
        return d <= TimeSpan.FromHours(1) ? 1 : d <= TimeSpan.FromHours(3) ? 3 : d <= TimeSpan.FromHours(24) ? 24 : 0;
    }

    public string Key => $"{Platform}|{Course}|{Title}|{Deadline:yyyyMMddHHmm}";

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
