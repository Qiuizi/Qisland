using Qisland.Core;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qisland.App;

/// <summary>
/// 雨课堂：微信扫码登录，只读拉取各课程里没交的作业 / 试卷和截止时间。
/// 登录得到的 Cookie 用 DPAPI 加密存在本机；不存密码，不做任何签到、答题、看课之类的写操作。
/// 20 分钟查一次，课程之间间隔 0.5 秒，对服务器很轻。
/// 学校有专属域名（如 xxx.yuketang.cn）的，把域名写进 %APPDATA%\Qisland\yuketang-host.txt；登录后域名随凭据一起保存。
///
/// 登录保持（v4）：
/// - 雨课堂的 sessionid 是 12 小时的滑动 Cookie（Max-Age=43200），有请求就会续；
///   以前只存扫码那一刻的 sessionid、从不收服务器回的 Set-Cookie，续期和换号都丢了。现在每次响应的 Set-Cookie 都合并并加密存回去。
/// - 以前任何接口回一次 401/403/50000 就判「过期」并且再也不查：校园网门户、睡眠唤醒时的抽风、某门课自己的错误都会误判。
///   现在先用一个只读的用户信息接口确认，要两次、间隔 ≥ 90 秒都确认没登录才算过期（<see cref="SessionHealth"/>）；
///   网络错误、HTML 门户页、5xx 只算临时问题，不会退出登录。
/// - 判了过期以后每 30 分钟还会轻轻确认一次，万一是误判就自己恢复；真的过期时岛上那条提示点一下就弹扫码窗口。
/// - 日志里只记接口路径、HTTP 状态、错误码和 Cookie 的名字 / 过期时间，从不记 Cookie 的值。
/// </summary>
public sealed class YuketangService
{
    public const string Platform = "雨课堂";
    public static string Host = DefaultHost();
    private static string DefaultHost()
    {
        try
        {
            var f = Path.Combine(App.DataDir, "yuketang-host.txt");
            if (File.Exists(f))
            {
                var h = File.ReadAllText(f).Trim().Replace("https://", "").Replace("http://", "").TrimEnd('/');
                if (h.Length > 0 && h.EndsWith("yuketang.cn", StringComparison.OrdinalIgnoreCase) && !h.Contains('/')) return h;
            }
        }
        catch { }
        return "www.yuketang.cn";
    }
    private readonly string _credPath;
    // 凭据文件 + 退出标记：删不掉时重启也不会恢复已退出的登录
    private readonly LoginFile _login;
    private static string DebugDir => Path.Combine(App.DataDir, "debug");
    private const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string ConfirmPath = "/v2/api/web/userinfo";

    private readonly HomeworkHub _hub;
    private readonly ActivityStore _store;
    private readonly object _state = new();
    // Cookie + 会话代数：退出登录 / 换账号换一代，旧一代的请求回来什么都不能改（Cookie、登录状态、作业、DPAPI 文件）
    private readonly SessionJar _jar = new();
    private readonly SessionHealth _health = new();
    private DateTime _next = DateTime.MinValue;
    private DateTime _lastExtendLog = DateTime.MinValue;
    private bool _busy;
    private bool _loggedCookieNames;
    private int _okButFailing;
    public string? UserName { get; private set; }
    /// <summary>当前账号身份（学校域名 + 用户 id 的哈希，见 <see cref="AccountId"/>）；没登录是空串。作业缓存按它认领。</summary>
    private string _account = "";
    public bool LoggedIn => _jar["sessionid"] != null;
    public bool Expired { get; private set; }
    public DateTime? LastOk { get; private set; }
    /// <summary>sessionid 当前的过期时间（随服务器续期更新；滑动窗口，只要还在用就会往后推）。</summary>
    public DateTime? ExpiresAt { get; private set; }
    public event Action? Changed;

    // 这次登录的凭据 id（存在加密凭据里，退出标记按它撤销）；本次运行见过的所有凭据 id，退出时一起撤销
    private string? _credId;
    private readonly HashSet<string> _knownIds = [];

    public YuketangService(HomeworkHub hub, ActivityStore store) : this(hub, store, Path.Combine(App.DataDir, "yuketang.bin")) { }

