using System.Text;
using System.Text.Json.Nodes;
using Qisland.Core;

namespace Qisland.Core.Tests;

public class AiTests
{
    [Theory]
    [InlineData("Hello world, this is a test.", "en", "zh-CN")]
    [InlineData("今天天气不错", "zh-CN", "en")]
    [InlineData("这个 API 的 rate limit 是多少", "zh-CN", "en")]
    [InlineData("The quick brown fox jumps over the lazy dog 狗", "en", "zh-CN")]
    [InlineData("ありがとうございます", "ja", "zh-CN")]
    [InlineData("안녕하세요", "ko", "zh-CN")]
    public void Direction_PicksSourceAndTarget(string text, string from, string to) =>
        Assert.Equal((from, to), AiCatalog.Direction(text));

    [Fact]
    public void Chunk_SplitsAtSentenceEnds_AndKeepsEverything()
    {
        var text = string.Concat(Enumerable.Repeat("This is one sentence. ", 60));
        var chunks = AiCatalog.Chunk(text, 200);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 200));
        Assert.All(chunks.SkipLast(1), c => Assert.EndsWith(".", c));
        Assert.Equal(text.Replace(" ", ""), string.Concat(chunks).Replace(" ", ""));
    }

    [Fact]
    public void Chunk_HardCutsWithoutPunctuation()
    {
        var chunks = AiCatalog.Chunk(new string('字', 1000), 480);
        Assert.Equal([480, 480, 40], chunks.Select(c => c.Length));
    }

    [Theory]
    [InlineData(AiEngine.DeepSeek, "https://api.deepseek.com/chat/completions", true)]
    [InlineData(AiEngine.DeepSeek, "http://api.deepseek.com/chat/completions", false)]
    [InlineData(AiEngine.DeepSeek, "https://evil.example.com/chat/completions", false)]
    [InlineData(AiEngine.DeepSeek, "https://user:pw@api.deepseek.com/chat/completions", false)]
    [InlineData(AiEngine.Kimi, "https://api.moonshot.cn/v1/chat/completions", true)]
    [InlineData(AiEngine.Ollama, "http://127.0.0.1:11434/api/chat", true)]
    [InlineData(AiEngine.Ollama, "http://192.168.1.5:11434/api/chat", false)]
    [InlineData(AiEngine.Ollama, "https://127.0.0.1:11434/api/chat", false)]
    public void EndpointAllowed_OnlyOfficialHosts(AiEngine e, string url, bool ok) => Assert.Equal(ok, AiCatalog.EndpointAllowed(e, url));

    [Fact]
    public void AllCatalogEndpointsAreAllowed() =>
        Assert.All(AiCatalog.All, i => Assert.True(AiCatalog.EndpointAllowed(i.Engine, i.Endpoint), i.Id));

    [Fact]
    public void ParseMyMemory_DecodesEntities_AndReportsQuota()
    {
        Assert.Equal("你好 & 再见", AiCatalog.ParseMyMemory("""{"responseStatus":200,"responseData":{"translatedText":"你好 &amp; 再见"}}"""));
        Assert.Throws<AiException>(() => AiCatalog.ParseMyMemory("""{"responseStatus":429,"responseData":{"translatedText":"MYMEMORY WARNING"}}"""));
    }

    [Fact]
    public void ParseChat_OpenAiAndOllama_StripsThinking()
    {
        Assert.Equal("Hello", AiCatalog.ParseChat(AiEngine.DeepSeek, 200, """{"choices":[{"message":{"content":"<think>hmm</think>Hello"}}]}"""));
        Assert.Equal("你好", AiCatalog.ParseChat(AiEngine.Ollama, 200, """{"message":{"role":"assistant","content":"你好"}}"""));
    }

    [Fact]
    public void ParseChat_ErrorsNeverEchoServerText()
    {
        var ex = Assert.Throws<AiException>(() => AiCatalog.ParseChat(AiEngine.Kimi, 401, """{"error":{"message":"sk-secret invalid"}}"""));
        Assert.DoesNotContain("sk-", ex.Message);
        Assert.Throws<AiException>(() => AiCatalog.ParseChat(AiEngine.Kimi, 500, "oops"));
        Assert.Throws<AiException>(() => AiCatalog.ParseChat(AiEngine.Kimi, 200, "{}"));
    }

    [Fact]
    public void ChatBody_HasSystemAndUser()
    {
        var o = JsonNode.Parse(AiCatalog.ChatBody(AiEngine.Qwen, "qwen-plus", "sys", "hi"))!;
        Assert.Equal("qwen-plus", o["model"]!.ToString());
        Assert.Equal("user", o["messages"]![1]!["role"]!.ToString());
        Assert.False(o["stream"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("Ctrl+Alt+T", true, "Ctrl+Alt+T")]
    [InlineData("alt + ctrl + k", true, "Ctrl+Alt+K")]
    [InlineData("Ctrl+Shift+F2", true, "Ctrl+Shift+F2")]
    [InlineData("Shift+T", false, "")]
    [InlineData("T", false, "")]
    [InlineData("Ctrl+A+B", false, "")]
    [InlineData("", false, "")]
    public void Hotkey_ParseAndFormat(string text, bool ok, string formatted)
    {
        Assert.Equal(ok, HotkeyText.TryParse(text, out var m, out var vk));
        if (ok) Assert.Equal(formatted, HotkeyText.Format(m, vk));
    }
}

public class DoubaoQuotaTests
{
    private const string Sample = """
        {"code":0,"data":{
          "current_subscription":{"display":{"short_name":"豆包 Pro"}},
          "window_limit_section":{"window_limit_groups":[
            {"feature_group_name":"","window_limits":[
              {"window_type":1,"used_percent":42.5,"end_time":1900000000},
              {"window_type":2,"used_amount":30,"total_amount":120,"end_time":1900000000000}]},
            {"window_limits":[{"window_type":1,"used_percent":10}]}]},
          "incentive_quota_section":{"window_limit_groups":[{"window_limits":[{"window_type":3,"usage_exhausted":true}]}]}}}
        """;

    [Fact]
    public void Parse_PicksTightestPerWindow_AndPlan()
    {
        var s = DoubaoQuotaParser.Parse(Sample);
        Assert.Equal("Pro", s.Plan);
        Assert.Equal(2, s.Windows.Count);
        Assert.Equal("5 小时", s.Windows[0].Label);
        Assert.Equal(42.5, s.Windows[0].UsedPercent);
        Assert.Equal(25, s.Windows[1].UsedPercent, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1900000000).LocalDateTime, s.Windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("""{"code":710012001,"msg":"not login"}""", true)]
    [InlineData("""{"code":0}""", true)]
    [InlineData("""{"code":5,"msg":"x"}""", false)]
    [InlineData("<html>", false)]
    public void Parse_Errors(string json, bool login)
    {
        var ex = Assert.Throws<DoubaoQuotaParser.QuotaException>(() => DoubaoQuotaParser.Parse(json));
        Assert.Equal(login, ex.Login);
    }

    [Fact]
    public void Cookie_RequiresSingleHexSessionId()
    {
        var sid = new string('a', 32);
        Assert.Equal($"a=1; sessionid={sid}", DoubaoQuotaParser.NormalizeCookie($"Cookie: a=1; sessionid={sid}"));
        Assert.Null(DoubaoQuotaParser.NormalizeCookie("a=1"));
        Assert.Null(DoubaoQuotaParser.NormalizeCookie($"sessionid={sid}; sessionid={sid}"));
        Assert.Null(DoubaoQuotaParser.NormalizeCookie("sessionid=xyz"));
        Assert.Null(DoubaoQuotaParser.NormalizeCookie($"sessionid={sid}; n=中文"));
    }
}

public class LocalLyricsTests
{
    private static string KrcText(string title, int totalMs, bool translation)
    {
        var sb = new StringBuilder();
        sb.Append($"[id:$00000000]\n[ar:周杰伦]\n[ti:{title}]\n[total:{totalMs}]\n[offset:0]\n");
        if (translation)
        {
            var json = """{"content":[{"language":0,"type":1,"lyricContent":[["Line one"],["Line two"],["Line three"]]}],"version":1}""";
            sb.Append("[language:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) + "]\n");
        }
        sb.Append("[1000,2000]<0,500,0>第<500,500,0>一<1000,1000,0>行\n");
        sb.Append("[3000,2000]<0,2000,0>第二行\n");
        sb.Append("[5000,2000]<0,2000,0>第三行\n");
        return sb.ToString();
    }

    [Fact]
    public void Krc_RoundTrip_ParsesWordsAndTranslation()
    {
        var bytes = Krc.Encode(KrcText("晴天", 269000, true));
        Assert.Equal("krc1", Encoding.ASCII.GetString(bytes, 0, 4));
        var text = Krc.Decode(bytes);
        Assert.NotNull(text);
        var lines = Krc.Parse(text!);
        Assert.Equal(3, lines.Count);
        Assert.Equal("第一行", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].At);
        Assert.Equal("Line two", lines[1].Translation);
    }

    [Fact]
    public void Krc_RejectsGarbage()
    {
        Assert.Null(Krc.Decode("krc1abcdef"u8.ToArray()));
        Assert.Null(Krc.Decode("hello"u8.ToArray()));
    }

    [Fact]
    public void Krc_ParseFallsBackToLrc()
    {
        var lines = Krc.Parse("[00:01.00]a\n[00:02.00]b\n");
        Assert.Equal(2, lines.Count);
    }

    [Theory]
    [InlineData("周杰伦 - 晴天-0123456789abcdef0123456789abcdef.krc", "晴天", "周杰伦", 269, true)]
    [InlineData("周杰伦 - 晴天-0123456789abcdef0123456789abcdef-123-0.krc", "晴天", "周杰伦", 268.5, true)]
    [InlineData("周杰伦 - 晴天-0123456789abcdef0123456789abcdef.krc", "晴天", "周杰伦", 280, false)]
    [InlineData("周杰伦 - 晴天 (Live)-0123456789abcdef0123456789abcdef.krc", "晴天", "周杰伦", 269, false)]
    [InlineData("林俊杰 - 晴天-0123456789abcdef0123456789abcdef.krc", "晴天", "周杰伦", 269, false)]
    public void Krc_FileMatching(string file, string title, string artist, double dur, bool ok) =>
        Assert.Equal(ok, Krc.Matches(file, KrcText(title, 269000, false), title, artist, dur));

    [Fact]
    public void Kugou_IniUtf16()
    {
        var bytes = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("[Lyric]\r\nLyricPath=D:\\KuGou\\Lyric\\\r\n")).ToArray();
        Assert.Equal("D:\\KuGou\\Lyric\\", Krc.LyricDirFromIni(bytes));
    }

    private static byte[] MsgStr(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        return b.Length < 32 ? [(byte)(0xa0 | b.Length), .. b] : b.Length < 256 ? [0xd9, (byte)b.Length, .. b] : [0xda, (byte)(b.Length >> 8), (byte)b.Length, .. b];
    }

    [Fact]
    public void Soda_FindsNearestLyricWithMatchingArtist()
    {
        var lrcA = "[ti:晴天]\n[00:01.00]故事的小黄花\n[00:05.00]从出生那年就飘着\n[00:09.00]童年的荡秋千\n";
        var lrcB = "[ti:七里香]\n[00:01.00]窗外的麻雀\n[00:05.00]在电线杆上多嘴\n[00:09.00]你说这一句\n";
        var data = new List<byte>();
        data.AddRange(new byte[100]);
        data.AddRange(MsgStr("name")); var anchor = data.Count; data.AddRange(MsgStr("晴天"));
        data.AddRange(MsgStr("artists")); data.AddRange(MsgStr("周杰伦"));
        data.AddRange(MsgStr("content")); data.AddRange(MsgStr(lrcA));
        data.AddRange(new byte[50]);
        data.AddRange(MsgStr("content")); data.AddRange(MsgStr(lrcB));
        var arr = data.ToArray();
        Assert.Contains(SodaScan.StringPatterns("晴天"), p => arr.AsSpan().IndexOf(p) == anchor);
        Assert.Equal(lrcA, SodaScan.FindLyric(arr, anchor, "晴天", "周杰伦"));
        Assert.Null(SodaScan.FindLyric(arr, anchor, "晴天", "林俊杰"));
        // [ti:] 对不上的歌词不要
        Assert.Null(SodaScan.FindLyric(MsgStr("周杰伦").Concat(MsgStr(lrcB)).ToArray(), 0, "晴天", "周杰伦"));
    }
}

