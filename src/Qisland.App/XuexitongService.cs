using Qisland.Core;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qisland.App;

/// <summary>
/// 学习通（超星）：学习通 App 扫码登录，只读拉取本学期课程里没交的作业和截止时间。
/// Cookie 用 DPAPI 加密存在本机；不存密码，不做签到、刷课、答题。
/// 作业截止时间只在作业详情页上，所以只对「未交」的作业各请求一次详情，并在内存里缓存。
/// 30 分钟查一次；遇到超星的反爬验证页就停 2 小时，不去硬闯。
/// </summary>
public sealed partial class XuexitongService
{
    public const string Platform = "学习通";
    private static string CredPath => Path.Combine(App.DataDir, "xuexitong.bin");
    private static string DebugDir => Path.Combine(App.DataDir, "debug");
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private static readonly Uri Root = new("https://chaoxing.com/");

    private readonly HomeworkHub _hub;
    private readonly ActivityStore _store;
    // 会话代数：退出登录 / 换账号换一代，旧一代的拉取结果不能写回作业列表、缓存、Cookie 和登录状态
    private readonly HomeworkSync _sync;
    private CookieContainer? _jar;
    private DateTime _next;
    private bool _busy;
    private readonly Dictionary<string, DateTime?> _deadlineCache = [];
    public string? UserName { get; private set; }
    public bool LoggedIn => _jar != null;
    public bool Expired { get; private set; }
    public DateTime? LastOk { get; private set; }
    public event Action? Changed;

    private sealed record Ck(string N, string V, string D, string P, long E = 0);
    /// <summary>登录 Cookie 里最早的过期时间（扫码登录后才记录；会话 Cookie 没有过期时间就是 null）。</summary>
    public DateTime? ExpiresAt { get; private set; }
    private static readonly string[] AuthCookies = ["_uid", "UID", "vc3", "_d", "uf", "p_auth_token", "xxtenc"];

    public XuexitongService(HomeworkHub hub, ActivityStore store)
    {
        _hub = hub; _store = store;
        _sync = hub.Sync();
        if (Dpapi.Load(CredPath) is { } json)
        {
            try
            {
                var doc = JsonDocument.Parse(json).RootElement;
                UserName = doc.GetProperty("name").GetString();
                _jar = new CookieContainer();
                foreach (var c in doc.GetProperty("cookies").Deserialize<List<Ck>>() ?? [])
                {
                    _jar.Add(new Cookie(c.N, c.V, c.P, c.D));
                    if (c.E > 0 && AuthCookies.Contains(c.N) && DateTimeOffset.FromUnixTimeSeconds(c.E).LocalDateTime is var t && (ExpiresAt == null || t < ExpiresAt)) ExpiresAt = t;
                }
            }
            catch { _jar = null; }
        }
        _next = DateTime.Now.AddSeconds(45);
    }

    private static HttpClient NewHttp(CookieContainer jar) =>
        new(new HttpClientHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(25), DefaultRequestHeaders = { { "User-Agent", UA } } };

    private void Persist()
    {
        if (_jar == null) return;
        var list = _jar.GetAllCookies().Where(c => c.Domain.Contains("chaoxing") || c.Domain.Contains("cldisk"))
            .Select(c => new Ck(c.Name, c.Value, c.Domain, c.Path, c.Expires > DateTime.Now ? new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds() : 0)).ToList();
        var auth = list.Where(c => c.E > 0 && AuthCookies.Contains(c.N)).Select(c => DateTimeOffset.FromUnixTimeSeconds(c.E).LocalDateTime).ToList();
        ExpiresAt = auth.Count > 0 ? auth.Min() : null;
        Dpapi.Save(CredPath, JsonSerializer.Serialize(new { name = UserName ?? "", cookies = list }));
    }

    // ───────────── 扫码登录 ─────────────

    public sealed class Qr { public required byte[] Png; public required string Uuid; public required string Enc; public required CookieContainer Jar; }

    public static async Task<Qr> RequestQr()
    {
        var jar = new CookieContainer();
        using var h = NewHttp(jar);
        var page = await h.GetStringAsync("https://passport2.chaoxing.com/login?fid=&newversion=true&refer=https%3A%2F%2Fi.chaoxing.com");
        var uuid = Hidden(page, "uuid");
        var enc = Hidden(page, "enc");
        if (uuid.Length == 0 || enc.Length == 0) throw new IOException("登录页结构变了，没找到二维码参数");
        var png = await h.GetByteArrayAsync($"https://passport2.chaoxing.com/createqr?uuid={uuid}&fid=-1");
        return new Qr { Png = png, Uuid = uuid, Enc = enc, Jar = jar };
    }

