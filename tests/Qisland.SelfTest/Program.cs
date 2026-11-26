using System.Text;
using Qisland.Core;

// Qisland 4.3 新增的核心解析逻辑自检：AI 额度解析与阈值防抖、IMAP 验证码（MIME 解码 + 假服务器走一遍只读流程）、外设电量。
// 用法：dotnet run --project tests/Qisland.SelfTest   全部通过打印 SELFTEST_OK，有失败返回 1。

var failed = 0; var passed = 0;
void Check(bool cond, string name)
{
    if (cond) { passed++; return; }
    failed++; Console.WriteLine("FAIL  " + name);
}
void Eq<T>(T actual, T expected, string name)
{
    if (EqualityComparer<T>.Default.Equals(actual, expected)) { passed++; return; }
    failed++; Console.WriteLine($"FAIL  {name}: 期望 {expected}，实际 {actual}");
}

// ───────────── AI 额度 ─────────────
{
    var cred = AiUsage.ClaudeCredential("{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-x\",\"refreshToken\":\"r\",\"expiresAt\":1893456000000,\"subscriptionType\":\"max\"}}");
    Check(cred != null && cred.Token == "sk-ant-oat01-x" && cred.Extra == "max", "Claude 登录文件");
    Eq(cred!.ExpiresAt, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Claude 过期时间（毫秒）");
    Check(!cred.Expired(new DateTime(2029, 1, 1, 0, 0, 0, DateTimeKind.Utc)), "Claude 未过期");
    Check(cred.Expired(new DateTime(2030, 1, 1, 0, 0, 30, DateTimeKind.Utc)), "Claude 过期前 60 秒算过期");
    Check(!cred.ToString().Contains("sk-ant"), "凭据 ToString 不带令牌");
    Check(AiUsage.ClaudeCredential("{\"claudeAiOauth\":{}}") == null, "Claude 没令牌");
    Check(AiUsage.ClaudeCredential("not json") == null, "Claude 坏 JSON");

    var c = AiUsage.ParseClaude("{\"five_hour\":{\"utilization\":6.0,\"resets_at\":\"2026-04-08T18:59:59Z\"},\"seven_day\":{\"utilization\":35,\"resets_at\":\"2026-04-14T16:59:59+00:00\"},\"seven_day_opus\":null,\"extra_usage\":{\"is_enabled\":true}}", "max");
    Check(c != null && c.Windows.Count == 2, "Claude 两个窗口（opus 为 null 跳过）");
    Eq(c!.Windows[0].UsedPercent, 6.0, "Claude 5 小时");
    Eq(c.Windows[1].Label, "本周", "Claude 本周标签");
    Eq(c.Windows[0].ResetsAt, new DateTime(2026, 4, 8, 18, 59, 59, DateTimeKind.Utc), "Claude 重置时间");
    Check(AiUsage.ParseClaude("{\"error\":{\"type\":\"permission_error\"}}") == null, "Claude 403 体");
    Eq(AiUsage.ParseClaude("{\"five_hour\":{\"utilization\":130}}")!.Windows[0].UsedPercent, 100.0, "Claude 超过 100 夹住");

    // Cursor：JWT 的 sub 拼 Cookie
    static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    var jwt = "eyJhbGciOiJIUzI1NiJ9." + B64Url("{\"sub\":\"github|user_01ABC\",\"exp\":1893456000,\"type\":\"session\"}") + ".sig";
    Eq(AiUsage.JwtSub(jwt), "github|user_01ABC", "Cursor JWT sub");
    Eq(AiUsage.JwtExpiry(jwt), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), "Cursor JWT exp");
    Eq(AiUsage.CursorCookie(jwt), "github%7Cuser_01ABC%3A%3A" + jwt, "Cursor Cookie 格式");
    Check(AiUsage.JwtSub("garbage") == null, "坏 JWT");
    var cu = AiUsage.ParseCursor("{\"billingCycleEnd\":\"2026-05-02T14:11:55.000Z\",\"membershipType\":\"pro\",\"individualUsage\":{\"plan\":{\"enabled\":true,\"used\":2000,\"limit\":2000,\"autoPercentUsed\":12,\"apiPercentUsed\":64,\"totalPercentUsed\":38}}}");
    Check(cu != null && cu.Windows.Count == 3 && cu.Plan == "pro", "Cursor 三条（总 + 两池）");
    Eq(cu!.Windows[0].UsedPercent, 38.0, "Cursor 本月总量");
    Eq(cu.Windows[2].Label, "指定模型", "Cursor 指定模型池");
    var cu2 = AiUsage.ParseCursor("{\"individualUsage\":{\"plan\":{\"used\":500,\"limit\":2000}}}");
    Eq(cu2!.Windows.Single().UsedPercent, 25.0, "Cursor 老格式按美分算");
    Check(AiUsage.ParseCursor("{\"individualUsage\":{\"overall\":{\"used\":3}}}") == null, "Cursor 企业无 plan 不硬造");

    var g = AiUsage.ParseGemini("{\"buckets\":[{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.25,\"resetTime\":\"2026-05-23T02:48:06Z\"},{\"modelId\":\"gemini-2.5-pro\",\"remainingFraction\":0.6},{\"modelId\":\"gemini-2.5-flash\",\"remainingFraction\":1,\"tokenType\":\"REQUESTS\"}]}");
    Check(g != null && g.Windows.Count == 2, "Gemini Pro + Flash");
    Eq(g!.Windows[0].UsedPercent, 75.0, "Gemini Pro 取最紧的一桶");
    Eq(g.Windows[1].UsedPercent, 0.0, "Gemini Flash 满额（缺 remainingAmount 也认）");
    Eq(AiUsage.ParseGeminiLoad("{\"cloudaicompanionProject\":\"proj-1\",\"currentTier\":{\"name\":\"Gemini Code Assist\"}}"), ("proj-1", "Gemini Code Assist"), "Gemini loadCodeAssist 字符串项目");
    Eq(AiUsage.ParseGeminiLoad("{\"cloudaicompanionProject\":{\"id\":\"p2\"}}").Project, "p2", "Gemini loadCodeAssist 对象项目");
    Check(AiUsage.GeminiCredential("{\"access_token\":\"ya29.x\",\"expiry_date\":1700000000000}")!.Expired(DateTime.UtcNow), "Gemini 过期令牌");

    var k = AiUsage.ParseKimi("{\"user\":{\"membership\":{\"level\":\"LEVEL_INTERMEDIATE\"}},\"usage\":{\"limit\":\"100\",\"used\":\"47\",\"remaining\":\"53\",\"resetTime\":\"2026-08-05T14:27:53.584450Z\"},\"limits\":[{\"window\":{\"duration\":300,\"timeUnit\":\"TIME_UNIT_MINUTE\"},\"detail\":{\"limit\":\"100\",\"used\":\"5\",\"remaining\":\"95\",\"resetTime\":\"2026-08-04T20:27:53Z\"}}]}");
    Check(k != null && k.Windows.Count == 2, "Kimi 5 小时 + 本周");
    Eq(k!.Windows[0].Label, "5 小时", "Kimi 300 分钟 → 5 小时");
    Eq(k.Windows[0].UsedPercent, 5.0, "Kimi 5 小时用量（字符串数字）");
    Eq(k.Windows[1].UsedPercent, 47.0, "Kimi 本周用量");
    Eq(AiUsage.ParseKimi("{\"usage\":{\"limit\":\"100\",\"remaining\":\"90\"}}")!.Windows[0].UsedPercent, 10.0, "Kimi 只有 remaining");
    Check(AiUsage.ParseKimi("{\"usage\":{\"limit\":\"0\"}}") == null, "Kimi 限额 0 不显示");
    Check(AiUsage.KimiCredential("{\"access_token\":\"t\",\"expires_at\":1893456000}") is { ExpiresAt: not null }, "Kimi 过期时间（秒）");

    // 阈值防抖
    var a = new QuotaAlerts();
    var r1 = new DateTime(2026, 11, 26, 18, 0, 0);
    Eq(a.Check("claude", "5h", 50, r1), 0, "50% 不提醒");
    Eq(a.Check("claude", "5h", 71, r1), 70, "过 70% 提醒");
    Eq(a.Check("claude", "5h", 75, r1), 0, "70 档不重复");
    Eq(a.Check("claude", "5h", 92, r1), 90, "过 90% 再提醒");
    Eq(a.Check("claude", "5h", 99, r1), 0, "90 档不重复");
    Eq(a.Check("claude", "5h", 95, r1.AddMinutes(5)), 0, "重置时间小抖动不算新周期");
    Eq(a.Check("claude", "5h", 93, r1.AddHours(5)), 90, "新周期重新提醒");
    Eq(a.Check("claude", "week", 72, null), 70, "不同窗口分开计");
    var saved = a.Serialize();
    var b = new QuotaAlerts(); b.Load(saved);
    Eq(b.Check("claude", "5h", 95, r1.AddHours(5)), 0, "重启后读回状态不重复轰炸");
    Eq(b.Check("claude", "week", 40, null), 0, "用量掉回去");
    Eq(b.Check("claude", "week", 71, null), 70, "掉回 10 个点以下后再上来重新提醒");
    b.Load("garbage\nx\t55\t\n");
    Eq(b.Serialize(), "", "坏状态文件忽略");
}

