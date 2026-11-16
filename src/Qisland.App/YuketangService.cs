using Qisland.Core;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qisland.App;

/// <summary>
/// 雨课堂（川大 scu.yuketang.cn）：微信扫码登录，只读拉取各课程里没交的作业 / 试卷和截止时间。
/// 登录得到的 sessionid 用 DPAPI 加密存在本机；不存密码，不做任何签到、答题、看课之类的写操作。
/// 20 分钟查一次，课程之间间隔 0.5 秒，对服务器很轻。
/// </summary>
public sealed class YuketangService
{
    public const string Platform = "雨课堂";
    public static string Host = "scu.yuketang.cn";
    private static string CredPath => Path.Combine(App.DataDir, "yuketang.bin");
    private static string DebugDir => Path.Combine(App.DataDir, "debug");
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly HomeworkHub _hub;
    private readonly ActivityStore _store;
    private string? _session;
    private DateTime _next = DateTime.MinValue;
    private bool _busy;
    public string? UserName { get; private set; }
    public bool LoggedIn => _session != null;
    public bool Expired { get; private set; }
    public DateTime? LastOk { get; private set; }
    public event Action? Changed;

    public YuketangService(HomeworkHub hub, ActivityStore store)
    {
        _hub = hub; _store = store;
        if (Dpapi.Load(CredPath) is { } json)
        {
            try
            {
                var o = JsonNode.Parse(json)!;
                _session = o["sessionid"]?.GetValue<string>();
                UserName = o["name"]?.GetValue<string>();
                Host = o["host"]?.GetValue<string>() ?? Host;
            }
            catch { }
        }
        _next = DateTime.Now.AddSeconds(20);
    }

