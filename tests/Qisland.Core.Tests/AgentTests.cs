using Qisland.Core;

namespace Qisland.Core.Tests;

public class AgentEventTests
{
    [Fact]
    public void ClaudePermissionRequest_WaitsForApproval()
    {
        var activity = AgentEventParser.Parse("""
            {"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"C:\\\\src\\\\app","tool_name":"Bash"}
            """);
        Assert.NotNull(activity);
        Assert.Equal("agent:claude-code:s1", activity!.Id);
        Assert.Equal(ActivityKind.WaitingApproval, activity.Kind);
        Assert.Contains("Claude Code", activity.Title);
        Assert.Contains("Bash", activity.Body);
    }

    [Fact]
    public void ClaudeStop_IsDone()
    {
        var activity = AgentEventParser.Parse("""{"hook_event_name":"Stop","session_id":"s1"}""");
        Assert.Equal(ActivityKind.Done, activity!.Kind);
        Assert.True(activity.ResetTtl);
    }

    [Fact]
    public void CodexApproval_AndComplete()
    {
        var waiting = AgentEventParser.Parse("""{"type":"exec-approval-request","thread-id":"t9","command":"git push"}""");
        Assert.Equal(ActivityKind.WaitingApproval, waiting!.Kind);
        Assert.Equal("codex", waiting.Source);
        Assert.Contains("git push", waiting.Body);

        var done = AgentEventParser.Parse("""{"type":"agent-turn-complete","thread-id":"t9","last-assistant-message":"已改完"}""");
        Assert.Equal(ActivityKind.Done, done!.Kind);
        Assert.Equal("agent:codex:t9", done.Id);
        Assert.Contains("已改完", done.Body);
    }

    [Fact]
    public void NormalizedDeepSeek_DoesNotRequireCliHook()
    {
        var activity = AgentEventParser.Parse("""
            {"source":"deepseek-harness","session_id":"desk","status":"waiting_approval","title":"需要批准","detail":"写入 README","quota":{"used":3,"limit":20,"unit":"requests"}}
            """);
        Assert.Equal(ActivityKind.WaitingApproval, activity!.Kind);
        Assert.Equal("3/20 requests", activity.Subtitle);
    }

    [Fact]
    public void QuotaFile_ReadsClaudeAndSkipsLevelDbPath()
    {
        var items = AgentQuotaReader.ReadJson("""{"claude-code":{"used":12,"limit":100,"unit":"5h"}}""");
        Assert.Single(items);
        Assert.Contains("12/100", items[0].Body);

        var dir = Path.Combine(Path.GetTempPath(), "qisland-quota-" + Guid.NewGuid().ToString("N"), "IndexedDB");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "quota.json");
        File.WriteAllText(file, """{"claude-code":{"used":1,"limit":2}}""");
        Assert.Empty(AgentQuotaReader.ReadFile(file));
    }
}

public class DeepSeekHarnessTests
{
    [Theory]
    [InlineData("DeepSeek Harness", true)]
    [InlineData("DeepSeekHarness", true)]
    [InlineData("chrome", false)]
    [InlineData("deepseek", false)]
    public void ProcessName(string name, bool expected) =>
        Assert.Equal(expected, DeepSeekHarnessSignals.IsHarnessProcess(name));

    [Fact]
    public void WindowTitle_Approval_Error_Done_AndExit()
    {
        var waiting = DeepSeekHarnessSignals.FromWindow(true, "DeepSeek Harness — 等待确认：删除文件");
        Assert.Equal(ActivityKind.WaitingApproval, waiting!.Kind);
        Assert.Contains("删除文件", waiting.Body);

        var failed = DeepSeekHarnessSignals.FromWindow(true, "任务失败");
        Assert.Equal(ActivityKind.Error, failed!.Kind);

        var done = DeepSeekHarnessSignals.FromWindow(true, "任务已完成");
        Assert.Equal(ActivityKind.Done, done!.Kind);

        Assert.Null(DeepSeekHarnessSignals.FromWindow(false, "任务已完成"));
    }

    [Fact]
    public void Privacy_HidesWindowTitleAndToastBody()
    {
        var window = DeepSeekHarnessSignals.FromWindow(true, "等待确认：cat ~/.ssh/id_rsa", privacy: true);
        Assert.DoesNotContain("ssh", window!.Body);
        Assert.Equal("等待确认", window.Body);

        var toast = DeepSeekHarnessSignals.FromToast("需要你确认", "要执行 rm -rf /tmp/build", privacy: true);
        Assert.DoesNotContain("rm -rf", toast!.Body);
    }

    [Fact]
    public void Toast_InfersDone()
    {
        var toast = DeepSeekHarnessSignals.FromToast("DeepSeek Harness", "这一轮已完成");
        Assert.Equal(ActivityKind.Done, toast!.Kind);
        Assert.True(DeepSeekHarnessSignals.IsHarnessToast("DeepSeek Harness", "com.deepseek.dsh-desktop"));
    }
}
