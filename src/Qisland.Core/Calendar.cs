using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>日历订阅里的一次具体日程（重复的已经展开）。</summary>
public sealed record CalEvent(string Uid, string Title, string Location, DateTime Start, DateTime End, bool AllDay, string? JoinUrl, string? JoinApp)
{
    /// <summary>订阅 ID；订阅是链接的稳定哈希，本机日程固定为 local。</summary>
    public string SourceId { get; init; } = "";

    /// <summary>这一次在原始日程中的开始时间；RECURRENCE-ID 改期时保留原定时间。</summary>
    public DateTime? OriginalStart { get; init; }

    /// <summary>去重、活动 ID 和提醒索引共用这一个身份。</summary>
    public string Key => CalendarIdentity.Key(SourceId, Uid, Title, Location, Start, OriginalStart);
}

/// <summary>日程身份：订阅 + UID + 原始开始时间（秒级 UTC）；没有 UID 才用内容哈希。</summary>
public static class CalendarIdentity
{
    public static string FeedId(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    public static string Key(string sourceId, string uid, string title, string location, DateTime start, DateTime? originalStart)
    {
        if (string.IsNullOrWhiteSpace(uid))
        {
            var raw = $"{title.Trim()}\n{Utc(originalStart ?? start):yyyyMMdd'T'HHmmss'Z'}\n{location.Trim()}";
            return "t:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }

        var identity = Utc(originalStart ?? start);
        return $"u:{sourceId}:{identity.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}:{uid}";
    }

    private static DateTime Utc(DateTime time) => time.Kind switch
    {
        DateTimeKind.Utc => time,
        DateTimeKind.Local => time.ToUniversalTime(),
        _ => DateTime.SpecifyKind(time, DateTimeKind.Local).ToUniversalTime(),
    };
}

/// <summary>
/// 解析 .ics 日历订阅（Outlook / Google / 飞书 / 钉钉导出的「私密地址」都是这个格式）。
/// 在课表解析的基础上多认：UID、URL、STATUS:CANCELLED、全天日程、RECURRENCE-ID 改期的单次。
/// 纯数据，可在 Linux 上单测。
/// </summary>
public static class CalendarIcs
{
    private sealed class Ev
    {
        public string Uid = "", Summary = "", Location = "", Desc = "", Url = "", Rrule = "", Status = "";
        public DateTime? Start, End, RecurrenceId;
        public TimeSpan? Duration;
        public DateTime StartWall;
        public TimeZoneInfo? StartZone;
        public bool AllDay;
        public readonly HashSet<DateTime> ExDates = [];
        public readonly HashSet<DateTime> RDates = [];
    }

    /// <param name="local">换算到哪个时区显示（默认本机时区；单测里指定）。</param>
    public static List<CalEvent> Expand(string ics, DateTime from, DateTime to, TimeZoneInfo? local = null, string sourceId = "")
    {
        local ??= TimeZoneInfo.Local;
        var evs = Parse(ics, local).ToList();
        // 改期 / 取消的单次：从主日程的展开里拿掉，改期后的那条单独算
        var moved = evs.Where(e => e.RecurrenceId != null).Select(e => (e.Uid, e.RecurrenceId!.Value)).ToHashSet();
        var list = new List<CalEvent>();
        foreach (var e in evs)
        {
            if (e.Start is not { } s || e.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            // 没有 DTEND 时按 RFC 5545 用 DURATION（有的订阅源只给时长）；两样都没有：全天 = 一天，定时 = 1 小时
            var end = e.End ?? (e.Duration is { } du ? s + du : e.AllDay ? s.AddDays(1) : s.AddHours(1));
            var dur = end - s;
            if (dur < TimeSpan.Zero || dur > TimeSpan.FromDays(14)) dur = TimeSpan.FromHours(1);
            var join = MeetingLink.Find(e.Url, e.Location, e.Desc);
            IEnumerable<DateTime> occ = e.RecurrenceId != null
                ? new[] { s }
                : Timetable.OccurrencesLocal(s, e.StartWall, e.StartZone, e.Rrule, from.AddDays(-1), to, local)
                    .Concat(e.RDates)
                    .Distinct();
            foreach (var st in occ)
            {
                if (e.ExDates.Contains(st)) continue;
                if (e.RecurrenceId == null && moved.Contains((e.Uid, st))) continue;
                var en = st + dur;
                if (en <= from || st >= to) continue;
                list.Add(new CalEvent(e.Uid, Clean(e.Summary), Clean(e.Location), st, en, e.AllDay, join?.Url, join?.App)
                {
                    SourceId = sourceId,
                    OriginalStart = e.RecurrenceId ?? st,
                });
            }
        }
        return Dedupe(list);
    }

    /// <summary>
    /// 去重：有 UID 的按 UID + 这一次的开始时间（RECURRENCE-ID 改期后的单次和主日程同 UID、不同时间，不会互相吞掉）；
    /// 没有 UID 才退回 标题 + 开始 + 地点。同名同时、UID 或地点不同的两个日程都保留。
    /// </summary>
    public static List<CalEvent> Dedupe(IEnumerable<CalEvent> events) =>
        events.GroupBy(c => c.Key, StringComparer.Ordinal)
            .Select(g => g.First()).OrderBy(c => c.Start).ToList();

    private static IEnumerable<Ev> Parse(string ics, TimeZoneInfo local)
    {
        Ev? cur = null;
        var depth = 0;   // VEVENT 里嵌的 VALARM 不能覆盖日程本身的字段
        foreach (var line in Timetable.Unfold(ics))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new Ev(); depth = 0; continue; }
            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur != null) yield return cur; cur = null; continue; }
            if (cur == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { depth = Math.Max(0, depth - 1); continue; }
            if (depth > 0) continue;
            var colon = Timetable.IndexOfValueColon(line);
            if (colon < 0) continue;
            var head = line[..colon];
            var val = line[(colon + 1)..];
            var semi = head.IndexOf(';');
            var name = (semi >= 0 ? head[..semi] : head).ToUpperInvariant();
            var pars = semi >= 0 ? head[(semi + 1)..] : "";
            switch (name)
            {
                case "UID": cur.Uid = val.Trim(); break;
                case "SUMMARY": cur.Summary = Timetable.Unescape(val); break;
                case "LOCATION": cur.Location = Timetable.Unescape(val); break;
                case "DESCRIPTION": cur.Desc = Timetable.Unescape(val); break;
                case "URL": cur.Url = val.Trim(); break;
                case "STATUS": cur.Status = val.Trim(); break;
                case "RRULE": cur.Rrule = val; break;
                case "DTSTART":
                    cur.Start = Timetable.ParseDate(val, pars, local);
                    if (Timetable.ParseZoned(val, pars) is { } z) { cur.StartWall = z.wall; cur.StartZone = z.zone; }
                    cur.AllDay = val.Trim().Length == 8 || pars.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) && !pars.Contains("DATE-TIME", StringComparison.OrdinalIgnoreCase);
                    break;
                case "DTEND": cur.End = Timetable.ParseDate(val, pars, local); break;
                case "DURATION": cur.Duration = ParseDuration(val); break;
                case "RECURRENCE-ID": cur.RecurrenceId = Timetable.ParseDate(val, pars, local); break;
                case "EXDATE":
                    foreach (var v in val.Split(',')) if (Timetable.ParseDate(v, pars, local) is { } d) cur.ExDates.Add(d);
                    break;
                case "RDATE":
                    foreach (var v in val.Split(',')) if (Timetable.ParseDate(v, pars, local) is { } d) cur.RDates.Add(d);
                    break;
                case "X-MICROSOFT-SKYPETEAMSMEETINGURL":
                case "X-GOOGLE-CONFERENCE":
                    if (cur.Url.Length == 0) cur.Url = val.Trim();
                    break;
            }
        }
    }

    /// <summary>RFC 5545 的时长：P1W、PT1H30M、P1DT2H、-PT15M（负的不认）。认不出返回 null。</summary>
    public static TimeSpan? ParseDuration(string v)
    {
        var m = Regex.Match(v.Trim(), @"^\+?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$", RegexOptions.IgnoreCase);
        if (!m.Success || v.Trim().TrimStart('+').Length <= 1 || v.Trim().EndsWith("T", StringComparison.OrdinalIgnoreCase)) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        var t = new TimeSpan(G(1) * 7 + G(2), G(3), G(4), G(5));
        return t > TimeSpan.Zero ? t : null;
    }

    private static string Clean(string s) => Regex.Replace(s.Replace('\n', ' '), @"\s+", " ").Trim();
}