    private static HttpClient NewHttp(CookieContainer? jar = null)
    {
        var h = new HttpClient(new HttpClientHandler { CookieContainer = jar ?? new CookieContainer(), UseCookies = jar != null }) { Timeout = TimeSpan.FromSeconds(40) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(UA);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        h.DefaultRequestHeaders.Referrer = new Uri($"https://{Host}/web");
        return h;
    }

    // ───────────── 扫码登录 ─────────────

    public sealed record Qr(string Content, string Token);

    public static async Task<Qr> RequestQr()
    {
        using var h = NewHttp();
        var o = JsonNode.Parse(await h.GetStringAsync($"https://{Host}/api/v3/user/login/app-web-pre-info"))!;
        if (o["code"]?.GetValue<int>() != 0) throw new IOException(o["msg"]?.ToString() ?? "获取二维码失败");
        return new Qr(o["data"]!["qrContent"]!.GetValue<string>(), o["data"]!["token"]!.GetValue<string>());
    }

    /// <summary>长轮询等扫码确认；成功返回 true。约 4.5 分钟后二维码失效返回 false。</summary>
    public async Task<bool> WaitLogin(Qr qr, CancellationToken ct)
    {
        var jar = new CookieContainer();
        using var h = NewHttp(jar);
        var end = DateTime.Now.AddSeconds(270);
        while (DateTime.Now < end)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(35));
                var res = await h.PostAsync($"https://{Host}/api/v3/user/login/app-web-login",
                    new StringContent(JsonSerializer.Serialize(new { token = qr.Token }), Encoding.UTF8, "application/json"), cts.Token);
                var text = await res.Content.ReadAsStringAsync(cts.Token);
                var o = text.Length > 0 ? JsonNode.Parse(text) : null;
                if (o?["code"]?.GetValue<int>() == 0 && o["data"] is JsonObject d)
                {
                    var sid = jar.GetCookies(new Uri($"https://{Host}/"))["sessionid"]?.Value;
                    if (string.IsNullOrEmpty(sid)) throw new IOException("登录成功但没拿到会话");
                    _session = sid;
                    UserName = d["Name"]?.ToString() ?? "";
                    Expired = false;
                    Dpapi.Save(CredPath, JsonSerializer.Serialize(new { host = Host, sessionid = sid, name = UserName }));
                    App.Log($"雨课堂: 已登录 {UserName}");
                    _next = DateTime.Now;
                    Changed?.Invoke();
                    return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HttpRequestException) { await Task.Delay(1500, ct); }
        }
        return false;
    }

    public void Logout()
    {
        _session = null; UserName = null; Expired = false;
        try { File.Delete(CredPath); } catch { }
        _hub.Clear(Platform);
        _store.Remove("ykt:expired");
        Changed?.Invoke();
    }

    // ───────────── 拉作业 ─────────────

    /// <summary>每秒的计时器里调用，到点才真正去拉。</summary>
    public void Tick()
    {
        if (_session == null || _busy || Expired || DateTime.Now < _next) return;
        _ = RefreshAsync();
    }

    public void RefreshNow() { _next = DateTime.MinValue; Expired = false; Tick(); }

    private async Task RefreshAsync()
    {
        _busy = true;
        _next = DateTime.Now.AddMinutes(20);
        try
        {
            var items = await Task.Run(Fetch);
            if (items == null) return;
            LastOk = DateTime.Now;
            _hub.Update(Platform, items);
            App.Log($"雨课堂: {items.Count(i => !i.Done)} 项未完成");
        }
        catch (Exception ex) { App.Log("雨课堂: " + ex.Message); _next = DateTime.Now.AddMinutes(5); }
        finally { _busy = false; Changed?.Invoke(); }
    }

    private async Task<JsonNode?> Get(HttpClient h, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}{path}");
        req.Headers.Add("Cookie", "sessionid=" + _session);
        req.Headers.Add("xtbz", "ykt");
        using var res = await h.SendAsync(req);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { MarkExpired(); return null; }
        var o = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        var code = o?["code"] ?? o?["errcode"];
        if (code != null && code.ToString() is "50000" or "401002" or "10005") { MarkExpired(); return null; }
        return o;
    }

    private void MarkExpired()
    {
        if (Expired) return;
        Expired = true;
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            _store.Upsert(new IslandActivity("ykt:expired", HomeworkHub.Source, "雨课堂登录过期了", "托盘菜单 → 雨课堂扫码登录，重新扫一下就好", ActivityKind.Warning, 40), sticky: true);
            Changed?.Invoke();
        });
    }

    private async Task<List<HomeworkItem>?> Fetch()
    {
        using var h = NewHttp();
        var courses = await Get(h, "/v2/api/web/courses/list?identity=2");
        if (courses == null) return null;
        Dump("courses", courses);
        var list = courses["data"]?["list"]?.AsArray() ?? [];
        // 只看当前学期：term 取最大的那一批
        var terms = list.Select(c => (long?)Num(c?["term"])).Where(t => t > 0).ToList();
        var term = terms.Count > 0 ? terms.Max() : null;
        var items = new List<HomeworkItem>();
        var n = 0;
        foreach (var c in list)
        {
            if (c == null) continue;
            if (term != null && (long)Num(c["term"]) is var t && t > 0 && t != term) continue;
            var cid = c["classroom_id"]?.ToString();
            if (string.IsNullOrEmpty(cid)) continue;
            var course = c["course"]?["name"]?.ToString() ?? c["name"]?.ToString() ?? "";
            await Task.Delay(500);
            var log = await Get(h, $"/v2/api/web/logs/learn/{cid}?actype=-1&page=0&offset=50&sort=-1");
            if (log == null) return null;
            if (n++ < 3) Dump("activities-" + cid, log);
            foreach (var a in log["data"]?["activities"]?.AsArray() ?? [])
            {
                if (a is not JsonObject ao) continue;
                var type = (int)Num(ao["type"]);
                // 5 = 试卷 / 作业，19 = 章节作业；其它类型只要带截止时间也收（比如考试）
                var dl = FindDeadline(ao);
                if (dl == null || type is 9 or 14) continue;
                if (dl < DateTime.Now.AddDays(-1)) continue;
                var done = Truthy(ao["is_finished"]) || Truthy(ao["finished"]) || Truthy(ao["is_submit"]) || Truthy(ao["submitted"]);
                var kind = type == 5 ? "试卷" : type == 19 ? "章节作业" : "作业";
                items.Add(new HomeworkItem(Platform, course, ao["title"]?.ToString() ?? kind, dl.Value, done,
                    $"https://{Host}/v2/web/studentLog/{cid}", kind));
            }
        }
        return items;
    }

    // 不同活动类型的截止字段名不统一：在活动本身和 content 里找名字带 deadline / end 的时间戳
    private static DateTime? FindDeadline(JsonObject a)
    {
        foreach (var obj in new[] { a, a["content"] as JsonObject }.Where(o => o != null))
            foreach (var (k, v) in obj!)
            {
                var key = k.ToLowerInvariant();
                if (!(key.Contains("deadline") || key is "end" or "end_time" or "endtime" or "end_date" or "score_d" or "limit_time")) continue;
                if (v is JsonValue && HomeworkItem.FromStamp(Num(v)) is { } t) return t;
            }
        return null;
    }

    private static double Num(JsonNode? n)
    {
        if (n is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return double.TryParse(v.ToString(), out var p) ? p : 0;
    }

    private static bool Truthy(JsonNode? n) => n is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : Num(v) > 0);

    // 调试开关打开时（QISLAND_DEBUG=1 或 debug\\enabled 文件），把前几次的原始响应存一份到本机 debug 目录，方便对字段（不含登录凭据）
    private static void Dump(string name, JsonNode node)
    {
        if (!App.DebugDumps) return;
        try
        {
            Directory.CreateDirectory(DebugDir);
            File.WriteAllText(Path.Combine(DebugDir, $"ykt-{name}.json"), node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch { }
    }
}
