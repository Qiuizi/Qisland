using System.Globalization;

namespace Qisland.Core;

/// <summary>
/// 健康提醒的设置：喝水（每隔多少分钟、一杯多少、每天目标）和久坐（连续用电脑多少分钟），
/// 只在提醒时段内提醒，午休时段免打扰。默认值参考岛仔 Daozai（MIT）：45 分钟一杯 250 ml、目标 2000 ml、
/// 9–22 点、久坐 60 分钟、午休 12:00–13:30。C# 重写。
/// </summary>
public sealed record HealthSettings(
    int WaterMinutes = 45, int ServingMl = 250, int GoalMl = 2000,
    int StartHour = 9, int EndHour = 22, int SitMinutes = 60,
    bool LunchQuiet = true, int LunchStartMin = 720, int LunchEndMin = 810,
    int BreakMinutes = 5)
{
    public bool InWindow(DateTime t)
    {
        var m = t.Hour * 60 + t.Minute;
        if (t.Hour < StartHour || t.Hour >= EndHour) return false;
        return !(LunchQuiet && m >= LunchStartMin && m < LunchEndMin);
    }
}

/// <summary>今天的喝水记录：总量、每小时分量（给柱状图）、上一杯的时间。跨天自动清零。</summary>
public sealed class WaterLog
{
    public string Day { get; private set; } = "";
    public int TotalMl { get; private set; }
    public int[] Hours { get; } = new int[24];
    public DateTime LastDrink { get; private set; } = DateTime.MinValue;

    public void Roll(DateTime now)
    {
        var d = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (d == Day) return;
        Day = d; TotalMl = 0; Array.Clear(Hours); LastDrink = DateTime.MinValue;
    }

    public void Drink(int ml, DateTime now)
    {
        Roll(now);
        ml = Math.Clamp(ml, -TotalMl, 2000);
        TotalMl += ml;
        Hours[now.Hour] = Math.Max(0, Hours[now.Hour] + ml);
        if (ml > 0) LastDrink = now;
    }

    public double Progress(int goal) => goal <= 0 ? 0 : Math.Clamp(TotalMl / (double)goal, 0, 1);

    /// <summary>day=yyyy-MM-dd / total / last=HH:mm / hours=逗号分隔 24 个数。</summary>
    public string Serialize() =>
        $"day={Day}\ntotal={TotalMl}\nlast={(LastDrink == DateTime.MinValue ? "" : LastDrink.ToString("HH:mm", CultureInfo.InvariantCulture))}\nhours={string.Join(',', Hours)}\n";

    public static WaterLog Parse(string? text, DateTime now)
    {
        var w = new WaterLog();
        var kv = (text ?? "").Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (kv.TryGetValue("day", out var d) && d == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            w.Day = d;
            if (kv.TryGetValue("total", out var t) && int.TryParse(t, out var n)) w.TotalMl = Math.Max(0, n);
            if (kv.TryGetValue("hours", out var h))
            {
                var parts = h.Split(',');
                for (var i = 0; i < 24 && i < parts.Length; i++) if (int.TryParse(parts[i], out var x)) w.Hours[i] = Math.Max(0, x);
            }
            if (kv.TryGetValue("last", out var l) && TimeSpan.TryParseExact(l, @"hh\:mm", CultureInfo.InvariantCulture, out var ts)) w.LastDrink = now.Date + ts;
        }
        else w.Roll(now);
        return w;
    }
}

/// <summary>什么时候该提醒。纯函数，方便测试。</summary>
public static class HealthClock
{
    /// <summary>
    /// 该提醒喝水了吗：在提醒时段内、今天还没喝够，而且距离上一杯（或今天提醒时段开始 / 上次提醒）超过间隔。
    /// </summary>
    public static bool WaterDue(HealthSettings s, WaterLog log, DateTime lastNudge, DateTime now)
    {
        if (!s.InWindow(now) || log.TotalMl >= s.GoalMl) return false;
        var start = now.Date.AddHours(s.StartHour);
        var since = new[] { log.LastDrink, lastNudge, start }.Max();
        return (now - since).TotalMinutes >= s.WaterMinutes;
    }

    /// <summary>
    /// 连续使用时长：键鼠空闲超过 BreakMinutes 就算起身休息过，从那之后重新计时。
    /// activeSince = 本轮开始使用的时刻；idle = 当前已经空闲多久。返回新的 activeSince。
    /// </summary>
    public static DateTime TrackActivity(HealthSettings s, DateTime activeSince, TimeSpan idle, DateTime now) =>
        idle.TotalMinutes >= s.BreakMinutes || activeSince == DateTime.MinValue ? now : activeSince;

    public static bool SitDue(HealthSettings s, DateTime activeSince, DateTime lastNudge, TimeSpan idle, DateTime now)
    {
        if (!s.InWindow(now) || idle.TotalMinutes >= 1 || activeSince == DateTime.MinValue) return false;
        var since = lastNudge > activeSince ? lastNudge : activeSince;
        return (now - since).TotalMinutes >= s.SitMinutes;
    }
}