/// <summary>从日程的链接 / 地点 / 描述里认会议入口：腾讯会议、Zoom、Teams、Google Meet、飞书、钉钉。</summary>
public static class MeetingLink
{
    private static readonly (string App, Regex Re)[] Patterns =
    [
        ("腾讯会议", new Regex(@"https?://(?:meeting\.tencent\.com|voovmeeting\.com|work\.weixin\.qq\.com/meeting)/[^\s""'<>，。）)]+", RegexOptions.IgnoreCase)),
        ("Zoom", new Regex(@"https?://(?:[\w-]+\.)?(?:zoom\.us|zoom\.com\.cn|zoom\.com)/(?:j|my|w|s)/[^\s""'<>，。）)]+", RegexOptions.IgnoreCase)),
        ("Teams", new Regex(@"https?://teams\.(?:microsoft|live)\.com/(?:l/meetup-join|meet)/[^\s""'<>，。）)]+", RegexOptions.IgnoreCase)),
        ("Google Meet", new Regex(@"https?://meet\.google\.com/[a-z]{3}-[a-z]{4}-[a-z]{3}[^\s""'<>，。）)]*", RegexOptions.IgnoreCase)),
        ("飞书", new Regex(@"https?://(?:vc\.feishu\.cn|[\w-]+\.feishu\.cn/(?:vc|j)|vc\.larksuite\.com|[\w-]+\.larksuite\.com/(?:vc|j))/[^\s""'<>，。）)]+", RegexOptions.IgnoreCase)),
        ("钉钉", new Regex(@"(?:https?://(?:meeting\.dingtalk\.com|h5\.dingtalk\.com/[^\s]*?meeting)[^\s""'<>，。）)]*|dingtalk://dingtalkclient/[^\s""'<>]+)", RegexOptions.IgnoreCase)),
    ];

    // 只有会议号的腾讯会议：「会议号：123 456 789」「#腾讯会议：123-456-789」
    private static readonly Regex TencentCode = new(@"腾讯会议[^0-9\n]{0,12}(\d{3}[\s-]?\d{3}[\s-]?\d{3,4})|会议号[:：\s]*(\d{3}[\s-]?\d{3}[\s-]?\d{3,4})", RegexOptions.IgnoreCase);

    public static (string App, string Url)? Find(params string?[] texts)
    {
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            foreach (var (app, re) in Patterns)
            {
                var m = re.Match(t);
                if (m.Success) return (app, m.Value.TrimEnd('.', ',', ';', '>', ']'));
            }
        }
        foreach (var t in texts)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var m = TencentCode.Match(t);
            if (m.Success)
            {
                var code = Regex.Replace(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, @"\D", "");
                return ("腾讯会议", "wemeet://page/inmeeting?meeting_code=" + code);
            }
        }
        return null;
    }
}
