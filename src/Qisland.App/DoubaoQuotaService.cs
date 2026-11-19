using System.IO;
using System.Net.Http;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 豆包网页会员额度（你自己选择连接的，非官方接口）：只读、约 25 分钟一次；
/// 登录失效 / 接口变了就停下并提示，不反复重试；429 按 Retry-After 退避。Cookie 用 DPAPI 加密存在 doubao.cookie，从不写日志。
/// 解析思路参考 MIT 项目岛仔（Daozai），C# 重写。
/// </summary>
public sealed class DoubaoQuotaService
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Action<Action> _post;
    private readonly AiSettings _settings;
    private CancellationTokenSource _wake = new();
    private DateTime _next = DateTime.MinValue;

    public static string CookieFile => Path.Combine(App.DataDir, "doubao.cookie");
    public DoubaoQuotaParser.Snapshot? Last { get; private set; }
    public DateTime LastOk { get; private set; }
    public string? Error { get; private set; }
    public bool LoginExpired { get; private set; }
    public bool Connected => File.Exists(CookieFile);
    public bool Fresh => Last != null && DateTime.Now - LastOk < TimeSpan.FromHours(1);
    public event Action? Changed;

    public DoubaoQuotaService(AiSettings settings, Action<Action> post) { _settings = settings; _post = post; }

    public void Start() => _ = Loop();

    private async Task Loop()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                if (_settings.DoubaoQuotaOn && Connected && !LoginExpired && DateTime.Now >= _next) await Fetch();
            }
            catch (Exception ex) { App.Log("豆包额度: " + ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), _wake.Token); }
            catch (OperationCanceledException) { _wake = new(); }
        }
    }

    public void RefreshNow() { _next = DateTime.MinValue; _wake.Cancel(); }

    public async Task<string> Connect(string raw)
    {
        var cookie = DoubaoQuotaParser.NormalizeCookie(raw);
        if (cookie == null) return "这不像豆包的 Cookie：要整行复制，里面要有一个 32 位的 sessionid";
        try { Dpapi.Save(CookieFile, cookie); } catch (Exception ex) { return "保存失败：" + ex.Message; }
        _settings.DoubaoQuotaOn = true; _settings.Save();
        LoginExpired = false; Error = null;
        await Fetch();
        return Error ?? (Last != null ? $"已连接{(Last.Plan.Length > 0 ? " · " + Last.Plan : "")}：{string.Join("，", Last.Windows.Select(w => $"{w.Label}已用 {w.UsedPercent:0}%"))}" : "已保存");
    }

    public void Disconnect()
    {
        try { if (File.Exists(CookieFile)) File.Delete(CookieFile); } catch { }
        _settings.DoubaoQuotaOn = false; _settings.Save();
        Last = null; Error = null; LoginExpired = false;
        _post(() => Changed?.Invoke());
    }

    private async Task Fetch()
    {
        _next = DateTime.Now.AddMinutes(25);
        var cookie = Dpapi.Load(CookieFile);
        if (cookie == null) { Error = "读不到保存的 Cookie，重新连接一下"; LoginExpired = true; _post(() => Changed?.Invoke()); return; }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, DoubaoQuotaParser.Endpoint) { Content = new StringContent(DoubaoQuotaParser.Body, Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Headers.TryAddWithoutValidation("Origin", "https://www.doubao.com");
            req.Headers.TryAddWithoutValidation("Referer", "https://www.doubao.com/member/quota-management");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
            using var resp = await Http.SendAsync(req);
            var code = (int)resp.StatusCode;
            if (code is 401 or 403 || code is >= 300 and < 400) throw new DoubaoQuotaParser.QuotaException(DoubaoQuotaParser.LoginError, true);
            if (code == 429)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(30);
                _next = DateTime.Now + TimeSpan.FromSeconds(Math.Clamp(wait.TotalSeconds, 30, 3600)) + TimeSpan.FromMinutes(1);
                Error = "豆包限流了，稍后再读";
                return;
            }
            if (code >= 400) { Error = $"豆包额度暂时读不到（HTTP {code}）"; _next = DateTime.Now.AddMinutes(30); return; }
            Last = DoubaoQuotaParser.Parse(await resp.Content.ReadAsStringAsync());
            LastOk = DateTime.Now; Error = null;
            App.Log($"豆包额度: {string.Join(" / ", Last.Windows.Select(w => $"{w.Label} {w.UsedPercent:0}%"))}");
        }
        catch (DoubaoQuotaParser.QuotaException ex)
        {
            Error = ex.Message;
            if (ex.Login) LoginExpired = true;          // 不再重试，等你重新连接
            else _next = DateTime.Now.AddHours(2);      // 接口变了：少打扰
            App.Log("豆包额度: " + ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Error = "网络连不上豆包"; _next = DateTime.Now.AddMinutes(10); }
        finally { _post(() => Changed?.Invoke()); }
    }
}
