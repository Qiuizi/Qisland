using System.Globalization;

namespace Qisland.App;

/// <summary>农历日期与传统节日，用 .NET 自带的 ChineseLunisolarCalendar 计算（覆盖 1901–2100），不联网。</summary>
public static class Lunar
{
    private static readonly ChineseLunisolarCalendar Cal = new();
    private static readonly string[] Months = ["正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊"];
    private static readonly string[] Tens = ["初", "十", "廿", "三"];
    private static readonly string[] Digits = ["一", "二", "三", "四", "五", "六", "七", "八", "九", "十"];

    /// <summary>(农历月, 日, 是否闰月)；超出范围返回 null。</summary>
    public static (int Month, int Day, bool Leap)? Of(DateTime d)
    {
        if (d < Cal.MinSupportedDateTime || d > Cal.MaxSupportedDateTime) return null;
        var y = Cal.GetYear(d);
        var m = Cal.GetMonth(d);
        var day = Cal.GetDayOfMonth(d);
        var leapMonth = Cal.GetLeapMonth(y);   // 0 = 无闰月；否则该序号的月份是闰月
        var leap = leapMonth > 0 && m == leapMonth;
        if (leapMonth > 0 && m >= leapMonth) m--;
        return (m, day, leap);
    }

    public static string DayName(int day) => day switch
    {
        10 => "初十",
        20 => "二十",
        30 => "三十",
        _ => Tens[day / 10] + Digits[(day - 1) % 10],
    };

    public static string MonthName(int m, bool leap) => (leap ? "闰" : "") + Months[m - 1] + "月";

    /// <summary>如「十月廿七」。</summary>
    public static string Text(DateTime d) => Of(d) is { } l ? MonthName(l.Month, l.Leap) + DayName(l.Day) : "";

    /// <summary>传统节日；没有就 null。</summary>
    public static string? Festival(DateTime d)
    {
        if (Of(d) is not { } l || l.Leap) return null;
        if (Of(d.AddDays(1)) is { Month: 1, Day: 1, Leap: false }) return "除夕";
        return (l.Month, l.Day) switch
        {
            (1, 1) => "春节",
            (1, 15) => "元宵",
            (2, 2) => "龙抬头",
            (5, 5) => "端午",
            (7, 7) => "七夕",
            (7, 15) => "中元",
            (8, 15) => "中秋",
            (9, 9) => "重阳",
            (12, 8) => "腊八",
            (12, 23) => "小年",
            _ => null,
        };
    }
}