public class AgentHubTests
{
    [Theory]
    [InlineData("codex", true)]
    [InlineData("deepseek-harness", true)]
    [InlineData("dsh", true)]
    [InlineData("claude-code", false)]
    [InlineData("claude", false)]
    [InlineData("gemini-cli", false)]
    [InlineData("cursor", false)]
    [InlineData("", false)]
    public void OnlyCodexAndDshApproveOnIsland(string source, bool ok) => Assert.Equal(ok, AgentPolicy.CanApproveOnIsland(source));

    [Theory]
    [InlineData("gemini", """{"hook_event_name":"AfterAgent","session_id":"g1"}""", ActivityKind.Done)]
    [InlineData("gemini", """{"hook_event_name":"BeforeAgent","session_id":"g1","prompt":"hi"}""", ActivityKind.Running)]
    [InlineData("gemini-cli", """{"hook_event_name":"Notification","session_id":"g1","notification_type":"ToolPermission"}""", ActivityKind.WaitingApproval)]
    [InlineData("cursor", """{"hook_event_name":"stop","conversation_id":"c1","status":"completed"}""", ActivityKind.Done)]
    [InlineData("cursor", """{"hook_event_name":"afterFileEdit","conversation_id":"c1","file_path":"a.cs"}""", ActivityKind.Running)]
    [InlineData("opencode", """{"source":"opencode","hook_event_name":"session.idle","session_id":"o1"}""", ActivityKind.Done)]
    [InlineData("opencode", """{"source":"opencode","hook_event_name":"session.error","session_id":"o1"}""", ActivityKind.Error)]
    [InlineData("opencode", """{"source":"opencode","hook_event_name":"permission.asked","session_id":"o1"}""", ActivityKind.WaitingApproval)]
    [InlineData("qwen", """{"hook_event_name":"Stop","session_id":"q1"}""", ActivityKind.Done)]
    public void NewSources_Classify(string source, string json, ActivityKind kind)
    {
        var a = AgentEventParser.Parse(json, source);
        Assert.NotNull(a);
        Assert.Equal(kind, a!.Kind);
        Assert.StartsWith("agent:" + AgentEventParser.NormalizeSource(source) + ":", a.Id);
    }

