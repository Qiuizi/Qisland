using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>
/// 待办的提醒时间和每周重复，直接写在 todo.md 那一行里，记事本里也看得懂、改得了：
/// <c>- [ ] 交实验报告 ⏰ 2026-11-21 20:00 🔁 每周五</c>。
/// 带重复的待办勾掉时不进「已完成」，而是把 ⏰ 挪到下一次。思路参考小糖岛仔（MIT）的待办提醒 / 每周重复，C# 重写。
/// </summary>
public sealed record TodoMeta(string Title, DateTime? Due, IReadOnlyList<DayOfWeek> Repeat)
{
    public const string DueMark = "⏰", RepeatMark = "🔁";
    private const string Days = "日一二三四五六";
    private static readonly Regex DueRx = new(@"\s*⏰\s*(\d{4}-\d{1,2}-\d{1,2})(?:\s+(\d{1,2}):(\d{2}))?", RegexOptions.Compiled);
    private static readonly Regex RepeatRx = new(@"\s*🔁\s*(?:每周|周)?([一二三四五六日天]+)", RegexOptions.Compiled);

    public bool Repeats => Repeat.Count > 0;

    public static TodoMeta Parse(string text)
    {
        DateTime? due = null;
        var repeat = new List<DayOfWeek>();
        var t = text;
        var m = DueRx.Match(t);
        if (m.Success)
        {
            if (DateTime.TryParseExact(m.Groups[1].Value, ["yyyy-M-d", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var hh) && int.TryParse(m.Groups[3].Value, out var mm) && hh < 24 && mm < 60)
                    d = d.AddHours(hh).AddMinutes(mm);
                else d = d.AddHours(9);   // 只写日期：当天早上 9 点提醒
                due = d;
            }
            t = t.Remove(m.Index, m.Length);
        }
        var r = RepeatRx.Match(t);
        if (r.Success)
        {
            foreach (var c in r.Groups[1].Value)
            {
                var i = c == '天' ? 0 : Days.IndexOf(c);
                if (i >= 0 && !repeat.Contains((DayOfWeek)i)) repeat.Add((DayOfWeek)i);
            }
            repeat.Sort((a, b) => Order(a).CompareTo(Order(b)));
            t = t.Remove(r.Index, r.Length);
        }
        return new TodoMeta(t.Trim(), due, repeat);
    }

    /// <summary>周一排最前（中文习惯），周日最后。</summary>
    private static int Order(DayOfWeek d) => d == DayOfWeek.Sunday ? 7 : (int)d;

    /// <summary>写回 todo.md 的一行文字（不含 “- [ ] ”）。</summary>
    public string ToText()
    {
        var sb = new StringBuilder(Title.Replace(DueMark, "").Replace(RepeatMark, "").Trim());
        if (Due is { } d) sb.Append(' ').Append(DueMark).Append(' ').Append(d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (Repeats) sb.Append(' ').Append(RepeatMark).Append(" 每周").Append(string.Concat(Repeat.Select(x => Days[(int)x])));
        return sb.ToString();
    }

    /// <summary>after 之后最近的一次（星期在 days 里、时刻 = time）。</summary>
    public static DateTime NextWeekly(DateTime after, IReadOnlyCollection<DayOfWeek> days, TimeSpan time)
    {
        if (days.Count == 0) throw new ArgumentException("没有选星期", nameof(days));
        for (var i = 0; i <= 7; i++)
        {
            var c = after.Date.AddDays(i) + time;
            if (c > after && days.Contains(c.DayOfWeek)) return c;
        }
        return after.Date.AddDays(8) + time;   // 不会走到这里
    }

    /// <summary>
    /// 勾掉这一条：不重复的就是完成（返回 null）；每周重复的返回挪到下一次的新版本（还是未完成）。
    /// 下一次从「现在」和「原定时间」里较晚的那个往后找，提前做完本周的也会跳到下周。
    /// </summary>
    public TodoMeta? Complete(DateTime now)
    {
        if (!Repeats) return null;
        var time = Due?.TimeOfDay ?? TimeSpan.FromHours(9);
        var from = Due is { } d && d > now ? d : now;
        return this with { Due = NextWeekly(from, Repeat, time) };
    }

    /// <summary>上次检查（since）之后、现在之前到点的。</summary>
    public bool DueBetween(DateTime since, DateTime now) => Due is { } d && d > since && d <= now;

    /// <summary>「今天 20:00」「明天 08:00」「周五 20:00」「11/23 20:00」，重复的再加「· 每周五」。</summary>
    public string When(DateTime now)
    {
        var parts = new List<string>();
        if (Due is { } d)
        {
            var day = (d.Date - now.Date).Days switch
            {
                0 => "今天",
                1 => "明天",
                -1 => "昨天",
                > 1 and < 7 => "周" + Days[(int)d.DayOfWeek],
                _ => d.ToString(d.Year == now.Year ? "M/d" : "yyyy/M/d", CultureInfo.InvariantCulture),
            };
            parts.Add($"{day} {d:HH:mm}");
        }
        if (Repeats) parts.Add("每周" + string.Concat(Repeat.Select(x => Days[(int)x])));
        return string.Join(" · ", parts);
    }

    public bool Overdue(DateTime now) => Due is { } d && d < now;
}
