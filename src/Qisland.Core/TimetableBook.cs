namespace Qisland.Core;

/// <summary>
/// 课表的内存状态：.ics 文本 + 按天展开的这一周。导入时直接换上新文本、立即重建（不用等文件监视的防抖），
/// 文件被删就清空。
/// </summary>
public sealed class TimetableBook
{
    private string _ics = "";
    private DateTime _builtFor = DateTime.MinValue;
    private IReadOnlyList<ClassSession> _week = [];
    private readonly TimeZoneInfo? _local;

    /// <param name="local">显示用的时区（默认本机；单测里指定）。</param>
    public TimetableBook(TimeZoneInfo? local = null) => _local = local;

    public bool HasTimetable => _ics.Length > 0;
    public IReadOnlyList<ClassSession> Week => _week;

    /// <summary>换上新的课表文本（null / 空 = 没有课表）；下一次查询会按新文本重建。</summary>
    public void SetText(string? ics)
    {
        _ics = ics ?? "";
        _builtFor = DateTime.MinValue;
        if (_ics.Length == 0) _week = [];
    }

    /// <summary>按「今天」重建一周；同一天只建一次。返回是否重建了。</summary>
    public bool Rebuild(DateTime now)
    {
        if (_builtFor == now.Date) return false;
        _builtFor = now.Date;
        _week = _ics.Length == 0 ? [] : Timetable.Expand(_ics, now.Date, now.Date.AddDays(8), _local);
        return true;
    }

    public IReadOnlyList<ClassSession> Day(DateTime d)
    {
        Rebuild(_builtFor == DateTime.MinValue ? d : _builtFor);
        return _week.Where(c => c.Start.Date == d.Date).ToList();
    }
}
