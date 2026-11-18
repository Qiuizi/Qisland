using Qisland.Core;

namespace Qisland.Core.Tests;

public class CalendarTests
{
    private const string Ics = """
        BEGIN:VCALENDAR
        BEGIN:VEVENT
        UID:a1
        SUMMARY:组会
        DTSTART;TZID=Asia/Shanghai:20261118T140000
        DTEND;TZID=Asia/Shanghai:20261118T150000
        LOCATION:腾讯会议
        DESCRIPTION:点击链接入会：https://meeting.tencent.com/dm/AbCdEf123\n#腾讯会议：123-456-789
        BEGIN:VALARM
        DESCRIPTION:Reminder
        TRIGGER:-PT15M
        END:VALARM
        END:VEVENT
        BEGIN:VEVENT
        UID:w1
        SUMMARY:周会
        DTSTART:20261116T020000Z
        DTEND:20261116T030000Z
        RRULE:FREQ=WEEKLY;BYDAY=MO,WE
        EXDATE:20261123T020000Z
        DESCRIPTION:Join: https://teams.microsoft.com/l/meetup-join/19%3ameeting_x%40thread.v2/0?context=abc
        END:VEVENT
        BEGIN:VEVENT
        UID:w1
        RECURRENCE-ID:20261125T020000Z
        SUMMARY:周会（改期）
        DTSTART:20261125T060000Z
        DTEND:20261125T070000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:c1
        SUMMARY:取消了
        STATUS:CANCELLED
        DTSTART:20261118T090000
        END:VEVENT
        BEGIN:VEVENT
        UID:d1
        SUMMARY:运动会
        DTSTART;VALUE=DATE:20261119
        DTEND;VALUE=DATE:20261120
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public void Expands_Recurring_Overrides_Cancelled_AllDay()
    {
        var evs = CalendarIcs.Expand(Ics, new DateTime(2026, 11, 16), new DateTime(2026, 11, 30));
        Assert.DoesNotContain(evs, e => e.Title == "取消了");
        var grp = Assert.Single(evs, e => e.Title == "组会");
        Assert.Equal("腾讯会议", grp.JoinApp);
        Assert.StartsWith("https://meeting.tencent.com/dm/AbCdEf123", grp.JoinUrl);
        Assert.Equal(new DateTime(2026, 11, 18, 14, 0, 0), grp.Start);
        Assert.Equal("腾讯会议", grp.Location);
        var weekly = evs.Where(e => e.Title == "周会").ToList();
        // 11/16 一、11/18 三、11/23 一被 EXDATE、11/25 三被改期、11/30 不在范围
        Assert.Equal(2, weekly.Count);
        Assert.All(weekly, w => Assert.Equal("Teams", w.JoinApp));
        Assert.Contains(evs, e => e.Title == "周会（改期）");
        var allDay = Assert.Single(evs, e => e.Title == "运动会");
        Assert.True(allDay.AllDay);
    }

    [Theory]
    [InlineData("https://zoom.us/j/123456789?pwd=x", "Zoom")]
    [InlineData("https://meet.google.com/abc-defg-hij", "Google Meet")]
    [InlineData("https://vc.feishu.cn/j/123456789", "飞书")]
    [InlineData("会议号：987 654 321", "腾讯会议")]
    public void FindsMeetingLinks(string text, string app)
    {
        var m = MeetingLink.Find(text);
        Assert.NotNull(m);
        Assert.Equal(app, m!.Value.App);
    }

    [Fact]
    public void NoLink_ReturnsNull() => Assert.Null(MeetingLink.Find("教学楼 A201", null, ""));
}

public class RruleTests
{
    private static List<DateTime> Run(string start, string rrule, string from = "20260101", string to = "20280101")
    {
        var s = Timetable.ParseDate(start, "")!.Value;
        return Timetable.Occurrences(s, rrule, Timetable.ParseDate(from, "")!.Value, Timetable.ParseDate(to, "")!.Value).ToList();
    }

    [Fact]
    public void Monthly_SecondTuesday_Count()
    {
        var r = Run("20261110T190000", "FREQ=MONTHLY;BYDAY=2TU;COUNT=3");
        Assert.Equal([new DateTime(2026, 11, 10, 19, 0, 0), new DateTime(2026, 12, 8, 19, 0, 0), new DateTime(2027, 1, 12, 19, 0, 0)], r);
    }

    [Fact]
    public void Monthly_LastFriday_Until()
    {
        var r = Run("20261127T100000", "FREQ=MONTHLY;BYDAY=-1FR;UNTIL=20270201T000000Z");
        Assert.Equal([new DateTime(2026, 11, 27, 10, 0, 0), new DateTime(2026, 12, 25, 10, 0, 0), new DateTime(2027, 1, 29, 10, 0, 0)], r);
    }

    [Fact]
    public void Monthly_31st_SkipsShortMonths_AndLastDay()
    {
        var r = Run("20270131T090000", "FREQ=MONTHLY;COUNT=3");
        Assert.Equal([new DateTime(2027, 1, 31, 9, 0, 0), new DateTime(2027, 3, 31, 9, 0, 0), new DateTime(2027, 5, 31, 9, 0, 0)], r);
        var last = Run("20270131T090000", "FREQ=MONTHLY;BYMONTHDAY=-1;COUNT=2");
        Assert.Equal(new DateTime(2027, 2, 28, 9, 0, 0), last[1]);
    }

    [Fact]
    public void Monthly_Interval_And_LastWeekday_SetPos()
    {
        var r = Run("20261130T080000", "FREQ=MONTHLY;INTERVAL=2;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=2");
        Assert.Equal([new DateTime(2026, 11, 30, 8, 0, 0), new DateTime(2027, 1, 29, 8, 0, 0)], r);
    }

    [Fact]
    public void Yearly_Birthday_And_Feb29()
    {
        Assert.Equal(3, Run("20261118", "FREQ=YEARLY", "20260101", "20290101").Count);
        var leap = Run("20280229", "FREQ=YEARLY;COUNT=2", "20280101", "20400101");
        Assert.Equal([new DateTime(2028, 2, 29), new DateTime(2032, 2, 29)], leap);
        var thanks = Run("20261126", "FREQ=YEARLY;BYMONTH=11;BYDAY=4TH;COUNT=2", "20260101", "20300101");
        Assert.Equal(new DateTime(2027, 11, 25), thanks[1]);
    }

    [Fact]
    public void Weekly_And_Daily_StillWork()
    {
        Assert.Equal(4, Run("20261116T080000", "FREQ=WEEKLY;BYDAY=MO,WE;COUNT=4").Count);
        Assert.Equal(5, Run("20261116T080000", "FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR", "20261116", "20261123").Count);
    }
}
