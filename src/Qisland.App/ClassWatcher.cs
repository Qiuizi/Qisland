using Qisland.Core;
using System.IO;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 课表：读 %APPDATA%\Qisland\timetable.ics（托盘「导入课表」会把选中的 .ics 复制过来），
/// 每 30 秒算一次「上课中 / 下一节」，以实时活动的形式上岛。不碰任何教务账号。
/// </summary>
public sealed class ClassWatcher : IDisposable
{
    public static string PathName => Path.Combine(App.DataDir, "timetable.ics");
    public const string Source = "class";
    /// <summary>提前多久开始在岛上显示下一节课。</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMinutes(30);

    private readonly ActivityStore _store;
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private string _ics = "";
    private DateTime _builtFor = DateTime.MinValue;
    private IReadOnlyList<ClassSession> _week = [];
    private readonly HashSet<string> _live = [];
    private bool _soon;
    private DateTime _shownDay;

    public event Action? Changed;
    public bool HasTimetable => _ics.Length > 0;
    public ClassSession? Current { get; private set; }
    public ClassSession? Next { get; private set; }

    /// <summary>demoIcs 不为空时只用这段文本（截图演示用），不读文件也不监视。</summary>
    public ClassWatcher(ActivityStore store, string? demoIcs = null)
    {
        _store = store;
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(); };
        if (demoIcs != null) { _ics = demoIcs; Tick(); return; }
        _watcher = new FileSystemWatcher(App.DataDir, "timetable.ics") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Created += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Renamed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        Load();
    }

    private void Kick() { _debounce.Stop(); _debounce.Start(); }

    public static bool Import(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllText(PathName, text);
            return true;
        }
        catch (Exception ex) { App.Log("导入课表: " + ex.Message); return false; }
    }

    private void Load()
    {
        try { _ics = File.Exists(PathName) ? File.ReadAllText(PathName) : ""; }
        catch (IOException) { Kick(); return; }
        _builtFor = DateTime.MinValue;
        Tick();
        if (_ics.Length > 0) App.Log($"课表: 本周 {_week.Count} 节");
    }

    /// <summary>今天剩下（含正在上）的课。</summary>
    public IReadOnlyList<ClassSession> TodayLeft()
    {
        var now = DateTime.Now;
        return _week.Where(c => c.Start.Date == now.Date && c.End > now).ToList();
    }

    public IReadOnlyList<ClassSession> Day(DateTime d) => _week.Where(c => c.Start.Date == d.Date).ToList();

    public void Tick()
    {
        var now = DateTime.Now;
        if (_builtFor != now.Date)
        {
            _builtFor = now.Date;
            try { _week = _ics.Length == 0 ? [] : Timetable.Expand(_ics, now.Date, now.Date.AddDays(8)); }
            catch (Exception ex) { App.Log("课表解析: " + ex.Message); _week = []; }
        }
        var cur = _week.FirstOrDefault(c => c.Start <= now && c.End > now);
        var next = _week.FirstOrDefault(c => c.Start > now);
        var soon = next != null && next.Start - now <= TimeSpan.FromMinutes(10);
        var changed = cur != Current || next != Next || soon != _soon || _builtFor != _shownDay;
        _soon = soon; _shownDay = _builtFor;
        Current = cur; Next = next;

        var acts = new List<(IslandActivity a, double? p)>();
        if (cur != null)
        {
            var left = (int)Math.Ceiling((cur.End - now).TotalMinutes);
            var body = Join(cur.Location, $"还剩 {left} 分钟");
            acts.Add((new IslandActivity("class:now", Source, cur.Name, body, ActivityKind.Running, 55), (now - cur.Start) / (cur.End - cur.Start)));
        }
        if (next != null && next.Start - now <= Lead && (cur == null || next.Start - cur.End <= TimeSpan.FromMinutes(20)))
        {
            var mins = (int)Math.Ceiling((next.Start - now).TotalMinutes);
            var urgent = mins <= 10;
            var body = Join($"{mins} 分钟后 · {next.Start:HH:mm}", next.Location);
            acts.Add((new IslandActivity("class:next", Source, "下一节 " + next.Name, body, urgent ? ActivityKind.Warning : ActivityKind.Info, urgent ? 80 : 60), null));
        }
        // 自己维护 id：上课中的进度要带上，且不让进度变化以外的东西刷新
        var ids = acts.Select(x => x.a.Id).ToHashSet();
        foreach (var gone in _live.Where(i => !ids.Contains(i)).ToList()) { _store.Remove(gone); _live.Remove(gone); }
        foreach (var (a, p) in acts) { _store.Upsert(a, sticky: true, progress: p is { } pp ? Math.Clamp(pp, 0, 1) : null); _live.Add(a.Id); }
        if (changed) Changed?.Invoke();
    }

    private static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    public void Dispose() => _watcher?.Dispose();

    /// <summary>以「现在」为基准造一份演示课表：一节正在上，一节 20 分钟后开始，晚上还有一节。</summary>
    public static string DemoIcs()
    {
        var n = DateTime.Now;
        static string F(DateTime t) => t.ToString("yyyyMMdd'T'HHmmss");
        string Ev(string name, string loc, DateTime s, int min) =>
            $"BEGIN:VEVENT\r\nSUMMARY:{name}\r\nLOCATION:{loc}\r\nDESCRIPTION:教师：示例\r\nDTSTART;TZID=Asia/Shanghai:{F(s)}\r\nDTEND;TZID=Asia/Shanghai:{F(s.AddMinutes(min))}\r\nEND:VEVENT\r\n";
        var start = new DateTime(n.Year, n.Month, n.Day, n.Hour, n.Minute, 0);
        return "BEGIN:VCALENDAR\r\n" + Ev("离散数学", "综合楼C102", start.AddMinutes(-70), 95) + Ev("数据结构", "一教A201", start.AddMinutes(45), 95)
            + Ev("概率论", "二教B305", start.AddHours(4), 95) + "END:VCALENDAR\r\n";
    }
}