// ───────────── 邮件验证码 ─────────────
{
    Eq(Mime.DecodeHeader("=?UTF-8?B?5oKo55qE6aqM6K+B56CB5pivIDQ4MjkxNg==?="), "您的验证码是 482916", "编码字 B");
    Eq(Mime.DecodeHeader("=?utf-8?Q?=E9=AA=8C=E8=AF=81=E7=A0=81_123456?="), "验证码 123456", "编码字 Q（下划线是空格）");
    Eq(Mime.DecodeHeader("=?UTF-8?B?6aqM6K+B?= =?UTF-8?B?56CB?="), "验证码", "相邻编码字之间空白去掉");
    var gbk = Mime.Charset("gb2312").GetBytes("登录验证码");
    Eq(Mime.DecodeHeader("=?gb2312?B?" + Convert.ToBase64String(gbk) + "?="), "登录验证码", "GB2312 按 GB18030 解");
    Eq(Mime.DecodeHeader("Plain subject"), "Plain subject", "普通主题原样");
    Eq(Mime.Raw8(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("验证码"))), "验证码", "头部直接 8 位 UTF-8");
    Eq(Mime.Raw8(Encoding.Latin1.GetString(gbk)), "登录验证码", "头部直接 8 位 GBK");
    Eq(Encoding.UTF8.GetString(Mime.QuotedPrintable("=E9=AA=8C=E8=AF=81=\r\n=E7=A0=81: 6655")), "验证码: 6655", "QP 软换行");
    Eq(MailOtp.SenderName("\"=?UTF-8?B?5oub5ZWG6ZO26KGM?=\" <noreply@cmbchina.com>"), "招商银行", "发件人显示名");
    Eq(MailOtp.SenderName("<noreply@github.com>"), "github.com", "无显示名用域名");
    Eq(MailOtp.HtmlToText("<html><style>p{}</style><p>Your code&nbsp;is <b>739201</b></p></html>"), "Your code is 739201", "HTML 转文本");

    Eq(MailOtp.Extract("【GitHub】Your verification code", "Your verification code is: 902731. It expires in 10 minutes."), "902731", "正文里的验证码");
    Eq(MailOtp.Extract("您的验证码 556677", ""), "556677", "主题里的验证码");
    Check(MailOtp.Extract("订单已发货", "订单号 20261126 金额 128.50 元") == null, "没有验证码关键字");
    Check(MailOtp.Extract("月度账单", "本期应还 3,286.00 元，尾号 1234") == null, "账单不误认");
    var longBody = new string('x', 5000) + " 验证码 112233";
    Check(MailOtp.Extract("hi", longBody) == null, "正文只看前 1800 字");
    Eq(MailOtp.Extract("hi", "验证码 445566 " + new string('y', 5000)), "445566", "长正文开头的验证码");
    // Otp 正则回归：句号结尾可以认，小数 / 版本号 / 时间不能误认
    Eq(Otp.Extract("您的验证码是 384920。"), "384920", "中文句号结尾");
    Eq(Otp.Extract("Your code is 384920."), "384920", "英文句号结尾");
    Check(Otp.Extract("验证码 1234.5678") == null, "小数不算验证码");
    Check(Otp.Extract("验证码版本 4.3.0 已发布") == null, "版本号不算验证码");
    Check(Otp.Extract("验证码 2026-11-26 过期") == null, "日期不算验证码");

    // multipart：text/plain 是 base64，另有 html
    var plain = Convert.ToBase64String(Encoding.UTF8.GetBytes("您好，您的登录验证码为 864209，5 分钟内有效。"));
    var mp = "--b1\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 7bit\r\n\r\n<p>html 版</p>\r\n--b1\r\nContent-Type: text/plain; charset=\"utf-8\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + plain + "\r\n--b1--\r\n";
    var hdr = Mime.Headers("Content-Type: multipart/alternative;\r\n boundary=\"b1\"\r\n\r\n");
    Eq(Mime.BodyText(hdr, mp), "您好，您的登录验证码为 864209，5 分钟内有效。", "multipart 优先 text/plain");
    var htmlOnly = "--z\r\nContent-Type: text/html; charset=gbk\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
                   string.Concat(Mime.Charset("gbk").GetBytes("<b>验证码</b> 3141").Select(x => x < 0x80 && x != '=' ? ((char)x).ToString() : "=" + x.ToString("X2"))) + "\r\n--z--";
    Eq(Mime.BodyText(Mime.Headers("Content-Type: multipart/mixed; boundary=z\r\n\r\n"), htmlOnly), "验证码 3141", "只有 GBK html + QP");
    var truncated = "--b1\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" + plain[..20];
    Check(Mime.BodyText(hdr, truncated).StartsWith("您好"), "截断的 base64 只解能解的部分");
    Check(Mime.BodyText(Mime.Headers("Content-Type: image/png\r\n\r\n"), "xx") == "", "非文本不解");

    Eq(MailPreset.Guess("abc@foxmail.com")?.Host, "imap.qq.com", "foxmail 归 QQ");
    Eq(MailPreset.Guess("x@163.com")?.Port, 993, "163 端口");
    Check(MailPreset.Guess("x@company.cn") == null, "未知域名不猜");
    Eq(ImapClient.Quote("p\"a\\ss"), "\"p\\\"a\\\\ss\"", "LOGIN 参数转义");
    Eq(string.Join(",", ImapClient.ParseSearch(["* SEARCH 3 5 18", "Q005 OK"])), "3,5,18", "SEARCH 解析");

    // 假 IMAP 服务器：走一遍「登录 → ID → 只读打开 → 记最大 UID → 新邮件 → 只 PEEK 读取」，并检查从没发过改邮件的命令
    var srv = new FakeImap();
    var cli = new ImapClient();
    cli.Attach(srv);
    var ct = CancellationToken.None;
    var greet = cli.ReadLineAsync(ct).GetAwaiter().GetResult();
    Check(greet.Text.StartsWith("* OK"), "问候");
    cli.LoginAsync("me@163.com", "auth\"code", ct).GetAwaiter().GetResult();
    Check(srv.Commands.Any(x => x.StartsWith("ID (")), "网易要求的 ID 命令发了");
    Eq(cli.ExamineInboxAsync(ct).GetAwaiter().GetResult(), 1711L, "UIDVALIDITY");
    Eq(cli.MaxUidAsync(ct).GetAwaiter().GetResult(), 41L, "最大 UID");
    Eq(cli.UidsAfterAsync(41, ct).GetAwaiter().GetResult().Count, 0, "n:* 没新邮件时回的最后一封要过滤掉");
    srv.NewMail = true;
    cli.NoopAsync(ct).GetAwaiter().GetResult();
    var uids = cli.UidsAfterAsync(41, ct).GetAwaiter().GetResult();
    Eq(string.Join(",", uids), "42", "新邮件 UID");
    var peek = cli.PeekAsync(42, 16384, ct).GetAwaiter().GetResult();
    Check(peek != null, "PEEK 读到");
    Eq(peek!.Subject, "【某平台】登录验证", "PEEK 主题（编码字）");
    Eq(MailOtp.Extract(peek.Subject, peek.Text), "730144", "PEEK 正文认出验证码");
    Eq(peek.Date, new DateTime(2026, 11, 26, 12, 30, 5, DateTimeKind.Utc), "INTERNALDATE");
    cli.LogoutAsync(ct).GetAwaiter().GetResult();
    Check(srv.Commands.All(x => !x.Contains("STORE") && !x.Contains("EXPUNGE") && !x.Contains("COPY") && !x.Contains("APPEND") && !x.Contains("SELECT ")), "只读：没有改邮件的命令");
    Check(srv.Commands.Where(x => x.StartsWith("UID FETCH")).All(x => x.Contains("BODY.PEEK[")), "取信只用 BODY.PEEK");
    Check(srv.Commands.Any(x => x.StartsWith("EXAMINE")), "用 EXAMINE 只读打开");

    var bad = new FakeImap { RejectLogin = true };
    var cli2 = new ImapClient(); cli2.Attach(bad);
    cli2.ReadLineAsync(ct).GetAwaiter().GetResult();
    try { cli2.LoginAsync("me@qq.com", "wrong", ct).GetAwaiter().GetResult(); Check(false, "错误授权码应失败"); }
    catch (ImapClient.ImapException ex) { Check(ex.Auth && ex.Message.Contains("授权码") && !ex.Message.Contains("wrong"), "登录失败提示友好且不含密码"); }
}

