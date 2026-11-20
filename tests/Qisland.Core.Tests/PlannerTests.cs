using Qisland.Core;
using Xunit;

namespace Qisland.Core.Tests;

public class TodoMetaTests
{
    [Fact]
    public void Parse_TitleDueRepeat_AndRoundTrip()
    {
        var m = TodoMeta.Parse("交实验报告 ⏰ 2026-11-21 20:00 🔁 每周五一");
        Assert.Equal("交实验报告", m.Title);
        Assert.Equal(new DateTime(2026, 11, 21, 20, 0, 0), m.Due);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], m.Repeat);
        Assert.Equal("交实验报告 ⏰ 2026-11-21 20:00 🔁 每周一五", m.ToText());
        Assert.Equal(m, TodoMeta.Parse(m.ToText()) with { Repeat = m.Repeat });
    }

    [Fact]
    public void Parse_PlainText_And_DateOnly()
    {
        var p = TodoMeta.Parse("买牛奶");
        Assert.Equal("买牛奶", p.Title); Assert.Null(p.Due); Assert.False(p.Repeats);
        Assert.Equal("买牛奶", p.ToText());
        var d = TodoMeta.Parse("体检 ⏰ 2026-12-1");
        Assert.Equal(new DateTime(2026, 12, 1, 9, 0, 0), d.Due);
    }

    [Fact]
    public void NextWeekly_SkipsToNextMatchingDay()
    {
        var fri = new DateTime(2026, 11, 20, 21, 0, 0); // 周五 21:00，已过 20:00
        Assert.Equal(new DateTime(2026, 11, 23, 20, 0, 0), TodoMeta.NextWeekly(fri, [DayOfWeek.Monday, DayOfWeek.Friday], TimeSpan.FromHours(20)));
        var early = new DateTime(2026, 11, 20, 19, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 20, 20, 0, 0), TodoMeta.NextWeekly(early, [DayOfWeek.Friday], TimeSpan.FromHours(20)));
    }

    [Fact]
    public void Complete_RepeatingMovesDue_PlainFinishes()
    {
        var now = new DateTime(2026, 11, 20, 18, 0, 0);
        var m = TodoMeta.Parse("背单词 ⏰ 2026-11-20 20:00 🔁 每周五");
        var next = m.Complete(now)!;
        Assert.Equal(new DateTime(2026, 11, 27, 20, 0, 0), next.Due);   // 提前做完本周的 → 下周
        Assert.Null(TodoMeta.Parse("一次性 ⏰ 2026-11-20 20:00").Complete(now));
    }

    [Fact]
    public void DueBetween_And_When()
    {
        var now = new DateTime(2026, 11, 20, 20, 0, 30);
        var m = TodoMeta.Parse("x ⏰ 2026-11-20 20:00");
        Assert.True(m.DueBetween(now.AddMinutes(-1), now));
        Assert.False(m.DueBetween(now, now.AddMinutes(1)));
        Assert.Equal("今天 20:00", m.When(now));
        Assert.Equal("明天 08:00 · 每周六", TodoMeta.Parse("y ⏰ 2026-11-21 08:00 🔁 周六").When(now));
    }
}

public class LocalCalendarTests
{
    private static LocalEvent Ev(string id, DateTime s, double hours, bool weekly = false) => new(id, "组会", "", s, s.AddHours(hours), false, weekly);

    [Fact]
    public void Upsert_Validates_Sorts_AndUpdates()
    {
        var c = new LocalCalendar();
        Assert.False(c.Upsert(new LocalEvent("a", " ", "", DateTime.Today, DateTime.Today.AddHours(1), false)));
        Assert.False(c.Upsert(new LocalEvent("a", "反了", "", DateTime.Today.AddHours(2), DateTime.Today.AddHours(1), false)));
        Assert.True(c.Upsert(Ev("b", new DateTime(2026, 11, 22, 10, 0, 0), 1)));
        Assert.True(c.Upsert(Ev("a", new DateTime(2026, 11, 21, 10, 0, 0), 1)));
        Assert.Equal("a", c.Items[0].Id);
        Assert.True(c.Upsert(c.Items[0] with { Title = "改名" }));
        Assert.Equal(2, c.Count);
        Assert.Equal("改名", c.Find("a")!.Title);
        Assert.True(c.Delete("a")); Assert.False(c.Delete("a"));
    }

    [Fact]
    public void Expand_Weekly_InWindow_WithUntil()
    {
        var c = new LocalCalendar();
        c.Upsert(Ev("w", new DateTime(2026, 9, 7, 14, 0, 0), 2, weekly: true) with { Until = new DateTime(2026, 12, 1) });
        var list = c.Expand(new DateTime(2026, 11, 20), new DateTime(2026, 12, 20)).ToList();
        Assert.Equal([new DateTime(2026, 11, 23, 14, 0, 0), new DateTime(2026, 11, 30, 14, 0, 0)], list.Select(e => e.Start));
        Assert.All(list, e => Assert.Equal("w", LocalCalendar.IdFromUid(e.Uid)));
        Assert.Null(LocalCalendar.IdFromUid("abc@google.com"));
    }

    [Fact]
    public void Expand_OneOff_OverlapOnly_AndJsonRoundTrip()
    {
        var c = new LocalCalendar();
        c.Upsert(Ev("o", new DateTime(2026, 11, 19, 23, 0, 0), 2));
        Assert.Single(c.Expand(new DateTime(2026, 11, 20), new DateTime(2026, 11, 21)));
        Assert.Empty(c.Expand(new DateTime(2026, 11, 21), new DateTime(2026, 11, 22)));
        var d = LocalCalendar.FromJson(c.ToJson());
        Assert.Equal(c.Items, d.Items);
        Assert.Equal(0, LocalCalendar.FromJson("[{oops").Count);
    }
}