    internal YuketangService(HomeworkHub hub, ActivityStore store, string credPath)
    {
        _hub = hub; _store = store;
        _credPath = credPath;
        _login = new LoginFile(credPath);
        // 退出标记读不到（被占用）：状态未知，文件都留着、先不登录，稍后再判断
        if (LoadSaved() == CredentialCheck.Unknown)
            LogoutCleanup.DeferLoad("雨课堂", () =>
            {
                var r = LoadSaved();
                if (LoggedIn) { _hub.Adopt(Platform, _account); _next = DateTime.Now.AddSeconds(5); Changed?.Invoke(); }
                return r;
            }, () => LoggedIn);
        if (!LoggedIn) _account = "";
        // 磁盘上的作业缓存不属于这个账号（换过号、没登录、身份未知、旧格式）就不用
        _hub.Adopt(Platform, _account);
        _next = DateTime.Now.AddSeconds(20);
    }

    /// <summary>读本机保存的登录凭据。已退出（id 在退出标记里）就清理掉；标记读不到返回 Unknown，什么都不删。</summary>
    private CredentialCheck LoadSaved()
    {
        if (Dpapi.Load(_credPath) is not { } json) return CredentialCheck.Ok;
        {
            try
            {
                var o = JsonNode.Parse(json)!;
                var cid = o["cid"]?.GetValue<string>();
                var check = _login.Check(cid);
                if (check == CredentialCheck.Revoked)
                {
                    App.Log("雨课堂: 这份凭据已经退出登录过（当时文件没删掉），不恢复登录");
                    _login.Cleanup();
                    return check;
                }
                if (check == CredentialCheck.Unknown) { App.Log("雨课堂: 退出标记暂时读不到，先不恢复登录，稍后再判断（文件都保留）"); return check; }
                _credId = string.IsNullOrEmpty(cid) ? LoginFile.LegacyId : cid;
                _knownIds.Add(_credId);
                UserName = o["name"]?.GetValue<string>();
                Host = o["host"]?.GetValue<string>() ?? Host;
                var set = new CookieSet();
                if (o["cookies"] is JsonArray arr)
                    foreach (var c in arr)
                        if (c?["n"]?.GetValue<string>() is { Length: > 0 } n && c["v"]?.GetValue<string>() is { } v)
                            set.Set(n, v, c["e"]?.GetValue<long>() is { } e and > 0 ? DateTimeOffset.FromUnixTimeSeconds(e) : null);
                if (set["sessionid"] == null && o["sessionid"]?.GetValue<string>() is { Length: > 0 } sid)   // v3 及以前只存了 sessionid
                    set.Set("sessionid", sid, o["expires"]?.GetValue<long>() is { } ex and > 0 ? DateTimeOffset.FromUnixTimeSeconds(ex) : null);
                _jar.Replace(set);
                ExpiresAt = set.ExpiresOf("sessionid")?.LocalDateTime;
                // 只信 4.0.5 起按用户 id 算的身份（4.0.4 的 acct 可能是按用户名算的）；没有就是未知，不认领缓存
                _account = o["account"]?.GetValue<string>() ?? "";
                return check;
            }
            catch { if (!LoggedIn) UserName = null; return CredentialCheck.Ok; }
        }
    }

    /// <summary>加密存登录状态；gen 已经不是当前会话（期间退出 / 换号）就不写。返回是否写成功。</summary>
    private bool Save(long gen)
    {
        lock (_state)
        {
            try
            {
                var saved = _jar.TryPersist(gen, c => WriteCredential(c, Host, UserName ?? "", _account, _credId));
                if (!saved) App.Log("雨课堂: 会话已切换，丢弃旧会话的保存");
                return saved;
            }
            catch (Exception ex)
            {
                App.Log("雨课堂: 保存登录状态失败 " + ex.Message);
                return false;
            }
        }
    }

