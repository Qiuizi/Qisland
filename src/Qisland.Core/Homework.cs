namespace Qisland.Core;

/// <summary>一条作业 / 测验 / 考试的截止信息（来自雨课堂、学习通等，只读）。</summary>
public sealed record HomeworkItem(string Platform, string Course, string Title, DateTime Deadline, bool Done, string Url = "", string Kind = "作业")
{
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