// ───────────── 外设电量 ─────────────
{
    Eq(Peripherals.Classify("Honor Mouse CRL-M"), PeripheralKind.Mouse, "按名字认鼠标");
    Eq(Peripherals.Classify("X", appearance: 0x03C1), PeripheralKind.Keyboard, "BLE 外观 键盘");
    Eq(Peripherals.Classify("X", appearance: 0x03C2), PeripheralKind.Mouse, "BLE 外观 鼠标");
    Eq(Peripherals.Classify("X", appearance: 0x03C4), PeripheralKind.Gamepad, "BLE 外观 手柄");
    Eq(Peripherals.Classify("X", appearance: 0x0941), PeripheralKind.Headset, "BLE 外观 耳塞");
    Eq(Peripherals.Classify("X", codMajor: 5, codMinor: 0x20), PeripheralKind.Mouse, "经典蓝牙 外设/鼠标");
    Eq(Peripherals.Classify("X", codMajor: 5, codMinor: 0x10), PeripheralKind.Keyboard, "经典蓝牙 外设/键盘");
    Eq(Peripherals.Classify("X", codMajor: 5, codMinor: 0x02), PeripheralKind.Gamepad, "经典蓝牙 手柄");
    Eq(Peripherals.Classify("X", codMajor: 4), PeripheralKind.Headset, "经典蓝牙 音频");
    Eq(Peripherals.Classify("Xbox Wireless Controller"), PeripheralKind.Gamepad, "Xbox 手柄");
    Eq(Peripherals.Classify("OPPO Enco Air5 Pro"), PeripheralKind.Headset, "Enco 耳机");
    Eq(Peripherals.Classify("Something"), PeripheralKind.Other, "认不出");
    Eq(Peripherals.ShortName("A very very long device name here"), "A very very l…", "长名字截短");
    Eq(Peripherals.ShortName("  "), "无线设备", "空名字");
    Check(Peripherals.Valid(0) && Peripherals.Valid(100) && !Peripherals.Valid(255) && !Peripherals.Valid(-1) && !Peripherals.Valid(null), "电量合法范围");
    var sorted = Peripherals.Sort([new("a", "B键盘", PeripheralKind.Keyboard, 80, "ble"), new("b", "A鼠标", PeripheralKind.Mouse, 15, "ble"), new("c", "C耳机", PeripheralKind.Headset, 80, "bt")]);
    Eq(sorted[0].Id, "b", "低电量排最前");
    var lb = new LowBatteryAlerts();
    Eq(lb.Check("m", 50), 0, "50% 不提醒");
    Eq(lb.Check("m", 19), 20, "20% 档");
    Eq(lb.Check("m", 18), 0, "20% 档不重复");
    Eq(lb.Check("m", 9), 10, "10% 档");
    Eq(lb.Check("m", 5), 0, "10% 档不重复");
    Eq(lb.Check("m", 35), 0, "充上电");
    Eq(lb.Check("m", 20), 20, "充电后重新计");
    Eq(lb.Check("k", 8), 10, "直接掉到 10% 只提醒一次");
    Eq(lb.Check("k", 15), 0, "回升到 15% 不再提醒 20 档");

    Eq(CompactStats.Speed(0), "0K", "网速 0");
    Eq(CompactStats.Speed(850 * 1024), "850K", "网速 K");
    Eq(CompactStats.Speed(1.2 * 1024 * 1024), "1.2M", "网速 M 一位小数");
    Eq(CompactStats.Speed(25 * 1024 * 1024), "25M", "网速大于 10M 不带小数");
    Eq(CompactStats.Speed(double.NaN), "0K", "网速 NaN");
    Eq(CompactStats.Pct(0.456), "46%", "百分比");
    Eq(CompactStats.Parse("cpu,net"), CompactStats.Items.Cpu | CompactStats.Items.Net, "选项解析");
    Eq(CompactStats.Parse(""), CompactStats.Items.Cpu | CompactStats.Items.Mem, "默认 CPU + 内存");
    Eq(CompactStats.Serialize(CompactStats.Items.Mem | CompactStats.Items.Net), "mem,net", "选项序列化");
}

