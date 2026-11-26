using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 本机 AI 编程工具额度：Claude Code / Cursor / Gemini CLI / Kimi Code。默认关，设置里打开后才读。
/// 怎么读：只读打开各家客户端登录后存在本机的令牌（Claude、Gemini、Kimi 是 JSON 文件，Cursor 是 state.vscdb 里的一项），
/// 调各家自己的用量接口，拿到百分比就丢掉令牌。
/// 不做的事：不写、不改、不锁这些文件；不刷新令牌（会轮换令牌把客户端踢下线）——令牌过期就显示「过期」，等你下次用那个工具时它自己续上；
/// 令牌和接口原文不写日志、不落盘。接口都没有公开文档，变了就显示「读不了」，不估算。
/// </summary>
public sealed class AiUsageService
{
    public enum State { Off, Missing, Expired, Ok, Error }
    public sealed record Status(State State, string Message, AiUsage.Snapshot? Snapshot, DateTime At);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "Qisland/4.3 (+https://github.com/Qiuizi/Qisland)" } },
    };

    private readonly Action<Action> _post;
    private readonly Dictionary<string, Status> _status = new();
    private readonly Dictionary<string, DateTime> _next = new();
    private int _busy;
    public event Action? Changed;

    public AiUsageService(Action<Action> post) => _post = post;

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static bool Enabled => Features.On(Features.AiUsage);
    public static bool ToolOn(string tool) => Features.On("ai-usage-" + tool);

    public Status Get(string tool) => _status.TryGetValue(tool, out var s) ? s : new Status(State.Off, Enabled ? "还没读" : "没打开", null, DateTime.MinValue);

    /// <summary>岛上用：读到了、且 1 小时内的。</summary>
    public IEnumerable<AiUsage.Snapshot> Fresh()
    {
        if (!Enabled && !_demo) yield break;
        foreach (var t in AiUsage.Tools)
            if ((_demo || ToolOn(t)) && _status.TryGetValue(t, out var s) && s.State == State.Ok && s.Snapshot != null && DateTime.Now - s.At < TimeSpan.FromHours(1))
                yield return s.Snapshot;
    }

    /// <summary>每 10 秒被 App 调一次；每个工具各自 10 分钟一次（429 / 出错退避更久）。</summary>
    public void Tick()
    {
        if (!Enabled) return;
        var due = AiUsage.Tools.Where(t => ToolOn(t) && DateTime.Now >= _next.GetValueOrDefault(t, DateTime.MinValue)).ToList();
        if (due.Count > 0) Refresh(due);
    }

    public void RefreshNow()
    {
        _next.Clear();
        if (Enabled) Refresh(AiUsage.Tools.Where(ToolOn).ToList());
    }

    /// <summary>关掉总开关或某个工具：清掉它的结果，岛上马上不显示。</summary>
    public void OnToggle()
    {
        foreach (var t in AiUsage.Tools) if (!Enabled || !ToolOn(t)) { _status.Remove(t); _next.Remove(t); }
        Changed?.Invoke();
        if (Enabled) Tick();
    }

    private void Refresh(List<string> tools)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        foreach (var t in tools) _next[t] = DateTime.Now.AddMinutes(10);
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var t in tools)
                {
                    Status st;
                    try { st = await Read(t); }
                    catch (TaskCanceledException) { st = new Status(State.Error, "超时，稍后再试", null, DateTime.Now); }
                    catch (HttpRequestException) { st = new Status(State.Error, "网络连不上", null, DateTime.Now); }
                    catch (Exception ex) { st = new Status(State.Error, "读取出错", null, DateTime.Now); App.Log($"AI 额度 {t}: {ex.GetType().Name}"); }
                    var tool = t;
                    _post(() =>
                    {
                        if (st.State == State.Error) _next[tool] = DateTime.Now.AddMinutes(st.Message.Contains("频繁") ? 30 : 15);
                        // 读到中途被关掉了：不再塞回结果
                        if (!Enabled || !ToolOn(tool)) return;
                        var prev = _status.GetValueOrDefault(tool);
                        // 一次网络失败不把上次的读数抹掉（1 小时内照常显示）
                        if (st.State == State.Error && prev is { State: State.Ok } && DateTime.Now - prev.At < TimeSpan.FromHours(1)) return;
                        _status[tool] = st;
                        if (prev?.State != st.State) App.Log($"AI 额度 {AiUsage.Name(tool)}: {st.State}");
                        Changed?.Invoke();
                    });
                }
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private Task<Status> Read(string tool) => tool switch
    {
        "claude" => ReadClaude(),
        "cursor" => ReadCursor(),
        "gemini" => ReadGemini(),
        "kimi" => ReadKimi(),
        _ => Task.FromResult(new Status(State.Off, "", null, DateTime.Now)),
    };

    private static Status Miss(string msg) => new(State.Missing, msg, null, DateTime.Now);
    private static Status Exp(string msg) => new(State.Expired, msg, null, DateTime.Now);
    private static Status Err(string msg) => new(State.Error, msg, null, DateTime.Now);
    private static Status Ok(AiUsage.Snapshot s) => new(State.Ok, string.Join(" · ", s.Windows.Select(w => $"{w.Label} {w.UsedPercent:0}%")), s, DateTime.Now);

    /// <summary>只读、允许别人同时写地打开一个小文件（客户端正在写也不锁它）。</summary>
    private static string? ReadShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var fi = new FileInfo(path);
            if (fi.Length > 256 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch { return null; }
    }

    private static Status? HttpFail(HttpStatusCode code, string who, string relogin) => code switch
    {
        HttpStatusCode.OK => null,
        HttpStatusCode.Unauthorized => Exp($"{who}的登录令牌失效了，{relogin}"),
        HttpStatusCode.Forbidden => Err($"{who}拒绝了用量查询（这个登录方式可能不支持查额度）"),
        HttpStatusCode.TooManyRequests => Err("查询太频繁，过半小时再试"),
        _ => Err($"{who}的用量接口返回 {(int)code}，可能改版了"),
    };

    // ───────────── Claude Code ─────────────

    private static async Task<Status> ReadClaude()
    {
        var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : Path.Combine(Home, ".claude");
        var text = ReadShared(Path.Combine(dir, ".credentials.json"));
        if (text == null) return Miss("没找到 Claude Code 的登录（用 claude 登录一次 Pro / Max 账号后才有）");
        var cred = AiUsage.ClaudeCredential(text);
        text = null;
        if (cred == null) return Miss("Claude Code 是用 API Key 登录的，没有订阅额度可查");
        if (cred.Expired(DateTime.UtcNow)) return Exp("Claude Code 的登录令牌过期了，下次用 Claude Code 时会自动续上");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cred.Token);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        if (HttpFail(resp.StatusCode, "Claude ", "下次用 Claude Code 时会自动续上") is { } f) return f;
        var snap = AiUsage.ParseClaude(await resp.Content.ReadAsStringAsync(), cred.Extra);
        return snap != null ? Ok(snap) : Err("Claude 的用量接口格式变了，暂时读不了");
    }

    // ───────────── Cursor ─────────────

    private static async Task<Status> ReadCursor()
    {
        var db = Path.Combine(AppData, "Cursor", "User", "globalStorage", "state.vscdb");
        if (!File.Exists(db)) return Miss("没找到 Cursor（装好并登录一次后才有）");
        var jwt = SqliteReadOnly.Value(db, "cursorAuth/accessToken");
        if (string.IsNullOrWhiteSpace(jwt)) return Miss("Cursor 还没登录");
        if (AiUsage.JwtExpiry(jwt) is { } exp && exp <= DateTime.UtcNow) return Exp("Cursor 的登录过期了，打开一次 Cursor 会自动续上");
        var cookie = AiUsage.CursorCookie(jwt);
        jwt = null;
        if (cookie == null) return Err("Cursor 的登录信息格式变了，暂时读不了");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://cursor.com/api/usage-summary");
        req.Headers.TryAddWithoutValidation("Cookie", "WorkosCursorSessionToken=" + cookie);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        if (HttpFail(resp.StatusCode, "Cursor ", "打开一次 Cursor 会自动续上") is { } f) return f;
        var snap = AiUsage.ParseCursor(await resp.Content.ReadAsStringAsync());
        return snap != null ? Ok(snap) : Err("这个 Cursor 账号没有按百分比计的额度（企业 / 按量计费账号）");
    }

    // ───────────── Gemini CLI ─────────────

    private static string? _geminiProject;
    private static string _geminiTier = "";

    private static async Task<Status> ReadGemini()
    {
        var text = ReadShared(Path.Combine(Home, ".gemini", "oauth_creds.json"));
        if (text == null) return Miss("没找到 Gemini CLI 的 Google 登录（用 API Key 的没有额度可查）");
        var cred = AiUsage.GeminiCredential(text);
        text = null;
        if (cred == null) return Miss("Gemini CLI 还没登录");
        if (cred.Expired(DateTime.UtcNow)) return Exp("Gemini CLI 的令牌过期了（约 1 小时一换），下次用 Gemini CLI 时会自动续上");
        const string Base = "https://cloudcode-pa.googleapis.com/v1internal:";
        async Task<HttpResponseMessage> Post(string method, string body)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Base + method) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cred.Token);
            try { return await Http.SendAsync(req); } finally { req.Dispose(); }
        }
        var project = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT") is { Length: > 0 } envp ? envp : _geminiProject;
        if (project == null)
        {
            using var load = await Post("loadCodeAssist", "{\"metadata\":{\"ideType\":\"IDE_UNSPECIFIED\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}");
            if (HttpFail(load.StatusCode, "Google ", "下次用 Gemini CLI 时会自动续上") is { } lf) return lf;
            (_geminiProject, _geminiTier) = AiUsage.ParseGeminiLoad(await load.Content.ReadAsStringAsync());
            project = _geminiProject;
            if (project == null) return Err("这个 Google 账号还没开通 Gemini Code Assist，没有额度可查");
        }
        using var resp = await Post("retrieveUserQuota", "{\"project\":\"" + project.Replace("\\", "").Replace("\"", "") + "\"}");
        if (HttpFail(resp.StatusCode, "Google ", "下次用 Gemini CLI 时会自动续上") is { } f) return f;
        var snap = AiUsage.ParseGemini(await resp.Content.ReadAsStringAsync(), _geminiTier);
        return snap != null ? Ok(snap) : Err("Gemini 的额度接口没有返回额度");
    }

    // ───────────── Kimi Code ─────────────

    private static async Task<Status> ReadKimi()
    {
        string? text = null;
        var home = Environment.GetEnvironmentVariable("KIMI_CODE_HOME") is { Length: > 0 } kh ? kh : Path.Combine(Home, ".kimi-code");
        foreach (var p in new[] { Path.Combine(home, "credentials", "kimi-code.json"), Path.Combine(Home, ".kimi", "credentials", "kimi-code.json") })
            if ((text = ReadShared(p)) != null) break;
        if (text == null) return Miss("没找到 Kimi Code 的登录（kimi login 之后才有）");
        var cred = AiUsage.KimiCredential(text);
        text = null;
        if (cred == null) return Miss("Kimi Code 还没登录");
        if (cred.Expired(DateTime.UtcNow)) return Exp("Kimi Code 的令牌过期了，下次用 Kimi Code 时会自动续上");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.kimi.com/coding/v1/usages");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cred.Token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        if (HttpFail(resp.StatusCode, "Kimi ", "下次用 Kimi Code 时会自动续上") is { } f) return f;
        var snap = AiUsage.ParseKimi(await resp.Content.ReadAsStringAsync());
        return snap != null ? Ok(snap) : Err("Kimi 的用量接口格式变了，暂时读不了");
    }

    /// <summary>演示用（--snapshot）。</summary>
    private bool _demo;

    public void Demo()
    {
        _demo = true;
        var now = DateTime.UtcNow;
        _status["claude"] = Ok(new AiUsage.Snapshot("claude", [new("5 小时", 42, now.AddHours(2.2)), new("本周", 63, now.AddDays(3))], "max"));
        _status["cursor"] = Ok(new AiUsage.Snapshot("cursor", [new("本月", 28, now.AddDays(12))], "pro"));
    }
}

