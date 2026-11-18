using System.Globalization;
using System.Text;

namespace Qisland.Core;

/// <summary>一节具体的课（已经按周次展开到某一天）。</summary>
public sealed record ClassSession(string Name, string Location, string Teacher, DateTime Start, DateTime End)
{
    public string ShortName => Name.Length > 8 ? Name[..8] : Name;
}

/// <summary>
/// 解析课表 ICS（SCU Plus / WakeUp / 不高山上导出的都行）。
/// 只认 VEVENT 的 SUMMARY / LOCATION / DESCRIPTION / DTSTART / DTEND / RRULE / EXDATE，
/// RRULE 支持 DAILY / WEEKLY / MONTHLY / YEARLY（见 Occurrences），课表导出和日历订阅都够用。纯数据，可在 Linux 上单测。
/// </summary>
public static class Timetable
{
    private sealed class Ev
    {
        public string Summary = "", Location = "", Desc = "", Rrule = "";
        public DateTime? Start, End;
        public readonly HashSet<DateTime> ExDates = [];
    }

    public static IReadOnlyList<ClassSession> Expand(string ics, DateTime from, DateTime to)
    {
        var list = new List<ClassSession>();
        foreach (var e in Parse(ics))
        {
            if (e.Start is not { } s) continue;
            var dur = (e.End ?? s.AddMinutes(45)) - s;
            if (dur <= TimeSpan.Zero || dur > TimeSpan.FromHours(12)) dur = TimeSpan.FromMinutes(45);
            foreach (var st in Occurrences(s, e.Rrule, from.AddHours(-12), to))
            {
                if (e.ExDates.Contains(st)) continue;
                var en = st + dur;
                if (en <= from || st >= to) continue;
                list.Add(new ClassSession(Clean(e.Summary), Clean(e.Location), Teacher(e.Desc), st, en));
            }
        }
        // 同一时间同名的重复事件（有的导出器每周各写一条又带 RRULE）只留一条
        return list.GroupBy(c => (c.Name, c.Start)).Select(g => g.First()).OrderBy(c => c.Start).ToList();
    }