    [Fact]
    public void ModelEvents_AreIgnored()
    {
        Assert.Null(AgentEventParser.Parse("""{"hook_event_name":"BeforeModel","session_id":"g"}""", "gemini"));
        Assert.Null(AgentEventParser.Parse("""{"hook_event_name":"x"}""", "unknown-agent"));
    }

    [Fact]
    public void Sessions_TrackLifecycle_AndOrder()
    {
        var s = new AgentSessions();
        var t0 = new DateTime(2026, 11, 20, 10, 0, 0);
        s.Ingest(AgentEventParser.Parse("""{"hook_event_name":"BeforeAgent","session_id":"a"}""", "gemini")!, t0);
        s.Ingest(AgentEventParser.Parse("""{"hook_event_name":"PermissionRequest","session_id":"b"}""", "claude")!, t0.AddSeconds(5));
        s.Ingest(AgentEventParser.Parse("""{"hook_event_name":"AfterAgent","session_id":"a"}""", "gemini")!, t0.AddSeconds(90));
        var list = s.List(t0.AddSeconds(100));
        Assert.Equal(2, list.Count);
        Assert.Equal(AgentStatus.Waiting, list[0].Status);
        Assert.Equal("claude-code", list[0].Source);
        Assert.Equal(AgentStatus.Done, list[1].Status);
        Assert.Equal("1 分 30 秒", AgentSessions.Elapsed(list[1], t0.AddSeconds(500)));
        Assert.Single(s.List(t0.AddMinutes(40)));          // 完成 30 分钟后清掉
        Assert.Empty(s.List(t0.AddHours(4)));              // 卡住的在跑会话 3 小时后清掉
    }

