using System.IO;
using System.Text.Json;
using Qisland.Core;

namespace Qisland.App;

/// <summary>邮箱验证码的设置：只存账号和服务器，授权码在 Windows 凭据管理器里（Qisland/mail/imap）。</summary>
public sealed class MailOtpSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "qq";
    public string Host { get; set; } = "imap.qq.com";
    public int Port { get; set; } = 993;
    public string User { get; set; } = "";
    public int IntervalSeconds { get; set; } = 20;
    /// <summary>验证码卡片挂多久（秒），通知验证码和邮件验证码共用。</summary>
    public int CardSeconds { get; set; } = 180;

    public const string SecretName = "mail/imap";
    public static string PathName => Path.Combine(App.DataDir, "mail-otp.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private static MailOtpSettings? _current;
    public static MailOtpSettings Current => _current ??= Load();

    private static MailOtpSettings Load()
    {
        try { if (File.Exists(PathName)) return Sanitize(JsonSerializer.Deserialize<MailOtpSettings>(File.ReadAllText(PathName)) ?? new()); }
        catch (Exception ex) { App.Log("mail-otp.json 读不了，用默认设置: " + ex.GetType().Name); }
        return new();
    }

    private static MailOtpSettings Sanitize(MailOtpSettings s)
    {
        s.IntervalSeconds = Math.Clamp(s.IntervalSeconds, 15, 120);
        s.CardSeconds = Math.Clamp(s.CardSeconds, 30, 900);
        s.Port = s.Port is > 0 and < 65536 ? s.Port : 993;
        return s;
    }

    public void Save()
    {
        Sanitize(this);
        _current = this;
        try { AtomicFile.TryWrite(PathName, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Opts)), out _); }
        catch (Exception ex) { App.Log("mail-otp.json 保存失败: " + ex.GetType().Name); }
    }

    public bool HasPassword => SecretStore.Has(SecretName);
}

/// <summary>
/// 只读 IMAP 邮箱验证码：默认关；打开后保持一条只读连接，每 15–30 秒 NOOP 一次看有没有新邮件。
/// 只看连上之后新来的、10 分钟内的邮件，只取头部和正文前 24 KB（BODY.PEEK，不标已读），认出验证码就上岛，邮件内容随即丢弃。
/// 不写日志里的邮件内容、发件人和验证码；授权码不进日志、不进配置文件。登录失败就停下等你改设置，不反复重试（免得被邮箱风控）。
/// </summary>
public sealed class MailOtpService
{
    private readonly ActivityStore _store;
    private readonly Action<Action> _post;
    private CancellationTokenSource _wake = new();
    private long _lastUid = -1, _uidValidity;
    private int _failures;

    public string StatusText { get; private set; } = "没打开";
    public bool Healthy { get; private set; }
    public DateTime LastCheck { get; private set; }
    public event Action? Changed;

    public MailOtpService(ActivityStore store, Action<Action> post) { _store = store; _post = post; }

    public void Start() => _ = Loop();

    /// <summary>设置改了：断开重连（新账号从头开始，不补旧邮件）。</summary>
    public void Restart() { _lastUid = -1; _failures = 0; _authFailed = false; try { _wake.Cancel(); } catch { } }

    private bool _authFailed;

    private void SetStatus(string s, bool healthy)
    {
        if (s == StatusText && healthy == Healthy) return;
        StatusText = s; Healthy = healthy;
        _post(() => Changed?.Invoke());
    }