/// <summary>
/// 用 Windows 自带的 winsqlite3.dll 只读查 Cursor 的 state.vscdb（不引 SQLite 包）。
/// 只读 + 不建锁文件；Cursor 正在写导致忙时，退回 immutable 方式读一次快照。
/// </summary>
internal static class SqliteReadOnly
{
    private const int ReadOnly = 0x1, Uri = 0x40, Row = 100;
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2")] private static extern int Open(byte[] file, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close_v2")] private static extern int Close(IntPtr db);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2")] private static extern int Prepare(IntPtr db, byte[] sql, int n, out IntPtr stmt, IntPtr tail);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_bind_text")] private static extern int BindText(IntPtr stmt, int i, byte[] text, int n, IntPtr destructor);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step")] private static extern int Step(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_bytes")] private static extern int ColumnBytes(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_blob")] private static extern IntPtr ColumnBlob(IntPtr stmt, int i);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize")] private static extern int Finalize(IntPtr stmt);
    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_busy_timeout")] private static extern int BusyTimeout(IntPtr db, int ms);

    private static byte[] Z(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static string? Value(string path, string key)
    {
        var baseUri = new System.Uri(path).AbsoluteUri;
        foreach (var q in new[] { "?mode=ro", "?mode=ro&immutable=1" })
        {
            try { if (Query(baseUri + q, key, out var v)) return v; }
            catch (DllNotFoundException) { App.Log("Cursor 额度: 系统里没有 winsqlite3.dll"); return null; }
            catch (EntryPointNotFoundException) { return null; }
        }
        return null;
    }

    private static bool Query(string uri, string key, out string? value)
    {
        value = null;
        if (Open(Z(uri), out var db, ReadOnly | Uri, IntPtr.Zero) != 0) { if (db != IntPtr.Zero) Close(db); return false; }
        try
        {
            BusyTimeout(db, 800);
            if (Prepare(db, Z("SELECT value FROM ItemTable WHERE key = ?1"), -1, out var stmt, IntPtr.Zero) != 0) return false;
            try
            {
                var k = Encoding.UTF8.GetBytes(key);
                BindText(stmt, 1, k, k.Length, new IntPtr(-1));   // SQLITE_TRANSIENT：让 SQLite 自己拷一份
                var rc = Step(stmt);
                if (rc != Row) return rc == 101;   // 101 = 查完了但没有这一项
                var n = ColumnBytes(stmt, 0);
                if (n <= 0 || n > 64 * 1024) return true;
                var buf = new byte[n];
                Marshal.Copy(ColumnBlob(stmt, 0), buf, 0, n);
                value = Encoding.UTF8.GetString(buf).Trim().Trim('"');
                Array.Clear(buf);
                return true;
            }
            finally { Finalize(stmt); }
        }
        finally { Close(db); }
    }
}
