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