    private async Task Loop()
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        while (true)
        {
            if (_wake.IsCancellationRequested) _wake = new();   // 上一轮是被「设置改了」叫醒的
            var wake = _wake.Token;
            var cfg = MailOtpSettings.Current;
            if (!cfg.Enabled) { SetStatus("没打开", false); await Sleep(TimeSpan.FromMinutes(5), wake); continue; }
            if (_authFailed) { await Sleep(TimeSpan.FromMinutes(30), wake); continue; }
            var pwd = SecretStore.Load(MailOtpSettings.SecretName);
            if (string.IsNullOrEmpty(cfg.User) || string.IsNullOrEmpty(cfg.Host) || string.IsNullOrEmpty(pwd))
            {
                SetStatus("还没填完账号、服务器和授权码", false);
                await Sleep(TimeSpan.FromMinutes(5), wake); continue;
            }
            using var cli = new ImapClient();
            try
            {
                SetStatus("连接中…", false);
                await cli.ConnectAsync(cfg.Host, cfg.Port, wake);
                await cli.LoginAsync(cfg.User, pwd, wake);
                pwd = null;
                var validity = await cli.ExamineInboxAsync(wake);
                if (_lastUid < 0 || validity != _uidValidity) { _lastUid = await cli.MaxUidAsync(wake); _uidValidity = validity; }   // 已有的邮件一律不看
                _failures = 0;
                App.Log("邮箱验证码: 已连接（只读）");
                SetStatus("已连接 · 只读等待新邮件", true);
                var connectedAt = DateTime.Now;
                while (!wake.IsCancellationRequested && MailOtpSettings.Current.Enabled)
                {
                    await Poll(cli, wake);
                    LastCheck = DateTime.Now;
                    // 长连接每 25 分钟换一次，避免被服务器静默踢掉后一直以为在线
                    if (DateTime.Now - connectedAt > TimeSpan.FromMinutes(25)) break;
                    await Sleep(TimeSpan.FromSeconds(MailOtpSettings.Current.IntervalSeconds), wake);
                }
                await cli.LogoutAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) when (wake.IsCancellationRequested) { }
            catch (ImapClient.ImapException ex) when (ex.Auth)
            {
                _authFailed = true;
                App.Log("邮箱验证码: 登录失败，已停止（改设置后重试）");
                SetStatus(ex.Message, false);
            }
            catch (Exception ex)
            {
                _failures++;
                App.Log($"邮箱验证码: 连接断开（{ex.GetType().Name}），稍后重连");
                SetStatus(ex is ImapClient.ImapException ie ? ie.Message + "，稍后重连" : "连不上邮箱服务器，稍后重连", false);
                await Sleep(TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(_failures, 5)))), wake);
            }
        }
    }

    private async Task Sleep(TimeSpan t, CancellationToken wake)
    {
        try { await Task.Delay(t, wake); }
        catch (OperationCanceledException) { if (_wake.IsCancellationRequested) _wake = new(); }
    }

    private async Task Poll(ImapClient cli, CancellationToken ct)
    {
        await cli.NoopAsync(ct);
        var uids = await cli.UidsAfterAsync(_lastUid, ct);
        foreach (var uid in uids.TakeLast(5))   // 一下子来很多封时只看最新 5 封
        {
            var peek = await cli.PeekAsync(uid, 24 * 1024, ct);
            if (peek == null) continue;
            if (peek.Date is { } d && DateTime.UtcNow - d > TimeSpan.FromMinutes(10)) continue;   // 迟到的旧邮件不弹
            if (MailOtp.Extract(peek.Subject, peek.Text) is not { } code) continue;
            var sender = MailOtp.SenderName(peek.From);
            var id = "otp:mail:" + uid;
            _post(() =>
            {
                _store.Upsert(new IslandActivity(id, "otp", "验证码 " + code, (sender.Length > 0 ? sender + " · " : "") + "邮件 · 点一下复制",
                    ActivityKind.Info, 86, "\uE715", DurationSeconds: MailOtpSettings.Current.CardSeconds, ResetTtl: true, ExactDuration: true));
            });
            App.Log("邮箱验证码: 已上岛（内容不记录）");
        }
        if (uids.Count > 0) _lastUid = Math.Max(_lastUid, uids.Max());
    }

    /// <summary>「测试连接」：用填的信息连一次、只读打开收件箱、退出。不保存任何东西。</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(string host, int port, string user, string password)
    {
        using var cli = new ImapClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            await cli.ConnectAsync(host, port, CancellationToken.None);
            await cli.LoginAsync(user, password, CancellationToken.None);
            await cli.ExamineInboxAsync(CancellationToken.None);
            await cli.LogoutAsync(CancellationToken.None);
            return (true, "连接成功：已用只读方式打开收件箱");
        }
        catch (ImapClient.ImapException ex) { return (false, ex.Message); }
        catch (System.Security.Authentication.AuthenticationException) { return (false, "SSL 证书校验没通过，服务器地址可能填错了"); }
        catch (System.Net.Sockets.SocketException) { return (false, "连不上服务器：检查地址和端口，或网络是否能访问"); }
        catch (OperationCanceledException) { return (false, "连接超时"); }
        catch (Exception ex) { return (false, "连接失败（" + ex.GetType().Name + "）"); }
    }
}