    /// <summary>每 2 秒问一次扫码状态；成功拿到 Cookie 后返回 true。约 4 分钟二维码过期。</summary>
    public async Task<bool> WaitLogin(Qr qr, Action<string> status, CancellationToken ct)
    {
        using var h = NewHttp(qr.Jar);
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(2000, ct);
            JsonElement o;
            try
            {
                var res = await h.PostAsync("https://passport2.chaoxing.com/getauthstatus/v2", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["enc"] = qr.Enc, ["uuid"] = qr.Uuid, ["doubleFactorLogin"] = "0", ["forbidotherlogin"] = "0",
                }), ct);
                o = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
            }
            catch (HttpRequestException) { continue; }
            catch (JsonException) { continue; }
            if (o.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.True)
            {
                if (o.TryGetProperty("containTwoFactorLogin", out var tf) && tf.ValueKind == JsonValueKind.True)
                {
                    status("这个账号开了二次验证，Qisland 暂不支持");
                    return false;
                }
                _sync.NewSession();   // 新登录 / 换号：之前在途的拉取全部作废
                _jar = qr.Jar;
                Expired = false;
                UserName = o.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
                try { UserName ??= await FetchName(h); } catch { }
                Persist();
                App.Log($"学习通: 已登录 {UserName}");
                _next = DateTime.Now;
                Changed?.Invoke();
                return true;
            }
            var type = o.TryGetProperty("type", out var t) ? t.ToString() : "";
            if (type == "4") status($"已扫码{(o.TryGetProperty("nickname", out var n4) ? "（" + n4.GetString() + "）" : "")}，请在手机上确认");
            else if (type == "6") { status("手机上取消了，点二维码刷新"); return false; }
            else if (type is "2" or "5") return false; // 二维码失效
            else if (type == "7") status(o.TryGetProperty("mes", out var m) ? m.GetString() ?? "" : "登录失败");
        }
        return false;
    }

    private static async Task<string?> FetchName(HttpClient h)
    {
        var b = await h.GetStringAsync("https://i.chaoxing.com/base");
        var m = Regex.Match(b, "aria-label=\"账号：([^\"<]{1,20})\"");
        if (!m.Success) m = Regex.Match(b, "class=\"user-name\"[^>]*>([^<]{1,20})<");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    public void Logout()
    {
        // 先换代（在途的拉取回来后什么都不能改），再删凭据、清作业缓存（排在正在进行的提交之后）
        _sync.Logout(Platform, () => { try { File.Delete(CredPath); } catch { } });
        _jar = null; UserName = null; Expired = false; ExpiresAt = null;
        _hub.Refreshed();
        _store.Remove("xxt:expired");
        Changed?.Invoke();
    }

    // ───────────── 拉作业 ─────────────

    public void Tick()
    {
        HomeworkHub.WarnExpiring(_store, "xxt:expiring", "学习通", _jar != null && !Expired ? ExpiresAt : null);
        if (_jar == null || _busy || Expired || DateTime.Now < _next || !App.NetReady()) return;
        _ = RefreshAsync();
    }

    public void RefreshNow() { _next = DateTime.MinValue; Expired = false; Tick(); }

    private async Task RefreshAsync()
    {
        _busy = true;
        _next = DateTime.Now.AddMinutes(30);
        var ticket = _sync.Begin();
        var jar = _jar!;
        try
        {
            var items = await Task.Run(() => Fetch(jar, ticket));
            if (items == null) return;
            // 会话没变才提交：存 Cookie（服务器可能续期了）、更新作业列表、写缓存；退出 / 换号后的旧结果直接丢掉
            if (!_sync.Commit(ticket, Platform, items, Persist)) { App.Log("学习通: 会话已切换，丢弃这次拉取的结果"); return; }
            LastOk = DateTime.Now;
            _hub.Refreshed();
            App.Log($"学习通: {items.Count} 项未交");
        }
        catch (StaleSession) { App.Log("学习通: 会话已切换，丢弃这次拉取"); }
        catch (AntiSpiderException) when (_sync.IsCurrent(ticket)) { App.Log("学习通: 触发了验证页，2 小时后再试"); _next = DateTime.Now.AddHours(2); }
        catch (Exception ex) when (_sync.IsCurrent(ticket)) { App.Log("学习通: " + ex.Message); _next = DateTime.Now.AddMinutes(10); }
        catch (Exception) { App.Log("学习通: 会话已切换，丢弃这次拉取的结果"); }
        finally { _busy = false; Changed?.Invoke(); }
    }

    private sealed class AntiSpiderException : Exception;
    private sealed class StaleSession : Exception;

    private async Task<string?> Get(HttpClient h, long ticket, string url)
    {
        if (!_sync.IsCurrent(ticket)) throw new StaleSession();   // 已经退出 / 换号：不再发后面的请求
        using var res = await h.GetAsync(url);
        if (!_sync.IsCurrent(ticket)) throw new StaleSession();
        var final = res.RequestMessage?.RequestUri?.ToString() ?? "";
        var text = await res.Content.ReadAsStringAsync();
        if (final.Contains("antispider", StringComparison.OrdinalIgnoreCase) || text.Contains("antispiderShowVerify")) throw new AntiSpiderException();
        if (final.Contains("passport2.chaoxing.com/login") || text.Contains("<title>用户登录</title>")) { MarkExpired(ticket); return null; }
        return text;
    }

    private void MarkExpired(long ticket)
    {
        if (Expired || !_sync.IsCurrent(ticket)) return;
        Expired = true;
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            _store.Upsert(new IslandActivity("xxt:expired", HomeworkHub.Source, "学习通登录过期了", "托盘菜单 → 作业 DDL → 学习通扫码登录", ActivityKind.Warning, 40), sticky: true, quiet: true);
            // 常驻那条是安静的（不顶掉时间），过期那一刻弹一下让人知道
            _store.Upsert(new IslandActivity("xxt:expired-note", HomeworkHub.Source, "学习通登录过期了", "作业读不到了，托盘里重新扫码登录", ActivityKind.Warning, 60, DurationSeconds: 8));
            Changed?.Invoke();
        });
    }

    private sealed record Course(string Name, string Id, string Clazz, string Cpi);

    private async Task<List<HomeworkItem>?> Fetch(CookieContainer jar, long ticket)
    {
        using var h = NewHttp(jar);
        var basePage = await Get(h, ticket, "https://i.chaoxing.com/base");
        if (basePage == null) return null;
        var fm = Regex.Match(basePage, "\"fid\"\\s*:\\s*\"?(\\d+)\"?");
        if (!fm.Success) fm = Regex.Match(basePage, "var fid = '(\\d+)'");
        var fid = fm.Success ? fm.Groups[1].Value : "0";
        var list = await Get(h, ticket, $"https://mooc2-ans.chaoxing.com/mooc2-ans/visit/courselistdata?courseType=1&courseFid={fid}&courseTeacherid=0&courseOrderBy=0");
        if (list == null) return null;
        Dump("courses.html", list);
        var courses = ParseCourses(list).Take(20).ToList();
        var now = DateTime.Now;
        var items = new List<HomeworkItem>();
        var dumped = 0;
        foreach (var c in courses)
        {
            await Task.Delay(700);
            var mid = await Get(h, ticket, $"https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid={c.Id}&clazzid={c.Clazz}&cpi={c.Cpi}&ismooc2=1&v=2");
            if (mid == null) return null;
            var workEnc = Hidden(mid, "workEnc");
            if (workEnc.Length == 0) continue; // 这门课没开作业
            var t = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            await Task.Delay(400);
            var wl = await Get(h, ticket, $"https://mooc1.chaoxing.com/mooc2/work/list?courseId={c.Id}&classId={c.Clazz}&cpi={c.Cpi}&ut=s&openc={Hidden(mid, "openc")}&enc={workEnc}&t={t}&stuenc={Hidden(mid, "enc")}&isdisplaytable=2");
            if (wl == null) return null;
            if (dumped++ < 2) Dump($"work-{c.Id}.html", wl);
            foreach (Match m in WorkItem().Matches(wl))
            {
                var url = WebUtility.HtmlDecode(Uri.UnescapeDataString(m.Groups[1].Value));
                var title = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                var state = m.Groups[3].Value.Trim();
                if (state.Contains("待批阅"))
                {
                    // 交了没批：只用列表上现成的剩余时间，不为它多发详情请求
                    if (LeftFromList(wl, m.Index, now) is { } sd && sd > now)
                        items.Add(new HomeworkItem(Platform, c.Name, title, sd, true, url.StartsWith("http") ? url : "", "作业", Ungraded: true));
                    continue;
                }
                if (DoneMarks.Any(state.Contains) || state.Contains("已截止") || state.Contains("已过期")) continue;
                // 列表上有时直接写着「剩余 X 小时」，能省一次详情请求
                var dl = LeftFromList(wl, m.Index, now);
                if (dl == null && url.StartsWith("http"))
                {
                    if (!_deadlineCache.TryGetValue(url, out dl))
                    {
                        await Task.Delay(500);
                        var detail = await Get(h, ticket, url);
                        if (detail == null) return null;
                        dl = ParseDeadline(detail, now);
                        _deadlineCache[url] = dl;
                    }
                }
                if (dl is { } d && d > now)
                    items.Add(new HomeworkItem(Platform, c.Name, title, d, false, url.StartsWith("http") ? url : "", "作业"));
            }
        }
        return items;
    }

    private static readonly string[] DoneMarks = ["已完成", "已提交", "待批阅", "已批阅", "已批改"];

    [GeneratedRegex("<li onclick=\"goTask\\(this\\);\" data=\"([^\"]+)\"[\\s\\S]*?<p class=\"overHidden2 fl\">([^<]+)</p>[\\s\\S]*?<p class=\"status fl\">([^<]*)</p>")]
    private static partial Regex WorkItem();

    private static List<Course> ParseCourses(string html)
    {
        var seen = new HashSet<string>();
        var list = new List<Course>();
        foreach (Match m in Regex.Matches(html, @"stucoursemiddle\?courseid=(\d+)&(?:amp;)?clazzid=(\d+)&(?:amp;)?cpi=(\d+)"))
        {
            var key = m.Value;
            if (!seen.Add(key)) continue;
            var seg = html.Substring(m.Index + m.Length, Math.Min(2500, html.Length - m.Index - m.Length));
            if (seg.Contains("课程已结束")) continue;
            var nm = Regex.Match(seg, "course-name[^>]*title=\"([^\"]+)\"");
            list.Add(new Course(nm.Success ? WebUtility.HtmlDecode(nm.Groups[1].Value) : "学习通课程", m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
        }
        return list;
    }

    // 「剩余 1天3小时」「剩余5小时20分钟」之类，只在这条作业的 li 片段里找
    private static DateTime? LeftFromList(string html, int at, DateTime now)
    {
        var end = html.IndexOf("</li>", at, StringComparison.Ordinal);
        var seg = end > at ? html[at..end] : "";
        var m = Regex.Match(seg, @"剩余\s*(?:(\d+)\s*天)?\s*(?:(\d+)\s*小时)?\s*(?:(\d+)\s*分)?");
        if (!m.Success || m.Length <= 2 || (!m.Groups[1].Success && !m.Groups[2].Success && !m.Groups[3].Success)) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return now.AddDays(G(1)).AddHours(G(2)).AddMinutes(G(3));
    }

    // 详情页「作答时间: 10-08 18:51 至 11-20 23:59」（无年份，取离今天最近的那一年）
    public static DateTime? ParseDeadline(string html, DateTime now)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ");
        var m = Regex.Match(text, @"(?:作答时间|截止时间)[:：]?\s*(?:(\d{4})-)?(\d{1,2}-\d{1,2}\s+\d{1,2}:\d{2})?\s*至\s*(?:(\d{4})-)?(\d{1,2})-(\d{1,2})\s+(\d{1,2}):(\d{2})");
        if (!m.Success) return null;
        int mo = int.Parse(m.Groups[4].Value), d = int.Parse(m.Groups[5].Value), hh = int.Parse(m.Groups[6].Value), mm = int.Parse(m.Groups[7].Value);
        if (m.Groups[3].Success) try { return new DateTime(int.Parse(m.Groups[3].Value), mo, d, hh, mm, 0); } catch { return null; }
        DateTime? best = null;
        foreach (var y in new[] { now.Year - 1, now.Year, now.Year + 1 })
        {
            try
            {
                var c = new DateTime(y, mo, d, hh, mm, 0);
                if (best == null || Math.Abs((c - now).TotalDays) < Math.Abs((best.Value - now).TotalDays)) best = c;
            }
            catch { }
        }
        return best;
    }

    private static string Hidden(string html, string id)
    {
        var m = Regex.Match(html, $"id=\"{id}\"[^>]*value=\"([^\"]*)\"");
        if (!m.Success) m = Regex.Match(html, $"value=\"([^\"]*)\"[^>]*id=\"{id}\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static void Dump(string name, string text)
    {
        if (!App.DebugDumps) return;
        try { Directory.CreateDirectory(DebugDir); File.WriteAllText(Path.Combine(DebugDir, "xxt-" + name), text); } catch { }
    }
}