public class IslandLayoutTests
{
    [Fact]
    public void Rest_TopSideMinimal()
    {
        Assert.Equal((176d, 34d, 17d), IslandLayout.Rest(DockSide.Top, false, 176, 34));
        Assert.Equal((IslandLayout.SideW, IslandLayout.SideH, IslandLayout.SideW / 2), IslandLayout.Rest(DockSide.Left, false, 176, 34));
        var m = IslandLayout.Rest(DockSide.Top, true, 176, 34);
        Assert.True(m.W > m.H && m.H <= 6);
        var s = IslandLayout.Rest(DockSide.Right, true, 176, 34);
        Assert.True(s.H > s.W);
    }

    [Fact]
    public void Origin_AnchorsToEdge()
    {
        Assert.Equal((252d, 10d), IslandLayout.Origin(DockSide.Top, 680, 600, 10, 176, 34));
        Assert.Equal((IslandLayout.SideInset, 252d), IslandLayout.Origin(DockSide.Left, 680, 600, 10, 40, 96));
        var r = IslandLayout.Origin(DockSide.Right, 680, 600, 10, 452, 300);
        Assert.Equal(680 - 452 - IslandLayout.SideInset, r.X);
        Assert.Equal(150, r.Y);
        Assert.True(IslandLayout.HideShift(DockSide.Left, 0, 40, 96, 10).X < 0);
        Assert.True(IslandLayout.HideShift(DockSide.Right, 0, 40, 96, 10).X > 0);
        Assert.Equal((0d, 0d), IslandLayout.HideShift(DockSide.Top, 1, 40, 96, 10));
    }

    [Fact]
    public void Pref_RoundTrip_DefaultsTop()
    {
        Assert.Equal((DockSide.Top, false), IslandLayout.Parse(null));
        Assert.Equal((DockSide.Right, true), IslandLayout.Parse(IslandLayout.Serialize(DockSide.Right, true)));
        Assert.Equal((DockSide.Top, false), IslandLayout.Parse("dock=diagonal\nminimal=nah"));
    }

    [Fact]
    public void HealthSettings_RoundTrip_AndClamp()
    {
        var h = new HealthSettings(WaterMinutes: 30, GoalMl: 2500, SitMinutes: 50, LunchQuiet: false);
        Assert.Equal(h, HealthSettings.Parse(h.Serialize()));
        var bad = HealthSettings.Parse("water_minutes=1\ngoal_ml=999999\nstart_hour=22\nend_hour=3");
        Assert.Equal(10, bad.WaterMinutes); Assert.Equal(6000, bad.GoalMl);
        Assert.True(bad.EndHour > bad.StartHour);
    }
}

public class FrameStatsTests
{
    [Fact]
    public void CountsSlowFramesAndExpected()
    {
        var s = new FrameStats();
        s.Begin();
        s.Frame(null);
        for (var i = 0; i < 59; i++) s.Frame(1.0 / 120);
        s.Frame(0.05);
        s.Ui(1); s.Ui(3);
        s.Count("rgn"); s.Count("rgn");
        Assert.Equal(1, s.Slow(120));
        Assert.Equal(60, s.Frames);
        Assert.Equal(2, s.Events("rgn"));
        Assert.Equal(2, s.UiAvgMs, 3);
        Assert.Equal(50, s.DtMaxMs, 3);
        var line = s.End("Expanded", 120);
        Assert.Contains("掉帧 1", line);
        Assert.Contains("rgn=2", line);
        Assert.Contains("anim → Expanded: 61 帧", line);
    }

    [Fact]
    public void SlowThresholdNeverBelow12_5ms()
    {
        Assert.Equal(0.0125, FrameStats.SlowThreshold(240), 6);
        Assert.Equal(0.025, FrameStats.SlowThreshold(60), 6);
        Assert.Equal(0.0125, FrameStats.SlowThreshold(120), 6);
    }

    [Fact]
    public void IgnoresWhenNotRunning()
    {
        var s = new FrameStats();
        s.Frame(0.1); s.Count("x");
        Assert.Equal(0, s.Frames);
        Assert.Equal(0, s.Events("x"));
        Assert.Equal(0, FrameStats.Percentile(Array.Empty<double>(), 0.95));
    }
}

public class GlassPrefTests
{
    [Theory]
    [InlineData(null, GlassMode.System)]
    [InlineData("", GlassMode.System)]
    [InlineData(" Capture \r\n", GlassMode.Capture)]
    [InlineData("off", GlassMode.Off)]
    [InlineData("weird", GlassMode.System)]
    public void Parses(string? text, GlassMode want) => Assert.Equal(want, GlassPref.Parse(text));

    [Fact]
    public void RoundTrips()
    {
        foreach (var m in Enum.GetValues<GlassMode>()) Assert.Equal(m, GlassPref.Parse(GlassPref.Serialize(m)));
    }

    [Theory]
    [InlineData(GlassMode.System, 26100, true, GlassRender.SystemBlur)]
    [InlineData(GlassMode.System, 26100, false, GlassRender.SystemSolid)]
    [InlineData(GlassMode.System, 19045, true, GlassRender.Capture)]
    [InlineData(GlassMode.System, 19045, false, GlassRender.None)]
    [InlineData(GlassMode.Capture, 26100, true, GlassRender.Capture)]
    [InlineData(GlassMode.Capture, 26100, false, GlassRender.None)]
    [InlineData(GlassMode.Off, 26100, true, GlassRender.None)]
    public void Resolves(GlassMode mode, int build, bool transparency, GlassRender want) => Assert.Equal(want, GlassPref.Resolve(mode, build, transparency));
}
