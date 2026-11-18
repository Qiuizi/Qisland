using Qisland.Core;

namespace Qisland.Core.Tests;

public class InfoStationTests
{
    [Fact]
    public void FeaturedUnread_Brief_AndDeadline()
    {
        var provider = new InfoStationProvider { MinScore = 80 };
        provider.ApplyItems("""
            {"items":[
              {"id":7,"title":"高分通知","summary":"奖学金","score":93,"source_name":"教务","is_read":false},
              {"id":8,"title":"已读","score":99,"is_read":true},
              {"id":9,"title":"低分","score":40,"is_read":false}
            ]}
            """);
        provider.ApplyBrief("""{"date":"2026-11-15","greeting":"早上好，有 2 条值得看。"}""", new DateOnly(2026, 11, 15));
        provider.ApplyDeadlines("""[{"id":3,"title":"提交报告","days_left":0.5,"source_name":"学校"}]""");

        var items = provider.Snapshot();
        Assert.Contains(items, a => a.Id == "info:item:7" && a.Body.Contains("93"));
        Assert.DoesNotContain(items, a => a.Title == "已读" || a.Title == "低分");
        Assert.Contains(items, a => a.Id == "info:brief:2026-11-15");
        Assert.Contains(items, a => a.Id == "info:deadline:3" && a.Kind == ActivityKind.Warning);
    }

    [Fact]
    public void YesterdayBrief_IsIgnored()
    {
        Assert.Null(InfoStationProvider.ReadBrief(
            """{"date":"2026-11-14","greeting":"昨天"}""", new DateOnly(2026, 11, 15)));
    }
}

public class CourseAndJarvisTests
{
    [Fact]
    public void Snapshot_SurfacesCheckInQuizAndHomework()
    {
        var activities = CourseDeskProvider.ReadSnapshot("""
            {
              "yuketang": {
                "onLesson": [{"lessonId":"L1","courseName":"高数"}],
                "lesson": {"courseName":"高数","problems":[
                  {"problemId":"P1","body":"求极限","submitState":"pending"},
                  {"problemId":"P2","body":"已交","submitState":"submitted"}
                ]},
                "homework": [{"id":"H1","title":"第三章","courseName":"高数","leftHours":5}]
              },
              "chaoxing": {
                "actives": [{"id":"A1","title":"二维码签到","courseName":"英语","ongoing":true}],
                "homework": [{"id":"H2","title":"下周","leftHours":80}]
              }
            }
            """);
        Assert.Contains(activities, a => a.Id == "course:sign:A1" && a.Kind == ActivityKind.WaitingApproval);
        Assert.Contains(activities, a => a.Id == "course:quiz:P1");
        Assert.DoesNotContain(activities, a => a.Id == "course:quiz:P2");
        Assert.Contains(activities, a => a.Id == "course:lesson:L1");
        Assert.Contains(activities, a => a.Id == "course:hw:H1");
        Assert.DoesNotContain(activities, a => a.Id == "course:hw:H2");
    }

    [Fact]
    public void Jarvis_UsesGenericContract()
    {
        var drafts = QislandIngest.Parse("""
            {"kind":"jarvis","id":"task-1","title":"整理实验报告","status":"running","body":"第 2 节"}
            """);
        Assert.Single(drafts);
        Assert.Equal("jarvis:task-1", drafts[0].Id);
        Assert.Equal("整理实验报告", drafts[0].Title);
        Assert.Equal("info", drafts[0].Type);
    }
}

public class NotificationAndWidgetTests
{
    [Fact]
    public void Privacy_HidesWeChatAndQq()
    {
        var policy = new NotificationPolicy(true, true, true, Privacy: true);
        var wechat = NotificationMirror.Present(ChatApp.WeChat, "张三", "晚上打球吗", policy);
        Assert.True(wechat.Show);
        Assert.Equal("微信", wechat.Title);
        Assert.Equal("新消息", wechat.Body);

        var off = NotificationMirror.Present(ChatApp.QQ, "李四", "在吗", new NotificationPolicy(true, true, false, false));
        Assert.False(off.Show);
        Assert.Equal(ChatApp.QQ, NotificationMirror.Detect("QQ", "Tencent.QQ"));
        Assert.Equal(ChatApp.DeepSeekHarness, NotificationMirror.Detect("DeepSeek Harness", ""));
    }

    [Fact]
    public void Weather_Timer_Todo_AndPriority()
    {
        var weather = WeatherParser.ParseForecast("""
            {"current":{"temperature_2m":18.2,"weather_code":2,"apparent_temperature":17,"relative_humidity_2m":60,"wind_speed_10m":8,"precipitation":0},
             "daily":{"temperature_2m_max":[22],"temperature_2m_min":[14]}}
            """);
        Assert.NotNull(weather);
        Assert.Equal("上海 18° 多云 14/22°", WeatherParser.FormatCompact(weather!, "上海"));

        var engine = new TimerEngine();
        var start = new DateTime(2026, 11, 15, 9, 0, 0);
        engine.StartCountdown(TimeSpan.FromSeconds(2), start);
        Assert.False(engine.Tick(start.AddSeconds(1)));
        Assert.True(engine.Tick(start.AddSeconds(2)));
        Assert.Equal(TimerMode.Idle, engine.Mode);

        engine.StartPomodoro(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), start);
        Assert.True(engine.Tick(start.AddMinutes(1)));
        Assert.Equal(TimerMode.PomodoroBreak, engine.Mode);

        var dir = Path.Combine(Path.GetTempPath(), "qisland-todo-" + Guid.NewGuid().ToString("N"));
        var store = new TodoStore(Path.Combine(dir, "todos.json"));
        var item = store.Add("交实验");
        Assert.True(store.Toggle(item.Id));
        var again = new TodoStore(Path.Combine(dir, "todos.json"));
        Assert.True(again.Items[0].Done);

        var ranked = ActivityPriority.Rank(new[]
        {
            new IslandActivity("weather:cd", "weather", "18°", "", ActivityKind.Info, 12),
            new IslandActivity("agent:claude-code:s", "claude-code", "等待确认", "", ActivityKind.WaitingApproval, 100),
        });
        Assert.Equal("agent:claude-code:s", ranked[0].Id);
    }

    [Fact]
    public void Wsl_MirroredThenNatHost()
    {
        var bases = WslEndpoint.CandidateBases("nameserver 172.22.0.1\n", "default via 172.22.0.1 dev eth0", 9840);
        Assert.Equal("http://127.0.0.1:9840", bases[0]);
        Assert.Equal("http://172.22.0.1:9840", bases[1]);
    }

    [Fact]
    public void Ingest_ClaudeHook_BecomesPush()
    {
        var drafts = QislandIngest.Parse("""{"hook_event_name":"Notification","session_id":"abc","message":"需要你看一眼"}""");
        Assert.Single(drafts);
        Assert.Equal("warning", drafts[0].Type);
        Assert.Equal("high", drafts[0].Priority);
        Assert.False(string.IsNullOrWhiteSpace(drafts[0].Title));
    }
}