    private bool WriteCredential(CookieSet c, string host, string name, string account, string? credId)
    {
        var arr = new JsonArray();
        foreach (var (n, e) in c.All)
            arr.Add(new JsonObject { ["n"] = n, ["v"] = e.Value, ["e"] = e.Expires is { } x ? x.ToUnixTimeSeconds() : 0 });
        var o = new JsonObject
        {
            ["host"] = host, ["name"] = name, ["account"] = account, ["cid"] = credId ?? "", ["cookies"] = arr,
            // 旧版本回退时还能读
            ["sessionid"] = c["sessionid"] ?? "", ["expires"] = c.ExpiresOf("sessionid") is { } se ? se.ToUnixTimeSeconds() : 0,
        };
        if (!Dpapi.TrySave(_credPath, o.ToJsonString(), out var error))
        {
            App.Log("雨课堂: 保存登录状态失败 " + error);
            return false;
        }
        _login.Saved();
        return true;
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
    public async Task<bool> WaitLogin(Qr qr, CancellationToken ct, Action<string>? status = null)
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
                    var set = new CookieSet();
                    foreach (Cookie ck in jar.GetCookies(new Uri($"https://{Host}/")))
                        if (!ck.Expired && ck.Value.Length > 0)
                            set.Set(ck.Name, ck.Value, ck.Expires > DateTime.Now ? new DateTimeOffset(ck.Expires.ToUniversalTime(), TimeSpan.Zero) : null);
                    if (string.IsNullOrEmpty(set["sessionid"])) throw new IOException("登录成功但没拿到会话");
                    var uid = (d["UserID"] ?? d["user_id"] ?? d["UserId"] ?? d["uid"])?.ToString();
                    return TryApplyLogin(set, d["Name"]?.ToString() ?? "", uid, status);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HttpRequestException) { await Task.Delay(1500, ct); }
        }
        return false;
    }

    /// <summary>扫码确认后的登录切换。抽出来供集成测试模拟同一段流程。</summary>
    internal bool TryApplyLogin(CookieSet set, string userName, string? uid, Action<string>? status)
    {
        var newCredId = LoginFile.NewId();
        var account = AccountId.Yuketang(Host, uid, userName);
        lock (_state)
        {
            var expected = _jar.Generation;
            bool saved;
            try { saved = _jar.TryReplace(expected, set, c => WriteCredential(c, Host, userName, account, newCredId)); }
            catch (Exception ex)
            {
                App.Log("雨课堂: 新登录保存失败 " + ex.Message);
                saved = false;
            }
            if (!saved)
            {
                App.Log("雨课堂: 新登录没有写进磁盘，保留原来的账号");
                status?.Invoke("登录未保存，请重试");
                return false;
            }

            _credId = newCredId;
            _knownIds.Add(newCredId);
            ExpiresAt = set.ExpiresOf("sessionid")?.LocalDateTime;
            UserName = userName;
            _account = account;
            // 新凭据已经落盘，才撤掉旧账号的作业、重写缓存
            if (_hub.Adopt(Platform, _account)) App.Log("雨课堂: 换了账号（或身份未知），已撤下旧账号的作业");
            if (_account.Length == 0) App.Log("雨课堂: 登录结果里没有用户 id，账号身份未知：不沿用任何作业缓存");
            Expired = false;
            _health.Reset(); _okButFailing = 0;
            _next = DateTime.Now;
            App.Log($"雨课堂: 已登录 {UserName}（Cookie：{string.Join(", ", set.All.Keys)}；sessionid 到 {ExpiresAt:MM-dd HH:mm}）");
        }
        Changed?.Invoke();
        return true;
    }

    internal string? SessionIdForTests => _jar["sessionid"];

    /// <summary>退出登录。返回凭据的处理结果：Removed 删掉了；Pending 删不掉但写了退出标记（后台重试）；Failed 两样都没做成。</summary>
    public LogoutOutcome Logout()
    {
        // 先换代（在途请求回来后不能再合并 Cookie / 写盘），再删凭据文件（排在正在进行的保存之后）
        var outcome = LogoutOutcome.Removed;
        lock (_state)
        {
            _jar.Clear(() => outcome = _login.Logout(_knownIds.ToList()));
            UserName = null; Expired = false; ExpiresAt = null; _health.Reset(); _okButFailing = 0; _account = ""; _credId = null;
            _hub.Clear(Platform);
            _store.Remove("ykt:expired");
        }
        LogoutCleanup.Start("雨课堂", _login, outcome, _hub.FlushCache, () => !LoggedIn, _store, "ykt:logout-failed");
        Changed?.Invoke();
        return outcome;
    }

    // ───────────── 拉作业 ─────────────

    /// <summary>每 10 秒的计时器里调用，到点才真正去拉。</summary>
    public void Tick()
    {
        // sessionid 是 12 小时的滑动 Cookie，只要在用就会续，不再拿它提示「几小时后过期」（以前登录后一直挂着「11 小时后过期」）
        HomeworkHub.WarnExpiring(_store, "ykt:expiring", "雨课堂", null);
        if (!LoggedIn || _busy || DateTime.Now < _next) return;
        // 本机存的会话 Cookie 已经过期 / 缺失：不发请求（空 Cookie 头会直接抛错，也没意义），直接按登录过期提示扫码
        var usable = _jar.CanAuthenticate("sessionid", DateTimeOffset.UtcNow);
        if (!usable)
        {
            _next = DateTime.Now.AddMinutes(30);
            if (!Expired) MarkExpired("本机保存的会话 Cookie 已过期或缺失（不再发请求）");
            return;
        }
        if (!App.NetReady()) return;
        _ = Expired ? RecheckExpiredAsync() : RefreshAsync();
    }

    public void RefreshNow() { _next = DateTime.MinValue; Tick(); }

    /// <summary>睡眠唤醒后尽快查一次（等网络好了再查），让滑动会话尽早续上。</summary>
    public void OnResume() { if (LoggedIn && !Expired) _next = DateTime.Now.AddSeconds(30); }

    private async Task RefreshAsync()
    {
        _busy = true;
        _next = DateTime.Now.AddMinutes(20);
        var gen = _jar.Generation;
        try
        {
            var items = await Task.Run(() => Fetch(gen));
            if (items == null || !_jar.IsCurrent(gen)) return;   // 期间退出 / 换号：旧结果不进作业列表
            LastOk = DateTime.Now;
            _okButFailing = 0;
            _health.Report(AuthVerdict.Ok, DateTime.Now);
            _hub.Update(Platform, items);
            App.Log($"雨课堂: {items.Count(i => !i.Done)} 项未完成");
        }
        catch (StaleSession) { App.Log("雨课堂: 会话已切换，丢弃这次拉取"); }
        catch (AuthProblem ap) when (_jar.IsCurrent(gen))
        {
            App.Log($"雨课堂: 接口说可能没登录（{ap.Message}），用户信息接口确认一下");
            var confirm = await Task.Run(() => Confirm(gen));
            if (!_jar.IsCurrent(gen)) return;
            var v = confirm == AuthVerdict.Transient ? ap.Verdict : confirm; // 确认接口自己也说不准时，按原来的结论记一笔（仍要两次才判过期）
            if (confirm == AuthVerdict.Ok && ++_okButFailing >= 4)
            {
                // 确认接口一直说没事，业务接口却连着几次说没登录：确认接口对匿名用户也可能返回 0，不能无限信它
                MarkExpired($"业务接口连续 {_okButFailing} 次说没登录（{ap.Message}），确认接口却说正常");
            }
            else if (confirm == AuthVerdict.Ok) { App.Log($"雨课堂: 确认登录还在，只是这次接口出错（第 {_okButFailing} 次），5 分钟后再拉"); _next = DateTime.Now.AddMinutes(5); }
            else if (_health.Report(v, DateTime.Now)) MarkExpired($"连续 {_health.Strikes} 次确认未登录（{ap.Message}；确认接口：{confirm}）");
            else { App.Log($"雨课堂: 疑似掉线（第 {_health.Strikes} 次，{confirm}），2 分钟后再确认，先不提示"); _next = DateTime.Now.AddMinutes(2); }
        }
        catch (Exception ex) when (_jar.IsCurrent(gen)) { App.Log("雨课堂: 网络或服务器临时问题，5 分钟后重试 · " + ex.Message); _next = DateTime.Now.AddMinutes(5); }
        catch (Exception) { App.Log("雨课堂: 会话已切换，丢弃这次拉取的结果"); }
        finally { _busy = false; Changed?.Invoke(); }
    }

    /// <summary>请求所属的会话已经换代（退出登录 / 换账号）。</summary>
    private sealed class StaleSession() : OperationCanceledException("会话已切换");

    /// <summary>已经判了过期：每 30 分钟用只读的用户信息接口确认一次，误判就自己恢复。</summary>
    private async Task RecheckExpiredAsync()
    {
        _busy = true;
        _next = DateTime.Now.AddMinutes(30);
        var gen = _jar.Generation;
        try
        {
            if (await Task.Run(() => Confirm(gen)) == AuthVerdict.Ok && _jar.IsCurrent(gen))
            {
                App.Log("雨课堂: 登录其实还在（之前是误判），已恢复");
                Expired = false;
                _health.Report(AuthVerdict.Ok, DateTime.Now);
                _ = App.Current.Dispatcher.BeginInvoke(() => { _store.Remove("ykt:expired"); _store.Remove("ykt:expired-note"); });
                _next = DateTime.Now;
            }
        }
        catch (Exception ex) { App.Log("雨课堂: 复查登录失败 " + ex.Message); }
        finally { _busy = false; Changed?.Invoke(); }
    }

    /// <summary>只读的「我是谁」接口，用来确认登录是不是真的失效。网络异常算 Transient。</summary>
    private async Task<AuthVerdict> Confirm(long gen)
    {
        try
        {
            using var h = NewHttp();
            var (_, v, _) = await Send(h, gen, ConfirmPath, null);
            return v;
        }
        catch (Exception ex) when (ex is not AuthProblem and not StaleSession) { App.Log("雨课堂: 确认接口网络问题 " + ex.Message); return AuthVerdict.Transient; }
    }

    private sealed class AuthProblem(AuthVerdict v, string msg) : Exception(msg) { public AuthVerdict Verdict { get; } = v; }

    /// <summary>请求一个接口：带上全部 Cookie，收下 Set-Cookie，并给出这次响应对登录状态的判断。</summary>
    private async Task<(JsonNode? body, AuthVerdict verdict, string detail)> Send(HttpClient h, long gen, string path, string? classroom)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}{path}");
        if (classroom != null) req.Headers.Add("classroom-id", classroom);
        if (!_jar.TryHeader(gen, DateTimeOffset.UtcNow, out var cookieHeader)) throw new StaleSession();
        if (cookieHeader.Length == 0) throw new AuthProblem(AuthVerdict.Rejected, "本机没有可用的会话 Cookie");
        req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        req.Headers.Add("xtbz", "ykt");
        using var res = await h.SendAsync(req);
        if (!_jar.IsCurrent(gen)) throw new StaleSession();   // 等响应期间退出 / 换号了：Set-Cookie 不收、结论不算
        var deleted = AbsorbCookies(res, gen);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? o = null;
        var isJson = false;
        try { if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) { o = JsonNode.Parse(text); isJson = true; } } catch { }
        var code = (o as JsonObject)?["code"] ?? (o as JsonObject)?["errcode"];
        var v = SessionHealth.ClassifyYuketang((int)res.StatusCode, isJson, code?.ToString(), deleted);
        var msg = (o as JsonObject)?["msg"] ?? (o as JsonObject)?["errmsg"] ?? (o as JsonObject)?["message"];
        var where = path.Split('?')[0];
        if (where.Length > 48) where = where[..48] + "…";
        var detail = $"{where} HTTP {(int)res.StatusCode}{(isJson ? "" : " 非JSON")}{(code != null ? " code " + code : "")}{(msg != null ? " " + Short(msg.ToString()) : "")}{(deleted ? " 会话Cookie被删除" : "")}";
        return (o, v, detail);
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>合并响应里的 Set-Cookie；sessionid 被续期 / 换号时加密存回去。返回服务器是否删除了 sessionid。</summary>
    private bool AbsorbCookies(HttpResponseMessage res, long gen)
    {
        if (!res.Headers.TryGetValues("Set-Cookie", out var values)) return false;
        var r = _jar.AbsorbAndPersist(gen, values, DateTimeOffset.UtcNow,
            c => WriteCredential(c, Host, UserName ?? "", _account, _credId));
        if (r.Stale) throw new StaleSession();
        var (changed, deleted, rotated, names) = (r.Changed, r.Deleted, r.Rotated, r.Names);
        if (!r.Persisted)
        {
            App.Log("雨课堂: 服务器更新了 Cookie，但写盘失败，保留原来的会话");
            _next = DateTime.Now.AddMinutes(2);
            return deleted;
        }
        ExpiresAt = r.Expires?.LocalDateTime;
        if (!_loggedCookieNames) { _loggedCookieNames = true; App.Log($"雨课堂: 服务器回的 Set-Cookie：{string.Join(", ", names.Distinct())}（只记名字）"); }
        if (deleted) return true;
        if (changed)
        {
            if (rotated) App.Log($"雨课堂: 服务器换了新的 sessionid，已加密保存（到 {ExpiresAt:MM-dd HH:mm}）");
            else if (DateTime.Now - _lastExtendLog > TimeSpan.FromHours(2)) { _lastExtendLog = DateTime.Now; App.Log($"雨课堂: 会话已续期到 {ExpiresAt:MM-dd HH:mm}"); }
        }
        return false;
    }

    /// <summary>业务接口：正常返回 JSON；可能没登录时抛 AuthProblem（由 RefreshAsync 去确认）；临时问题抛 IOException。</summary>
    private async Task<JsonNode> Get(HttpClient h, long gen, string path, string? classroom = null)
    {
        var (o, v, detail) = await Send(h, gen, path, classroom);
        return v switch
        {
            AuthVerdict.Ok => o!,
            AuthVerdict.Rejected or AuthVerdict.Suspect => throw new AuthProblem(v, detail),
            _ => throw new IOException(detail),
        };
    }

    /// <summary>可选接口（成绩页）：出错就当没有数据，不影响整次拉取。</summary>
    private async Task<JsonNode?> TryGet(HttpClient h, long gen, string path, string? classroom = null)
    {
        try { return await Get(h, gen, path, classroom); }
        catch (Exception ex) when (ex is not StaleSession) { App.Log("雨课堂: 跳过 " + ex.Message); return null; }
    }

    private void MarkExpired(string why)
    {
        if (Expired) return;
        Expired = true;
        App.Log($"雨课堂: 判定登录过期 · {why}");
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            _store.Upsert(new IslandActivity("ykt:expired", HomeworkHub.Source, "雨课堂登录过期了", "点这里扫码重新登录（只读作业，不存密码）", ActivityKind.Warning, 40), sticky: true, quiet: true);
            // 常驻那条是安静的（不顶掉时间），过期那一刻弹一下让人知道
            _store.Upsert(new IslandActivity("ykt:expired-note", HomeworkHub.Source, "雨课堂登录过期了", "点一下扫码重新登录", ActivityKind.Warning, 60, DurationSeconds: 8));
            Changed?.Invoke();
        });
    }

    private async Task<List<HomeworkItem>?> Fetch(long gen)
    {
        using var h = NewHttp();
        var courses = await Get(h, gen, "/v2/api/web/courses/list?identity=2");
        Dump("courses", courses);
        var list = courses["data"]?["list"]?.AsArray() ?? [];
        // 只看当前学期：term 取最大的那一批
        var terms = list.Select(c => (long?)Num(c?["term"])).Where(t => t > 0).ToList();
        var term = terms.Count > 0 ? terms.Max() : null;
        var items = new List<HomeworkItem>();
        foreach (var c in list)
        {
            if (c == null) continue;
            if (term != null && (long)Num(c["term"]) is var t && t > 0 && t != term) continue;
            var cid = c["classroom_id"]?.ToString();
            if (string.IsNullOrEmpty(cid)) continue;
            var course = c["course"]?["name"]?.ToString() ?? c["name"]?.ToString() ?? "";
            await Task.Delay(500);
            JsonNode log;
            try { log = await Get(h, gen, $"/v2/api/web/logs/learn/{cid}?actype=-1&page=0&offset=50&sort=-1"); }
            catch (Exception ex) when (ex is IOException || ex is AuthProblem { Verdict: AuthVerdict.Suspect })
            {
                // 课程列表刚刚正常返回，说明登录是好的：单门课的 50000 / 业务错误只跳过这门课，沿用它上次的数据
                App.Log($"雨课堂: 跳过一门课 · {ex.Message}");
                items.AddRange(_hub.Previous(Platform).Where(i => i.Course == course));
                continue;
            }

            Dump("activities-" + cid, log);
            // 学习日志里不带提交状态（作业交了但老师没批时也看不出来）。
            // 成绩页每个小节有 schedule：作业 / 考试只要交了就是 1，跟有没有批分无关
            // no_corrected_subj_count > 0：交了，但还有主观题老师没批 →「已交待批」
            Dictionary<string, (double sc, bool ungraded)>? sched = null;
            async Task<Dictionary<string, (double sc, bool ungraded)>> Schedules(string sku)
            {
                if (sched != null) return sched;
                sched = new();
                await Task.Delay(500);
                var sd = await TryGet(h, gen, $"/c27/online_courseware/schedule/score_detail/single/{sku}/0/", cid);
                Dump("schedule-" + cid, sd ?? new JsonObject());
                foreach (var l in sd?["data"]?["leaf_level_infos"]?.AsArray() ?? [])
                    if (l?["id"]?.ToString() is { Length: > 0 } id) sched[id] = (Num(l["schedule"]), Num(l["no_corrected_subj_count"]) > 0);
                return sched;
            }
            foreach (var a in log["data"]?["activities"]?.AsArray() ?? [])
            {
                if (a is not JsonObject ao) continue;
                var type = (int)Num(ao["type"]);
                // 5 = 试卷 / 作业，19 = 章节作业；其它类型只要带截止时间也收（比如考试）
                var dl = FindDeadline(ao);
                // 9 / 14 是课件和课堂；15（is_graph）是知识图谱节点，带着课程结束时间但不是作业
                if (dl == null || type is 9 or 14 or 15 || Truthy(ao["is_graph"])) continue;
                if (dl < DateTime.Now.AddDays(-1)) continue;
                var done = Truthy(ao["is_finished"]) || Truthy(ao["finished"]) || Truthy(ao["is_submit"]) || Truthy(ao["submitted"]);
                var ungraded = false;
                // 试卷 / 课堂习题（type 5 / 4）没有 content.leaf_id，成绩页里也查不到；它们自己带状态：
                // status 3 = 已提交（实测：交了的 hw1–hw11 全是 3，没交的「第二章Store Buffer」是 0），
                // progress = 已答题数，答满 problem_count 也算交了。
                // 交了但 score 还是 0、总分不为 0：多半是主观题老师还没批 →「已交待批」
                if (ao["status"] is JsonValue || ao["progress"] is JsonValue)
                {
                    var pc = Num(ao["problem_count"]);
                    var paperDone = (int)Num(ao["status"]) == 3 || (pc > 0 && Num(ao["progress"]) >= pc);
                    if (paperDone && !done) ungraded = Num(ao["score"]) <= 0 && Num(ao["total_score"]) > 0;
                    done |= paperDone;
                }
                if (ao["content"] is JsonObject co && co["leaf_id"]?.ToString() is { Length: > 0 } leaf
                    && co["sku_id"]?.ToString() is { Length: > 0 } sku
                    && (done ? sched : await Schedules(sku)) is { } sd && sd.TryGetValue(leaf, out var st))
                {
                    done |= st.sc >= 1;
                    ungraded = st.sc >= 1 && st.ungraded;
                }
                var kind = type == 5 ? "试卷" : type == 19 ? "章节作业" : "作业";
                items.Add(new HomeworkItem(Platform, course, ao["title"]?.ToString() ?? kind, dl.Value, done,
                    $"https://{Host}/v2/web/studentLog/{cid}", kind, ungraded));
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
