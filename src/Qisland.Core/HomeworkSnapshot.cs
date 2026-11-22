namespace Qisland.Core;

/// <summary>一门课这次拉取的结果。只有 <see cref="Parsed"/> 和 <see cref="Empty"/> 算拿到了确定的新数据。</summary>
public enum HomeworkCourseState
{
    /// <summary>解析出了作业条目。</summary>
    Parsed,
    /// <summary>页面 / 接口明确表示没有作业（确认是空的）：这门课的旧作业清掉。</summary>
    Empty,
    /// <summary>接口返回里缺了该有的字段：沿用旧数据。</summary>
    MissingFields,
    /// <summary>列表容器里有条目，但一条都没解析出来（多半是页面改版）：沿用旧数据。</summary>
    Unparsed,
    /// <summary>这门课请求失败：沿用旧数据。</summary>
    Failed,
}

/// <summary>
/// 一次拉取的作业快照：逐门课记下状态，没拿到确定结果的课沿用上次的作业。
/// 旧作业按稳定的课程 ID 认（课程改名不影响）；4.0.9 及以前缓存里没有课程 ID 的旧条目退回按课程名认。
/// </summary>
public sealed class HomeworkSnapshot(IReadOnlyList<HomeworkItem> previous)
{
    private readonly List<HomeworkItem> _items = [];
    private readonly Dictionary<HomeworkCourseState, int> _states = [];
    private readonly HashSet<string> _kept = [];

    public IReadOnlyList<HomeworkItem> Items => _items;
    public IReadOnlyDictionary<HomeworkCourseState, int> States => _states;

    public static bool SameCourse(HomeworkItem i, string courseId, string courseName) =>
        i.CourseId.Length > 0 && courseId.Length > 0 ? i.CourseId == courseId : i.Course == courseName;

    public void Add(HomeworkItem item) => _items.Add(item);

    /// <summary>记下一门课的状态；没拿到确定结果的（缺字段 / 0 条解析 / 失败）把这门课的旧作业原样带上。</summary>
    public void Course(string courseId, string courseName, HomeworkCourseState state)
    {
        _states[state] = _states.GetValueOrDefault(state) + 1;
        if (state is HomeworkCourseState.Parsed or HomeworkCourseState.Empty) return;
        foreach (var old in previous.Where(i => SameCourse(i, courseId, courseName))) Keep(old);
    }

    /// <summary>
    /// 部分成功：识别出的作业已经通过 <see cref="Add"/> 写入；旧缓存里「这次没识别出来」的条目按课程认领保留，
    /// 不能因为只解析出一部分就把整门课的旧数据丢掉。parsedAlertKeys 是这次识别到的条目键。
    /// </summary>
    public void CoursePartial(string courseId, string courseName, IReadOnlyCollection<string> parsedAlertKeys)
    {
        _states[HomeworkCourseState.Parsed] = _states.GetValueOrDefault(HomeworkCourseState.Parsed) + 1;
        foreach (var old in previous.Where(i => SameCourse(i, courseId, courseName)))
            if (!parsedAlertKeys.Contains(old.AlertKey))
                Keep(old);
    }

    /// <summary>某一条作业缺字段（比如截止时间读不出来）：沿用同一份作业的旧条目。返回是否找到了。</summary>
    public bool KeepItem(HomeworkItem fresh)
    {
        _states[HomeworkCourseState.MissingFields] = _states.GetValueOrDefault(HomeworkCourseState.MissingFields) + 1;
        var old = previous.FirstOrDefault(i => i.AlertKey == fresh.AlertKey
            || (i.Url.Length > 0 && i.Url == fresh.Url && i.Title == fresh.Title));
        if (old == null) return false;
        Keep(old);
        return true;
    }

    private void Keep(HomeworkItem old)
    {
        if (_kept.Add(old.Key + "\u0001" + old.AlertKey)) _items.Add(old);
    }

    /// <summary>日志用：「解析 3 / 确认为空 1 / 缺字段 1」。</summary>
    public string Summary() => string.Join(" / ", _states.Where(kv => kv.Value > 0).Select(kv => $"{Name(kv.Key)} {kv.Value}"));

    private static string Name(HomeworkCourseState s) => s switch
    {
        HomeworkCourseState.Parsed => "解析",
        HomeworkCourseState.Empty => "确认为空",
        HomeworkCourseState.MissingFields => "缺字段",
        HomeworkCourseState.Unparsed => "0 条解析",
        _ => "失败",
    };
}