Console.WriteLine($"{passed} 项通过，{failed} 项失败");
if (failed > 0) return 1;
Console.WriteLine("SELFTEST_OK");
return 0;

/// <summary>按命令回放的假 IMAP 服务器（内存流）。</summary>
sealed class FakeImap : Stream
{
    private readonly MemoryStream _out = new();
    private readonly StringBuilder _in = new();
    public readonly List<string> Commands = [];
    public bool NewMail, RejectLogin;

    public FakeImap() => Emit("* OK [CAPABILITY IMAP4rev1 ID] ready\r\n");

    private void Emit(string s)
    {
        var pos = _out.Position;
        _out.Seek(0, SeekOrigin.End);
        var b = Encoding.Latin1.GetBytes(s);
        _out.Write(b);
        _out.Position = pos;
    }

    private void Handle(string line)
    {
        var sp = line.IndexOf(' ');
        var tag = line[..sp]; var cmd = line[(sp + 1)..];
        Commands.Add(cmd);
        if (cmd.StartsWith("LOGIN"))
        {
            Emit(RejectLogin ? $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n" : $"{tag} OK LOGIN completed\r\n");
        }
        else if (cmd == "CAPABILITY") Emit($"* CAPABILITY IMAP4rev1 ID IDLE\r\n{tag} OK done\r\n");
        else if (cmd.StartsWith("ID ")) Emit($"* ID NIL\r\n{tag} OK ID completed\r\n");
        else if (cmd == "EXAMINE INBOX") Emit($"* 41 EXISTS\r\n* OK [UIDVALIDITY 1711] UIDs valid\r\n{tag} OK [READ-ONLY] EXAMINE completed\r\n");
        else if (cmd == "UID SEARCH ALL") Emit($"* SEARCH 1 2 3 40 41\r\n{tag} OK done\r\n");
        else if (cmd.StartsWith("UID SEARCH UID ")) Emit((NewMail ? "* SEARCH 42\r\n" : "* SEARCH 41\r\n") + $"{tag} OK done\r\n");
        else if (cmd == "NOOP") Emit((NewMail ? "* 42 EXISTS\r\n" : "") + $"{tag} OK NOOP\r\n");
        else if (cmd.StartsWith("UID FETCH 42"))
        {
            var head = "From: =?UTF-8?B?5p+Q5bmz5Y+w?= <no-reply@example.com>\r\nSubject: =?UTF-8?B?44CQ5p+Q5bmz5Y+w44CR55m75b2V6aqM6K+B?=\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n";
            var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("您正在登录，验证码：730144，10 分钟内有效。如非本人操作请忽略。")) + "\r\n";
            Emit($"* 42 FETCH (UID 42 INTERNALDATE \"26-Nov-2026 20:30:05 +0800\" BODY[HEADER] {{{Encoding.Latin1.GetByteCount(head)}}}\r\n{head} BODY[TEXT]<0> {{{body.Length}}}\r\n{body})\r\n{tag} OK FETCH completed\r\n");
        }
        else if (cmd == "LOGOUT") Emit($"* BYE\r\n{tag} OK LOGOUT\r\n");
        else Emit($"{tag} BAD unknown\r\n");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _in.Append(Encoding.UTF8.GetString(buffer, offset, count));
        int nl;
        while ((nl = _in.ToString().IndexOf("\r\n", StringComparison.Ordinal)) >= 0)
        {
            var line = _in.ToString(0, nl);
            _in.Remove(0, nl + 2);
            Handle(line);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => _out.Read(buffer, offset, count);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
