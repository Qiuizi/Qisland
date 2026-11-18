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
/// RRULE 支持 WEEKLY / DAILY 的 INTERVAL、COUNT、UNTIL、BYDAY，够覆盖所有课表导出。纯数据，可在 Linux 上单测。
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

    internal static IEnumerable<DateTime> Occurrences(DateTime start, string rrule, DateTime from, DateTime to)
    {
        if (string.IsNullOrWhiteSpace(rrule)) { yield return start; yield break; }
        var r = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.Ordinal);
        var freq = r.GetValueOrDefault("FREQ", "WEEKLY").ToUpperInvariant();
        var interval = int.TryParse(r.GetValueOrDefault("INTERVAL"), out var iv) && iv > 0 ? iv : 1;
        int? count = int.TryParse(r.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTime? until = r.TryGetValue("UNTIL", out var u) ? ParseDate(u, "") : null;
        if (until is { } uu && uu.TimeOfDay == TimeSpan.Zero && u!.Length == 8) until = uu.AddDays(1).AddTicks(-1);
        var byDay = r.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(x => x.Length >= 2 ? x[^2..].ToUpperInvariant() : "").Select(Day).Where(x => x != null).Select(x => x!.Value).ToHashSet()
            : null;
        var n = 0;
        if (freq == "DAILY")
        {
            for (var t = start; ; t = t.AddDays(interval))
            {
                if (until is { } un && t > un || count is { } cc && n >= cc || t >= to) yield break;
                n++;
                if (t >= from.AddDays(-1)) yield return t;
            }
        }
        if (freq != "WEEKLY") { yield return start; yield break; }
        var days = byDay is { Count: > 0 } ? byDay : [start.DayOfWeek];
        var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));   // 周一
        for (var w = weekStart; ; w = w.AddDays(7 * interval))
        {
            if (w >= to) yield break;
            for (var k = 0; k < 7; k++)
            {
                var day = w.AddDays(k);
                if (!days.Contains(day.DayOfWeek)) continue;
                var t = day + start.TimeOfDay;
                if (t < start) continue;
                if (until is { } un && t > un || count is { } cc && n >= cc) yield break;
                n++;
                if (t >= from.AddDays(-1) && t < to) yield return t;
            }
        }
    }

    private static DayOfWeek? Day(string s) => s switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };
}