    [Fact]
    public void Sessions_IgnoreNonAgentActivities()
    {
        var s = new AgentSessions();
        s.Ingest(new IslandActivity("music:x", "music", "t", "b", ActivityKind.Info, 1), DateTime.Now);
        Assert.Empty(s.List(DateTime.Now));
    }
}

public class HookPlanTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Qisland\Qisland.exe";

    [Fact]
    public void Gemini_ApplyKeepsUserHooks_AndRemoveRestores()
    {
        var t = HookPlans.ById("gemini-hooks")!;
        var original = """
            { // user comment
              "theme": "Dracula",
              "hooks": { "BeforeTool": [ { "matcher": "write_file", "hooks": [ { "type": "command", "command": "my-check.sh" } ] } ] },
            }
            """;
        var applied = HookPlans.Apply(t, original, Exe);
        var root = JsonNode.Parse(applied)!;
        Assert.Equal("Dracula", root["theme"]!.ToString());
        Assert.Equal("my-check.sh", root["hooks"]!["BeforeTool"]![0]!["hooks"]![0]!["command"]!.ToString());
        Assert.Equal(t.Events.OrderBy(x => x), HookPlans.InstalledEvents(t, applied).OrderBy(x => x));
        Assert.Contains("hook gemini-cli", root["hooks"]!["AfterAgent"]![0]!["hooks"]![0]!["command"]!.ToString());

        // 再装一次不会重复
        var twice = HookPlans.Apply(t, applied, Exe);
        Assert.Single(JsonNode.Parse(twice)!["hooks"]!["AfterAgent"]!.AsArray());

        var removed = JsonNode.Parse(HookPlans.Remove(t, twice, out var n))!;
        Assert.Equal(t.Events.Length, n);
        Assert.Equal("Dracula", removed["theme"]!.ToString());
        Assert.Single(removed["hooks"]!.AsObject());
        Assert.Empty(HookPlans.InstalledEvents(t, removed.ToJsonString()));
    }

    [Fact]
    public void Cursor_FlatShape_WithVersion()
    {
        var t = HookPlans.ById("cursor-hooks")!;
        var applied = JsonNode.Parse(HookPlans.Apply(t, null, Exe))!;
        Assert.Equal(1, applied["version"]!.GetValue<int>());
        Assert.Contains("Qisland.exe", applied["hooks"]!["stop"]![0]!["command"]!.ToString());
        Assert.Null(applied["hooks"]!["beforeShellExecution"]); // 不碰会拦截命令的事件
        var back = JsonNode.Parse(HookPlans.Remove(t, applied.ToJsonString(), out _))!;
        Assert.Null(back["hooks"]);
        Assert.Equal(1, back["version"]!.GetValue<int>());
    }

    [Fact]
    public void Cursor_RelayNeverAllowsPermissions()
    {
        Assert.Equal("{\"continue\":true}", HookPlans.RelayOutput("cursor", """{"hook_event_name":"beforeSubmitPrompt"}"""));
        Assert.Equal("{}", HookPlans.RelayOutput("cursor", """{"hook_event_name":"stop"}"""));
        Assert.DoesNotContain("allow", HookPlans.RelayOutput("gemini-cli", """{"hook_event_name":"BeforeTool"}"""));
    }

    [Fact]
    public void OpenCode_PluginIsLocalOnly()
    {
        var t = HookPlans.ById("opencode-plugin")!;
        var js = HookPlans.Apply(t, null, Exe);
        Assert.Contains(HookPlans.PluginMarker, js);
        Assert.Contains("http://127.0.0.1:9840/v1/agent/opencode", js);
        Assert.DoesNotContain("https://", js);
        Assert.NotEmpty(HookPlans.InstalledEvents(t, js));
        Assert.Empty(HookPlans.InstalledEvents(t, "export const Other = 1;"));
    }

    [Fact]
    public void NoTargetTouchesClaudeSettings() =>
        Assert.DoesNotContain(HookPlans.All, t => t.RelativePath.Contains(".claude", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void BadRootIsRefused() =>
        Assert.Throws<FormatException>(() => HookPlans.Apply(HookPlans.ById("gemini-hooks")!, "[1,2]", Exe));
}
