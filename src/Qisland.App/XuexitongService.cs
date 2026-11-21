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
    private readonly string _credPath;
    // 凭据文件 + 退出标记：删不掉时重启也不会恢复已退出的登录
    private readonly LoginFile _login;
    private static string DebugDir => Path.Combine(App.DataDir, "debug");
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private static readonly Uri Root = new("https://chaoxing.com/");

    private readonly HomeworkHub _hub;
    private readonly ActivityStore _store;
    private readonly object _state = new();
    private readonly Func<CookieContainer, HttpClient> _newHttp;
    private readonly Func<int, Task> _delay;
    // 会话代数：退出登录 / 换账号换一代，旧一代的拉取结果不能写回作业列表、缓存、Cookie 和登录状态
    private readonly HomeworkSync _sync;
    private CookieContainer? _jar;
    private DateTime _next;
    private bool _busy;
    private readonly Dictionary<string, DateTime?> _deadlineCache = [];
    public string? UserName { get; private set; }
    /// <summary>当前账号身份（uid 的哈希，见 <see cref="AccountId"/>）；没登录是空串。作业缓存按它认领。</summary>
    private string _account = "";
    public bool LoggedIn => _jar != null;
    public bool Expired { get; private set; }
    public DateTime? LastOk { get; private set; }
    public string LastError { get; private set; } = "";
    public event Action? Changed;

    private sealed record Ck(string N, string V, string D, string P, long E = 0);
    /// <summary>登录 Cookie 里最早的过期时间（扫码登录后才记录；会话 Cookie 没有过期时间就是 null）。</summary>
    public DateTime? ExpiresAt { get; private set; }
    private static readonly string[] AuthCookies = ["_uid", "UID", "vc3", "_d", "uf", "p_auth_token", "xxtenc"];

    // 这次登录的凭据 id（存在加密凭据里，退出标记按它撤销）；本次运行见过的所有凭据 id，退出时一起撤销
    private string? _credId;
    private readonly HashSet<string> _knownIds = [];

    public XuexitongService(HomeworkHub hub, ActivityStore store) : this(hub, store, Path.Combine(App.DataDir, "xuexitong.bin")) { }

    internal XuexitongService(
        HomeworkHub hub,
        ActivityStore store,
        string credPath,
        Func<CookieContainer, HttpClient>? httpFactory = null,
        Func<int, Task>? delay = null)
    {
        _hub = hub; _store = store;
        _credPath = credPath;
        _newHttp = httpFactory ?? NewHttp;
        _delay = delay ?? (milliseconds => Task.Delay(milliseconds));
        _login = new LoginFile(credPath);
        _sync = hub.Sync();
        // 退出标记读不到（被占用）：状态未知，文件都留着、先不登录，稍后再判断
        if (LoadSaved() == CredentialCheck.Unknown)
            LogoutCleanup.DeferLoad("学习通", () =>
            {
                var r = LoadSaved();
                if (_jar != null) { _sync.Adopt(Platform, _account); _next = DateTime.Now.AddSeconds(5); _hub.Refreshed(); Changed?.Invoke(); }
                return r;
            }, () => _jar != null);
        // 磁盘上的作业缓存不属于这个账号（换过号、没登录、身份未知、旧格式）就不用
        _sync.Adopt(Platform, _account);
        _next = DateTime.Now.AddSeconds(45);
    }

    /// <summary>读本机保存的登录凭据。已退出（id 在退出标记里）就清理掉；标记读不到返回 Unknown，什么都不删。</summary>
    private CredentialCheck LoadSaved()
    {
        if (Dpapi.Load(_credPath) is not { } json) return CredentialCheck.Ok;
        try
        {
            var doc = JsonDocument.Parse(json).RootElement;
            var cid = doc.TryGetProperty("cid", out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null;
            var check = _login.Check(cid);
            if (check == CredentialCheck.Revoked)
            {
                App.Log("学习通: 这份凭据已经退出登录过（当时文件没删掉），不恢复登录");
                _login.Cleanup();
                return check;
            }
            if (check == CredentialCheck.Unknown) { App.Log("学习通: 退出标记暂时读不到，先不恢复登录，稍后再判断（文件都保留）"); return check; }
            var jar = new CookieContainer();
            DateTime? expires = null;
            foreach (var c in doc.GetProperty("cookies").Deserialize<List<Ck>>() ?? [])
            {
                jar.Add(new Cookie(c.N, c.V, c.P, c.D));
                if (c.E > 0 && AuthCookies.Contains(c.N) && DateTimeOffset.FromUnixTimeSeconds(c.E).LocalDateTime is var t && (expires == null || t < expires)) expires = t;
            }
            UserName = doc.GetProperty("name").GetString();
            ExpiresAt = expires;
            _credId = string.IsNullOrEmpty(cid) ? LoginFile.LegacyId : cid;
            _knownIds.Add(_credId);
            // 身份每次都按 Cookie 里的 UID 重算（4.0.4 存的 acct 可能是按昵称算的，不再信）；没有 UID 就是未知，不认领缓存
            _account = AccountId.Xuexitong(Uid(jar), UserName);
            _jar = jar;
            return check;
        }
        catch { _jar = null; UserName = null; _account = ""; return CredentialCheck.Ok; }
    }

    /// <summary>超星账号 uid（登录 Cookie 里的 _uid / UID），只拿来算账号哈希，不记日志。</summary>
    private static string? Uid(CookieContainer jar) =>
        jar.GetAllCookies().FirstOrDefault(c => c.Name is "_uid" or "UID" && c.Value.Length > 0)?.Value;

    private static HttpClient NewHttp(CookieContainer jar) =>
        new(new HttpClientHandler { CookieContainer = jar, UseCookies = true, AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(25), DefaultRequestHeaders = { { "User-Agent", UA } } };

    private bool TryPersistCredential(CookieContainer jar, string? name, string? credId, out DateTime? expires)
    {
        var list = jar.GetAllCookies().Where(c => c.Domain.Contains("chaoxing") || c.Domain.Contains("cldisk"))
            .Select(c => new Ck(c.Name, c.Value, c.Domain, c.Path, c.Expires > DateTime.Now ? new DateTimeOffset(c.Expires.ToUniversalTime()).ToUnixTimeSeconds() : 0)).ToList();
        var auth = list.Where(c => c.E > 0 && AuthCookies.Contains(c.N)).Select(c => DateTimeOffset.FromUnixTimeSeconds(c.E).LocalDateTime).ToList();
        expires = auth.Count > 0 ? auth.Min() : null;
        if (!Dpapi.TrySave(_credPath, JsonSerializer.Serialize(new { name = name ?? "", cid = credId ?? "", cookies = list }), out var error))
        {
            App.Log("学习通: 保存登录状态失败 " + error);
            return false;
        }
        _login.Saved();
        return true;
    }

    /// <summary>调用方持有 _state：服务器续期 Cookie 先落盘，成功后才更新内存里的过期时间。</summary>
    private bool PersistLocked(CookieContainer jar)
    {
        if (!TryPersistCredential(jar, UserName, _credId, out var expires)) return false;
        ExpiresAt = expires;
        return true;
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
                var name = o.TryGetProperty("nickname", out var nn) ? nn.GetString() : null;
                try { name ??= await FetchName(h); } catch { }
                return TryApplyLogin(qr.Jar, name, status);
            }
            var type = o.TryGetProperty("type", out var t) ? t.ToString() : "";
            if (type == "4") status($"已扫码{(o.TryGetProperty("nickname", out var n4) ? "（" + n4.GetString() + "）" : "")}，请在手机上确认");
            else if (type == "6") { status("手机上取消了，点二维码刷新"); return false; }
            else if (type is "2" or "5") return false; // 二维码失效
            else if (type == "7") status(o.TryGetProperty("mes", out var m) ? m.GetString() ?? "" : "登录失败");
        }
        return false;
    }

    /// <summary>扫码确认后的登录切换。抽出来供集成测试模拟同一段流程。</summary>
    internal bool TryApplyLogin(CookieContainer jar, string? name, Action<string>? status)
    {
        var account = AccountId.Xuexitong(Uid(jar), name);
        var newCredId = LoginFile.NewId();
        var switched = false;
        var saved = false;
        lock (_state)
        {
            DateTime? expires = null;
            saved = _sync.TrySwitchAccount(Platform, account,
                () => TryPersistCredential(jar, name, newCredId, out expires),
                () =>
                {
                    _account = account;
                    _credId = newCredId;
                    _knownIds.Add(newCredId);
                    _jar = jar;
                    Expired = false;
                    UserName = name;
                    ExpiresAt = expires;
                    _next = DateTime.Now;
                }, out switched);
        }
        if (!saved)
        {
            App.Log("学习通: 新登录没有写进磁盘，保留原来的账号");
            status?.Invoke("登录未保存，请重试");
            return false;
        }
        if (account.Length == 0) App.Log("学习通: 登录 Cookie 里没有 UID，账号身份未知：不沿用任何作业缓存");
        if (switched) { App.Log("学习通: 换了账号，已撤下旧账号的作业"); _hub.Refreshed(); }
        App.Log($"学习通: 已登录 {UserName}");
        Changed?.Invoke();
        return true;
    }

    internal string? UidForTests => _jar == null ? null : Uid(_jar);

    private static async Task<string?> FetchName(HttpClient h)
    {
        var b = await h.GetStringAsync("https://i.chaoxing.com/base");
        var m = Regex.Match(b, "aria-label=\"账号：([^\"<]{1,20})\"");
        if (!m.Success) m = Regex.Match(b, "class=\"user-name\"[^>]*>([^<]{1,20})<");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : null;
    }

    /// <summary>退出登录。返回凭据的处理结果：Removed 删掉了；Pending 删不掉但写了退出标记（后台重试）；Failed 两样都没做成。</summary>
    public LogoutOutcome Logout()
    {
        // 先换代（在途的拉取回来后什么都不能改），再删凭据、清作业缓存（排在正在进行的提交之后）
        var outcome = LogoutOutcome.Removed;
        lock (_state)
        {
            _sync.Logout(Platform, () => outcome = _login.Logout(_knownIds.ToList()));
            _jar = null; UserName = null; Expired = false; ExpiresAt = null; _account = ""; _credId = null;
        }
        _hub.Refreshed();
        _store.Remove("xxt:expired");
        LogoutCleanup.Start("学习通", _login, outcome, _hub.FlushCache, () => _jar == null, _store, "xxt:logout-failed");
        Changed?.Invoke();
        return outcome;
    }

    // ───────────── 拉作业 ─────────────

    public void Tick()
    {
        HomeworkHub.WarnExpiring(_store, "xxt:expiring", "学习通", _jar != null && !Expired ? ExpiresAt : null);
        if (_jar == null || _busy || Expired || DateTime.Now < _next || !App.NetReady()) return;
        _ = RefreshAsync();
    }

    public void RefreshNow() { _next = DateTime.MinValue; Expired = false; Tick(); }

    internal async Task RefreshForTestAsync()
    {
        _next = DateTime.MinValue;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _busy = true;
        _next = DateTime.Now.AddMinutes(30);
        long ticket;
        CookieContainer jar;
        lock (_state)
        {
            if (_jar == null) { _busy = false; return; }
            ticket = _sync.Begin();
            jar = _jar;
        }
        try
        {
            var previous = _hub.Previous(Platform);
            var items = await Task.Run(() => Fetch(jar, ticket, previous));
            if (items == null)
            {
                if (Expired) LastError = "学习通登录已过期";
                return;
            }
            // 会话没变才提交：存 Cookie（服务器可能续期了）、更新作业列表、写缓存；退出 / 换号后的旧结果直接丢掉
            bool committed;
            lock (_state) committed = _sync.Commit(ticket, Platform, items, () => PersistLocked(jar));
            if (!committed) { App.Log("学习通: 会话已切换或凭据没保存，丢弃这次拉取的结果"); return; }
            lock (_state) { LastOk = DateTime.Now; LastError = ""; }
            _hub.Refreshed();
            App.Log($"学习通: {items.Count} 项未交");
        }
        catch (StaleSession) { App.Log("学习通: 会话已切换，丢弃这次拉取"); }
        catch (AntiSpiderException) when (_sync.IsCurrent(ticket))
        {
            LastError = "学习通暂时不可用";
            App.Log("学习通: 触发了验证页，2 小时后再试");
            _next = DateTime.Now.AddHours(2);
        }
        catch (Exception ex) when (_sync.IsCurrent(ticket))
        {
            LastError = "学习通暂时不可用";
            App.Log("学习通: " + ex.Message);
            _next = DateTime.Now.AddMinutes(10);
        }
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
        if (final.Contains("passport2.chaoxing.com/login", StringComparison.OrdinalIgnoreCase)) { MarkExpired(ticket); return null; }
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)res.StatusCode}", null, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        if (!_sync.IsCurrent(ticket)) throw new StaleSession();
        if (final.Contains("antispider", StringComparison.OrdinalIgnoreCase) || text.Contains("antispiderShowVerify")) throw new AntiSpiderException();
        if (text.Contains("<title>用户登录</title>", StringComparison.OrdinalIgnoreCase)) { MarkExpired(ticket); return null; }
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

    /// <summary>Courses 解析出了课程；Empty 确认没课；Unparsed 容器里有条目却一门都没解析出来；Unknown 认不出的页面。</summary>
    internal enum CoursePageKind { Courses, Empty, Unparsed, Unknown }
    internal sealed record CoursePage(CoursePageKind Kind, List<Course> Courses);
    internal sealed record Course(string Name, string Id, string Clazz, string Cpi);
    private sealed class UnknownPageException(string message) : Exception(message);

    private async Task<List<HomeworkItem>?> Fetch(CookieContainer jar, long ticket, IReadOnlyList<HomeworkItem> previous)
    {
        using var h = _newHttp(jar);
        var basePage = await Get(h, ticket, "https://i.chaoxing.com/base");
        if (basePage == null) return null;
        var fm = Regex.Match(basePage, "\"fid\"\\s*:\\s*\"?(\\d+)\"?");
        if (!fm.Success) fm = Regex.Match(basePage, "var fid = '(\\d+)'");
        var fid = fm.Success ? fm.Groups[1].Value : "0";
        var list = await Get(h, ticket, $"https://mooc2-ans.chaoxing.com/mooc2-ans/visit/courselistdata?courseType=1&courseFid={fid}&courseTeacherid=0&courseOrderBy=0");
        if (list == null) return null;
        Dump("courses.html", list);
        var page = ParseCoursePage(list);
        if (page.Kind == CoursePageKind.Unknown) throw new UnknownPageException("无法识别的课程页面");
        if (page.Kind == CoursePageKind.Unparsed) throw new UnknownPageException("课程列表里有条目，但一门课都没解析出来，保留旧作业");
        var courses = page.Courses.Take(20).ToList();
        var now = DateTime.Now;
        var snap = new HomeworkSnapshot(previous);
        var dumped = 0;
        foreach (var c in courses)
        {
            try
            {
                await _delay(700);
                var mid = await Get(h, ticket, $"https://mooc1.chaoxing.com/visit/stucoursemiddle?courseid={c.Id}&clazzid={c.Clazz}&cpi={c.Cpi}&ismooc2=1&v=2");
                if (mid == null) return null;
                var workEnc = Hidden(mid, "workEnc");
                if (workEnc.Length == 0)
                {
                    if (!LooksLikeCourseMiddlePage(mid)) throw new UnknownPageException("无法识别的课程详情页");
                    snap.Course(c.Id, c.Name, HomeworkCourseState.Empty);
                    continue; // 这门课没开作业
                }
                var t = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                await _delay(400);
                var wl = await Get(h, ticket, $"https://mooc1.chaoxing.com/mooc2/work/list?courseId={c.Id}&classId={c.Clazz}&cpi={c.Cpi}&ut=s&openc={Hidden(mid, "openc")}&enc={workEnc}&t={t}&stuenc={Hidden(mid, "enc")}&isdisplaytable=2");
                if (wl == null) return null;
                if (dumped++ < 2) Dump($"work-{c.Id}.html", wl);
                var matches = WorkItem().Matches(wl);
                if (matches.Count == 0)
                {
                    var kind = ClassifyEmptyWorkList(wl);
                    if (kind == HomeworkCourseState.Failed) throw new UnknownPageException("无法识别的作业列表页");
                    if (kind == HomeworkCourseState.Unparsed) App.Log($"学习通: 课程 {c.Name} 的作业列表有条目但一条都没解析出来，保留旧作业");
                    snap.Course(c.Id, c.Name, kind);
                    continue;
                }
                foreach (Match m in matches)
                {
                    var url = WebUtility.HtmlDecode(Uri.UnescapeDataString(m.Groups[1].Value));
                    var title = WebUtility.HtmlDecode(m.Groups[2].Value.Trim());
                    var state = m.Groups[3].Value.Trim();
                    if (state.Contains("待批阅"))
                    {
                        // 交了没批：只用列表上现成的剩余时间，不为它多发详情请求
                        if (LeftFromList(wl, m.Index, now) is { } sd && sd > now)
                            snap.Add(new HomeworkItem(Platform, c.Name, title, sd, true, url.StartsWith("http") ? url : "", "作业", Ungraded: true, CourseId: c.Id));
                        continue;
                    }
                    if (DoneMarks.Any(state.Contains) || state.Contains("已截止") || state.Contains("已过期")) continue;
                    // 列表上有时直接写着「剩余 X 小时」，能省一次详情请求
                    var dl = LeftFromList(wl, m.Index, now);
                    if (dl == null && url.StartsWith("http"))
                    {
                        if (!_deadlineCache.TryGetValue(url, out dl))
                        {
                            await _delay(500);
                            var detail = await Get(h, ticket, url);
                            if (detail == null) return null;
                            dl = ParseDeadline(detail, now);
                            _deadlineCache[url] = dl;
                        }
                    }
                    var fresh = new HomeworkItem(Platform, c.Name, title, dl ?? now, false, url.StartsWith("http") ? url : "", "作业", CourseId: c.Id);
                    if (dl is { } d) { if (d > now) snap.Add(fresh); }
                    else if (snap.KeepItem(fresh)) App.Log($"学习通: 课程 {c.Name} 有一份作业读不出截止时间，沿用上次的数据");
                }
                snap.Course(c.Id, c.Name, HomeworkCourseState.Parsed);
            }
            catch (Exception ex) when (ex is not StaleSession and not AntiSpiderException)
            {
                App.Log($"学习通: 课程 {c.Name} 拉取失败，保留旧作业 · {ex.GetType().Name}");
                snap.Course(c.Id, c.Name, HomeworkCourseState.Failed);   // 按课程 ID 沿用旧数据（课程改名也认得）
            }
        }
        App.Log("学习通: 本次拉取 " + snap.Summary());
        return snap.Items.ToList();
    }

    private static readonly string[] DoneMarks = ["已完成", "已提交", "待批阅", "已批阅", "已批改"];

    [GeneratedRegex("<li onclick=\"goTask\\(this\\);\" data=\"([^\"]+)\"[\\s\\S]*?<p class=\"overHidden2 fl\">([^<]+)</p>[\\s\\S]*?<p class=\"status fl\">([^<]*)</p>")]
    private static partial Regex WorkItem();

    internal static CoursePage ParseCoursePage(string html)
    {
        var courses = ParseCourses(html);
        if (courses.Count > 0) return new CoursePage(CoursePageKind.Courses, courses);
        if (!LooksLikeCoursePage(html)) return new CoursePage(CoursePageKind.Unknown, []);
        // 课程链接都在、只是全部已结束：确实没有在上的课
        if (CourseLink().IsMatch(html)) return new CoursePage(CoursePageKind.Empty, []);
        // 明确写着「暂无课程」，或容器里一个条目都没有，才算空；容器里有条目却一门都没解析出来不是空
        if (html.Contains("暂无课程", StringComparison.Ordinal) || !HasListEntries(html)) return new CoursePage(CoursePageKind.Empty, []);
        return new CoursePage(CoursePageKind.Unparsed, []);
    }

    internal static bool LooksLikeCoursePage(string html) =>
        Regex.IsMatch(html, @"class\s*=\s*[""'][^""']*\bcourse-list\b", RegexOptions.IgnoreCase)
        || html.Contains("id=\"courseList\"", StringComparison.OrdinalIgnoreCase)
        || html.Contains("暂无课程", StringComparison.Ordinal)
        || html.Contains("暂无课程数据", StringComparison.Ordinal);

    private static bool LooksLikeCourseMiddlePage(string html) =>
        html.Contains("courseid", StringComparison.OrdinalIgnoreCase)
        || html.Contains("stuenc", StringComparison.OrdinalIgnoreCase)
        || html.Contains("暂无作业", StringComparison.Ordinal);

    private static bool LooksLikeEmptyWorkList(string html) =>
        Regex.IsMatch(html, @"class\s*=\s*[""'][^""']*\bwork-list\b", RegexOptions.IgnoreCase)
        || html.Contains("id=\"workList\"", StringComparison.OrdinalIgnoreCase)
        || html.Contains("暂无作业", StringComparison.Ordinal)
        || html.Contains("暂无任务", StringComparison.Ordinal);

    [GeneratedRegex(@"stucoursemiddle\?courseid=(\d+)&(?:amp;)?clazzid=(\d+)&(?:amp;)?cpi=(\d+)")]
    private static partial Regex CourseLink();

    private static bool HasListEntries(string html) => Regex.IsMatch(html, @"<li[\s>]", RegexOptions.IgnoreCase);

    /// <summary>作业列表一条都没匹配上时：明确的空 → Empty；容器里有条目 → Unparsed（保留旧数据）；认不出 → Failed。</summary>
    internal static HomeworkCourseState ClassifyEmptyWorkList(string html)
    {
        if (html.Contains("暂无作业", StringComparison.Ordinal) || html.Contains("暂无任务", StringComparison.Ordinal)) return HomeworkCourseState.Empty;
        if (!LooksLikeEmptyWorkList(html)) return HomeworkCourseState.Failed;
        return HasListEntries(html) ? HomeworkCourseState.Unparsed : HomeworkCourseState.Empty;
    }

    private static List<Course> ParseCourses(string html)
    {
        var seen = new HashSet<string>();
        var list = new List<Course>();
        foreach (Match m in CourseLink().Matches(html))
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