    private static IEnumerable<Ev> Parse(string ics)
    {
        Ev? cur = null;
        foreach (var line in Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new Ev(); continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur != null) yield return cur; cur = null; continue; }
            if (cur == null) continue;
            var colon = IndexOfValueColon(line);
            if (colon < 0) continue;
            var head = line[..colon];
            var val = line[(colon + 1)..];
            var semi = head.IndexOf(';');
            var name = (semi >= 0 ? head[..semi] : head).ToUpperInvariant();
            var pars = semi >= 0 ? head[(semi + 1)..] : "";
            switch (name)
            {
                case "SUMMARY": cur.Summary = Unescape(val); break;
                case "LOCATION": cur.Location = Unescape(val); break;
                case "DESCRIPTION": cur.Desc = Unescape(val); break;
                case "RRULE": cur.Rrule = val; break;
                case "DTSTART": cur.Start = ParseDate(val, pars); break;
                case "DTEND": cur.End = ParseDate(val, pars); break;
                case "EXDATE":
                    foreach (var v in val.Split(',')) if (ParseDate(v, pars) is { } d) cur.ExDates.Add(d);
                    break;
            }
        }
    }

    // 参数里可能有带引号的冒号（TZID="..."），值的冒号是第一个不在引号里的
    internal static int IndexOfValueColon(string line)
    {
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') q = !q;
            else if (line[i] == ':' && !q) return i;
        }
        return -1;
    }

    internal static IEnumerable<string> Unfold(string ics)
    {
        var sb = new StringBuilder();
        foreach (var raw in ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')) { sb.Append(raw, 1, raw.Length - 1); continue; }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear().Append(raw.TrimEnd());
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    internal static string Unescape(string v) => v.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static string Clean(string s) => s.Replace('\n', ' ').Trim();

    // 描述里常见「教师：张三」「老师: 张三」或第一行就是老师名
    private static string Teacher(string desc)
    {
        foreach (var l in desc.Split('\n'))
        {
            var t = l.Trim();
            foreach (var k in new[] { "教师", "老师", "任课教师", "Teacher" })
                if (t.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                    return t[k.Length..].TrimStart(':', '：', ' ').Trim();
        }
        return "";
    }

    public static DateTime? ParseDate(string v, string pars)
    {
        v = v.Trim();
        var utc = v.EndsWith('Z');
        if (utc) v = v[..^1];
        if (v.Length == 8 && DateTime.TryParseExact(v, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (!DateTime.TryParseExact(v, ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
        if (utc) return DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime();
        // TZID：课表几乎都是 Asia/Shanghai；其它时区就按系统时区换算，找不到就当本地时间
        var tz = pars.Split(';').FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))?[5..].Trim('"');
        if (!string.IsNullOrEmpty(tz) && tz is not ("Asia/Shanghai" or "China Standard Time" or "Asia/Chongqing" or "PRC"))
        {
            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), zone, TimeZoneInfo.Local);
            }
            catch { }
        }
        else if (!string.IsNullOrEmpty(tz))
        {
            var off = TimeZoneInfo.Local.GetUtcOffset(t) - TimeSpan.FromHours(8);
            return t + off;
        }
        return t;
    }

    /// <summary>
    /// 按 RRULE 展开开始时间。支持 DAILY / WEEKLY / MONTHLY / YEARLY，INTERVAL、COUNT、UNTIL，
    /// BYDAY（含序号：2MO 第二个周一、-1FR 最后一个周五）、BYMONTHDAY（含负数：-1 月末）、BYMONTH、BYSETPOS。
    /// COUNT 从第一次算起（包括 from 之前的），和 RFC 5545 一致；EXDATE 由调用方过滤。
    /// </summary>
    internal static IEnumerable<DateTime> Occurrences(DateTime start, string rrule, DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(rrule)) { yield return start; yield break; }
        var r = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.Ordinal);
        var freq = r.GetValueOrDefault("FREQ", "WEEKLY").ToUpperInvariant();
        var interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) && c > 0 ? c : null;
        DateTime? until = r.TryGetValue("UNTIL", out var u) ? ParseDate(u, "") : null;
        if (until is { } uu && uu.TimeOfDay == TimeSpan.Zero && u!.Length == 8) until = uu.AddDays(1).AddTicks(-1);
        var byDay = r.TryGetValue("BYDAY", out var bd) ? ParseByDay(bd) : [];
        var byMonthDay = Ints(r.GetValueOrDefault("BYMONTHDAY")).Where(x => x != 0 && x >= -31 && x <= 31).ToList();
        var byMonth = Ints(r.GetValueOrDefault("BYMONTH")).Where(x => x is >= 1 and <= 12).ToList();
        var bySetPos = Ints(r.GetValueOrDefault("BYSETPOS")).Where(x => x != 0).ToList();
        var n = 0;
        var periods = 0;
        foreach (var day in Candidates(start, freq, interval, byDay, byMonthDay, byMonth, bySetPos))
        {
            var t = day.Date + start.TimeOfDay;
            if (t < start) continue;
            if (until is { } un && t > un || count is { } cc && n >= cc || t >= to) yield break;
            n++;
            if (t >= from.AddDays(-1)) yield return t;
            if (++periods > 20000) yield break;   // 坏数据兜底
        }
    }

    private static IEnumerable<DateTime> Candidates(DateTime start, string freq, int interval, List<(int ord, DayOfWeek day)> byDay,
        List<int> byMonthDay, List<int> byMonth, List<int> bySetPos)
    {
        var d0 = start.Date;
        switch (freq)
        {
            case "DAILY":
                for (var k = 0; k < 200000; k++)
                {
                    var d = d0.AddDays((long)k * interval);
                    if (byMonth.Count > 0 && !byMonth.Contains(d.Month)) continue;
                    if (byDay.Count > 0 && !byDay.Any(b => b.day == d.DayOfWeek)) continue;
                    if (byMonthDay.Count > 0 && !MatchesMonthDay(d, byMonthDay)) continue;
                    yield return d;
                }
                yield break;
            case "WEEKLY":
                var days = byDay.Count > 0 ? byDay.Select(b => b.day).ToHashSet() : [start.DayOfWeek];
                var weekStart = d0.AddDays(-(((int)d0.DayOfWeek + 6) % 7));   // 周一
                for (var k = 0; k < 20000; k++)
                {
                    var w = weekStart.AddDays(7L * interval * k);
                    for (var i = 0; i < 7; i++)
                    {
                        var d = w.AddDays(i);
                        if (days.Contains(d.DayOfWeek) && (byMonth.Count == 0 || byMonth.Contains(d.Month))) yield return d;
                    }
                }
                yield break;
            case "MONTHLY":
                var m0 = new DateTime(d0.Year, d0.Month, 1);
                for (var k = 0; k < 6000; k++)
                {
                    var m = m0.AddMonths(k * interval);
                    if (byMonth.Count > 0 && !byMonth.Contains(m.Month)) continue;
                    foreach (var d in InMonth(m, start, byDay, byMonthDay, bySetPos)) yield return d;
                }
                yield break;
            case "YEARLY":
                for (var k = 0; k < 500; k++)
                {
                    var y = d0.Year + k * interval;
                    if (y > 9998) yield break;
                    var months = byMonth.Count > 0 ? byMonth.OrderBy(x => x).ToList() : [start.Month];
                    var set = new List<DateTime>();
                    foreach (var mo in months)
                    {
                        var m = new DateTime(y, mo, 1);
                        // 只有 BYMONTH 没有日子：用开始那天的日期（2 月 29 日在平年就没有）
                        set.AddRange(InMonth(m, start, byDay, byMonthDay, []));
                    }
                    set.Sort();
                    foreach (var d in ApplySetPos(set, bySetPos)) yield return d;
                }
                yield break;
            default:
                yield return d0;
                yield break;
        }
    }

    private static List<DateTime> InMonth(DateTime month, DateTime start, List<(int ord, DayOfWeek day)> byDay, List<int> byMonthDay, List<int> bySetPos)
    {
        var dim = DateTime.DaysInMonth(month.Year, month.Month);
        var set = new List<DateTime>();
        if (byMonthDay.Count > 0)
        {
            foreach (var md in byMonthDay)
            {
                var day = md > 0 ? md : dim + md + 1;
                if (day >= 1 && day <= dim) set.Add(new DateTime(month.Year, month.Month, day));
            }
            if (byDay.Count > 0) set = set.Where(d => byDay.Any(b => b.day == d.DayOfWeek)).ToList();
        }
        else if (byDay.Count > 0)
        {
            foreach (var (ord, dow) in byDay)
            {
                var all = Enumerable.Range(1, dim).Select(i => new DateTime(month.Year, month.Month, i)).Where(d => d.DayOfWeek == dow).ToList();
                if (ord == 0) set.AddRange(all);
                else
                {
                    var idx = ord > 0 ? ord - 1 : all.Count + ord;
                    if (idx >= 0 && idx < all.Count) set.Add(all[idx]);
                }
            }
        }
        else if (start.Day <= dim) set.Add(new DateTime(month.Year, month.Month, start.Day));   // 31 号在小月里没有，按 RFC 跳过
        set = set.Distinct().OrderBy(d => d).ToList();
        return ApplySetPos(set, bySetPos);
    }

    private static List<DateTime> ApplySetPos(List<DateTime> set, List<int> bySetPos)
    {
        if (bySetPos.Count == 0 || set.Count == 0) return set;
        var res = new List<DateTime>();
        foreach (var p in bySetPos)
        {
            var i = p > 0 ? p - 1 : set.Count + p;
            if (i >= 0 && i < set.Count) res.Add(set[i]);
        }
        return res.Distinct().OrderBy(d => d).ToList();
    }

    private static bool MatchesMonthDay(DateTime d, List<int> byMonthDay)
    {
        var dim = DateTime.DaysInMonth(d.Year, d.Month);
        return byMonthDay.Any(md => (md > 0 ? md : dim + md + 1) == d.Day);
    }

    private static List<(int ord, DayOfWeek day)> ParseByDay(string v)
    {
        var list = new List<(int, DayOfWeek)>();
        foreach (var raw in v.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var x = raw.Trim().ToUpperInvariant();
            if (x.Length < 2 || Day(x[^2..]) is not { } d) continue;
            var ord = x.Length > 2 && int.TryParse(x[..^2], out var o) ? o : 0;
            list.Add((ord, d));
        }
        return list;
    }

    private static IEnumerable<int> Ints(string? v) =>
        (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x.Trim(), out var i) ? (int?)i : null).Where(x => x != null).Select(x => x!.Value);

    private static DayOfWeek? Day(string s) => s switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };
}
