using System.Text;
using Qisland.Core;

// Qisland 4.3 / 5.0 新增的核心解析逻辑自检：AI 额度解析与阈值防抖、IMAP 验证码（MIME 解码 + 假服务器走一遍只读流程）、外设电量；
// 5.0 零代码 DIY：收起胶囊编排、组件顺序迁移、theme.json 校验钳位与差异合并、快捷按钮的网址 / 路径 / 本机地址校验。
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

// ───────────── 4.3 第二波：封面取色 ─────────────
{
    byte[] Img(int w, int h, Func<int, int, (byte r, byte g, byte b)> f)
    {
        var a = new byte[w * h * 4];
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var (r, g, b) = f(x, y); var i = (y * w + x) * 4; a[i] = b; a[i + 1] = g; a[i + 2] = r; a[i + 3] = 255; }
        return a;
    }
    // 大面积深蓝 + 小块橙：主色应是蓝
    var blue = Img(64, 64, (x, y) => x < 56 ? ((byte)30, (byte)60, (byte)160) : ((byte)240, (byte)140, (byte)20));
    var d = ArtColor.Dominant(blue, 64, 64, 64 * 4);
    Check(d is { } dc && dc.B > dc.R && dc.B > dc.G, "封面主色取面积大的蓝");
    // 黑白灰封面：不上色
    Check(ArtColor.Dominant(Img(32, 32, (x, y) => ((byte)(x * 8), (byte)(x * 8), (byte)(x * 8))), 32, 32, 128) == null, "黑白封面不取色");
    Check(ArtColor.Dominant(new byte[10], 32, 32, 128) == null, "像素不够不崩");
    // 红色跨 0°：两边的红合成一簇
    var red = ArtColor.Dominant(Img(32, 32, (x, y) => x % 2 == 0 ? ((byte)220, (byte)30, (byte)50) : ((byte)220, (byte)50, (byte)30)), 32, 32, 128);
    Check(red is { } rc && rc.R > 150 && rc.G < 80, "跨 0° 的红");

    // 钳位：各种极端输入在深色 / 浅色岛上都落在柔和区间，并且和底色 ≥ 3:1
    var extremes = new[] { new Rgb(255, 255, 0), new Rgb(0, 0, 255), new Rgb(10, 0, 20), new Rgb(255, 0, 255), new Rgb(0, 255, 0), new Rgb(250, 250, 250), new Rgb(128, 0, 0) };
    foreach (var c in extremes)
    {
        var dk = ArtColor.Soften(c, true);
        var (_, s1, l1) = ArtColor.ToHsl(dk);
        Check(ArtColor.Contrast(dk, ArtColor.DarkBase) >= 3.0 && l1 <= 0.80 && s1 <= 0.62, $"深色岛钳位 {c}→{dk}");
        var lt = ArtColor.Soften(c, false);
        Check(ArtColor.Contrast(lt, ArtColor.LightBase) >= 3.0, $"浅色岛钳位 {c}→{lt}");
        // 光晕最浓处（26%）叠在深色底上，白字对比度仍 ≥ 7:1（AAA）
        var glow = ArtColor.Over(dk, 0.26, ArtColor.DarkBase);
        Check(ArtColor.Contrast(new Rgb(0xF5, 0xF5, 0xF7), glow) >= 7, $"光晕下白字对比度 {c}");
        // 浅色主题光晕 18% 叠在白底上，深色字 ≥ 7:1
        var glowL = ArtColor.Over(lt, 0.18, ArtColor.LightBase);
        Check(ArtColor.Contrast(new Rgb(0x1D, 0x1D, 0x1F), glowL) >= 7, $"浅色光晕下黑字对比度 {c}");
    }
    Eq(ArtColor.FromHsl(0, 1, 0.5), new Rgb(255, 0, 0), "HSL→RGB 红");
    var (hh, ss, ll) = ArtColor.ToHsl(new Rgb(0, 128, 255));
    Check(Math.Abs(hh - 210) < 1 && ss > 0.99 && Math.Abs(ll - 0.5) < 0.01, "RGB→HSL");
    Check(Math.Abs(ArtColor.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)) - 21) < 0.01, "黑白对比度 21");
}

// ───────────── 4.3 第二波：逐字歌词 ─────────────
{
    // 增强 LRC
    var e = Lrc.Parse("[00:10.00]<00:10.00>你<00:10.50>好<00:11.00>世界<00:12.00>\n[00:13.00]<00:13.00>再见\n[00:15.00]普通的一句");
    Eq(e.Count, 3, "增强 LRC 行数");
    Eq(e[0].Text, "你好世界", "增强 LRC 去掉逐字时间");
    Check(e[0].Words is { Count: 3 } w0 && w0[2].Text == "世界" && w0[2].Duration == TimeSpan.FromSeconds(1), "增强 LRC 逐字");
    Check(e[1].Words is { Count: 1 } w1 && w1[0].Duration == TimeSpan.FromSeconds(2), "最后一个字唱到下一句");
    Check(e[2].Words == null, "普通行没有逐字");
    Eq(Lrc.WordAt(e[0].Words!, TimeSpan.FromSeconds(9.9)), (-1, 0.0), "逐字：还没开始");
    Eq(Lrc.WordAt(e[0].Words!, TimeSpan.FromSeconds(10.25)), (0, 0.5), "逐字：第一个字唱一半");
    Eq(Lrc.WordAt(e[0].Words!, TimeSpan.FromSeconds(12.5)), (3, 0.0), "逐字：唱完");
    Check(Math.Abs(Lrc.CharProgress(e[0].Words!, TimeSpan.FromSeconds(11.5)) - 0.75) < 1e-9, "按字数的整句进度");
    // 多时间标签的重复行不挂逐字
    var rep = Lrc.Parse("[00:01.00][00:20.00]<00:01.00>啦<00:02.00>");
    Check(rep.Count == 2 && rep.All(l => l.Words == null), "重复行不挂逐字");
    // offset 也作用到逐字
    var off = Lrc.Parse("[offset:+500]\n[00:10.00]<00:10.00>啊<00:11.00>");
    Check(off[0].Words is { Count: 1 } ow && ow[0].At == TimeSpan.FromSeconds(9.5), "offset 作用到逐字");
    // 翻译合并保留逐字
    var tr = Lrc.Parse("[00:10.00]<00:10.00>Hello <00:10.50>world<00:11.00>", "[00:10.00]你好世界");
    Check(tr[0].Translation == "你好世界" && tr[0].Words is { Count: 2 }, "翻译合并保留逐字");

    // 酷狗 KRC（解密后的文本）
    var krc = Krc.Parse("[ar:歌手]\n[ti:歌名]\n[offset:0]\n[1000,2000]<0,500,0>一<500,500,0>二<1000,1000,0>三四\n[4000,1000]<0,1000,0>五");
    Eq(krc.Count, 2, "KRC 行数");
    Eq(krc[0].Text, "一二三四", "KRC 整句");
    Check(krc[0].Words is { Count: 3 } kw && kw[1].At == TimeSpan.FromMilliseconds(1500) && kw[2].Duration == TimeSpan.FromMilliseconds(1000), "KRC 逐字时间");
    var krcOff = Krc.Parse("[offset:200]\n[1000,500]<100,400,0>啊");
    Check(krcOff[0].At == TimeSpan.FromMilliseconds(800) && krcOff[0].Words![0].At == TimeSpan.FromMilliseconds(900), "KRC offset");
}

// ───────────── 日历：只有 DURATION 的日程 ─────────────
{
    Eq(CalendarIcs.ParseDuration("PT1H30M"), TimeSpan.FromMinutes(90), "DURATION PT1H30M");
    Eq(CalendarIcs.ParseDuration("P1DT2H"), TimeSpan.FromHours(26), "DURATION P1DT2H");
    Eq(CalendarIcs.ParseDuration("P2W"), TimeSpan.FromDays(14), "DURATION P2W");
    Check(CalendarIcs.ParseDuration("-PT15M") == null && CalendarIcs.ParseDuration("P") == null && CalendarIcs.ParseDuration("PT") == null && CalendarIcs.ParseDuration("abc") == null, "坏 DURATION");
    var day = DateTime.Today.AddDays(1);
    var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a1\r\nSUMMARY:组会\r\nDTSTART:" + day.ToString("yyyyMMdd") + "T140000\r\nDURATION:PT45M\r\nEND:VEVENT\r\nEND:VCALENDAR";
    var ev = CalendarIcs.Expand(ics, DateTime.Today, DateTime.Today.AddDays(3), TimeZoneInfo.Local);
    Check(ev.Count == 1 && ev[0].End - ev[0].Start == TimeSpan.FromMinutes(45), "只有 DURATION 的日程按时长结束");
}

// ───────────── 4.3 第三波：更新包挑选 ─────────────
{
    string? PickZip(bool sc, params string[] names) { string? z = null; var rank = -1; foreach (var n in names) { var k = ReleaseAsset.Rank(n, sc); if (k >= 0 && k >= rank) { rank = k; z = n; } } return z; }
    Eq(PickZip(false, "Qisland-4.3.0-full-x64.zip", "Qisland-4.3.0-win-x64.zip"), "Qisland-4.3.0-win-x64.zip", "依赖运行时：拿 win-x64 包");
    Eq(PickZip(false, "Qisland-4.3.0-win-x64.zip", "Qisland-4.3.0-full-x64.zip"), "Qisland-4.3.0-win-x64.zip", "依赖运行时：顺序反过来也不拿 full");
    Eq(PickZip(true, "Qisland-4.3.0-win-x64.zip", "Qisland-4.3.0-full-x64.zip"), "Qisland-4.3.0-full-x64.zip", "自带运行时：拿 full 包");
    Check(PickZip(true, "Qisland-4.2.0-win-x64.zip") == null, "自带运行时：没有 full 包就不自动更新");
    Eq(PickZip(false, "notes.txt", "Qisland.zip"), "Qisland.zip", "旧规则：没有 win-x64 时用第一个 zip");
    // 4.2.0 及以前的规则（第一个 zip 或带 win-x64 的）永远拿不到 full 包
    string? Old(params string[] names) { string? z = null; foreach (var n in names) { if (!n.EndsWith(".zip")) continue; if (z == null || n.Contains("win-x64")) z = n; } return z; }
    Eq(Old("Qisland-4.3.0-full-x64.zip", "Qisland-4.3.0-win-x64.zip"), "Qisland-4.3.0-win-x64.zip", "旧更新器不会误拿 full 包");
    Check(!"Qisland-4.3.0-full-x64.zip".Contains("win-x64"), "full 包名不含 win-x64");
}

// ───────────── 5.0 零代码 DIY：收起胶囊编排 ─────────────
{
    var def = CapsuleLayout.Parse(null);
    Check(def.IsDefault(CapsuleState.Idle) && def.IsDefault(CapsuleState.Playing), "没有文件 = 4.3 原样（默认编排）");
    Eq(string.Join(",", def.Shown(CapsuleState.Idle, CapsuleSlot.Left)), "clock", "默认空闲左槽是时间");
    Eq(string.Join(",", def.Shown(CapsuleState.Idle, CapsuleSlot.Right)), "quota,weather", "默认空闲右槽：额度环 + 天气");
    Eq(string.Join(",", def.Shown(CapsuleState.Playing, CapsuleSlot.Right)), "bars", "默认播放右槽只有频谱");
    Check(!def.IsOn(CapsuleState.Playing, "clock"), "播放时的时间默认关");

    var round = CapsuleLayout.Parse(def.Serialize());
    Check(round.Idle.SequenceEqual(def.Idle) && round.Playing.SequenceEqual(def.Playing), "编排序列化往返一致");

    var l = CapsuleLayout.Parse("idle.left = weather\nidle.center = clock, -stats\nidle.right = quota\nplaying.right = clock, bars\nplaying.left = cover\n");
    Eq(string.Join(",", l.Shown(CapsuleState.Idle, CapsuleSlot.Center)), "clock,countdown", "自定义：时间居中，隐藏的系统状态不显示，漏写的倒计时补在后面");
    Check(l.Idle.Any(e => e.Id == "countdown" && e.On && e.Slot == CapsuleSlot.Center), "文件里漏掉的项按默认位置补上（倒计时回到中间）");
    Eq(string.Join(",", l.Shown(CapsuleState.Playing, CapsuleSlot.Right)), "clock,bars", "播放右槽按文件顺序");
    Check(l.IsOn(CapsuleState.Playing, "title") && l.Playing.First(e => e.Id == "title").Slot == CapsuleSlot.Center, "歌名漏写也补回中间");
    var re = CapsuleLayout.Parse(l.Serialize());
    Check(re.Idle.SequenceEqual(l.Idle) && re.Playing.SequenceEqual(l.Playing), "自定义编排往返一致");
    var g = CapsuleLayout.Default();
    g.Move(CapsuleState.Idle, 0, 1);   // 时间往下拖进中间那一组
    Check(g.Idle[0].Id == "countdown" && g.Idle[1] is { Id: "clock", Slot: CapsuleSlot.Center }, "拖过组界就跟着换槽位");
    g.Set(CapsuleState.Idle, "clock", slot: CapsuleSlot.Right);
    Check(g.Idle[^1] is { Id: "clock", Slot: CapsuleSlot.Right } && g.Idle.Select(e => e.Slot).SequenceEqual(g.Idle.Select(e => e.Slot).Order()), "改槽位后列表仍按左 / 中 / 右分组");
    var pl = CapsuleLayout.Default();
    pl.Move(CapsuleState.Playing, 1, 0);
    Check(pl.Playing.First(e => e.Id == "title").Slot == CapsuleSlot.Center && pl.Playing[0].Id == "cover", "歌名拖不出中间槽位");

    var junk = CapsuleLayout.Parse("idle.left = clock, clock, evil, ../x\nfoo.bar = clock\nidle.middle = weather\n# idle.left = weather\nIDLE.RIGHT = Weather");
    Eq(junk.Idle.Count(e => e.Id == "clock"), 1, "重复项只留一个");
    Check(junk.Idle.All(e => CapsuleLayout.Catalog(CapsuleState.Idle).Any(c => c.Id == e.Id)), "不认识的项 / 槽位 / 状态都忽略");
    Check(junk.Idle.First(e => e.Id == "weather").Slot == CapsuleSlot.Right, "大小写不敏感");
    var none = CapsuleLayout.Parse("idle.left = -clock, -weather, -countdown, -stats, -quota");
    Check(none.IsDefault(CapsuleState.Idle), "一项都不显示时退回默认，胶囊不会变空壳");
    var lockT = CapsuleLayout.Parse("playing.left = title, cover");
    Check(lockT.Playing.First(e => e.Id == "title").Slot == CapsuleSlot.Center, "歌名固定在中间");
    lockT.Set(CapsuleState.Playing, "title", slot: CapsuleSlot.Right);
    Check(lockT.Playing.First(e => e.Id == "title").Slot == CapsuleSlot.Center, "Set 也挪不走歌名");
    var mv = CapsuleLayout.Default();
    mv.Move(CapsuleState.Idle, 0, 4);
    Eq(mv.Idle[^1].Id, "clock", "拖拽排序：第一项挪到最后");
    mv.Move(CapsuleState.Idle, 4, 99);
    Eq(mv.Idle[^1].Id, "clock", "越界目标夹住");
    Check(CapsuleLayout.Parse("idle.left = clock\r\nidle.right = weather\r\n").IsOn(CapsuleState.Idle, "weather"), "Windows 换行");
}

// ───────────── 5.0：组件页顺序（widgets.txt 旧文件迁移） ─────────────
{
    string[] known = ["weather", "school", "quick", "todo", "clip", "calendar", "timer", "health", "system", "devices"];
    var old = WidgetOrder.Parse(["# Qisland 组件页", "weather", "-todo", "clip", "calendar", "timer", "system"], known);
    Eq(string.Join(",", old.Select(w => w.Id)), "weather,school,quick,todo,clip,calendar,timer,health,system,devices", "旧文件：新组件插在默认位置，顺序保留");
    Check(!old.First(w => w.Id == "todo").On, "旧文件里隐藏的仍然隐藏");
    Check(old.First(w => w.Id == "quick").On, "新加的快捷按钮默认显示（没按钮时卡片自己不占位）");
    var custom = WidgetOrder.Parse(["system", "weather", "-clip", "bogus", "SYSTEM"], known);
    Eq(custom[0].Id, "system", "自定义顺序保留");
    Eq(custom.Count, known.Length, "不认识的行忽略、重复的去掉、缺的补齐");
    var lines = WidgetOrder.Serialize(custom, known);
    var back = WidgetOrder.Parse(lines, known);
    Check(back.SequenceEqual(custom), "widgets.txt 往返一致");
    var mv = custom.ToList(); WidgetOrder.Move(mv, 0, 2);
    Eq(mv[2].Id, "system", "拖拽：0 → 2");
    WidgetOrder.Move(mv, -1, 3);
    Eq(mv[2].Id, "system", "非法来源不动");
}

// ───────────── 5.0：theme.json 主题包 ─────────────
{
    Eq(Argb.Parse("#0A84FF"), new Argb(0xFF, 0x0A, 0x84, 0xFF), "颜色 #RRGGBB");
    Eq(Argb.Parse("#0A84FF80"), new Argb(0x80, 0x0A, 0x84, 0xFF), "颜色 #RRGGBBAA（CSS 写法）");
    Eq(Argb.Parse("#fff"), new Argb(0xFF, 0xFF, 0xFF, 0xFF), "颜色 #RGB");
    Check(Argb.Parse("red") == null && Argb.Parse("#12345") == null && Argb.Parse("#GGGGGG") == null && Argb.Parse("rgb(1,2,3)") == null, "非法颜色不认");
    Check(Math.Abs(Argb.Contrast(new Argb(255, 0, 0, 0), new Argb(255, 255, 255, 255)) - 21) < 0.01, "黑白对比度 21");

    var p = ThemePack.Parse("{\"name\":\"测试\",\"material\":\"solid\",\"background\":\"#101018\",\"text\":\"#F0F0F0\",\"accent\":\"#8C7DFF\",\"radius\":28,\"opacity\":0.9}", out var err, out var warn);
    Check(p != null && err == null && warn.Count == 0, "合法主题包");
    Eq(p!.Radius, 28.0, "圆角");
    Check(p.DarkBase, "深色底 → 深色外观");

    var bad = ThemePack.Parse("{\"name\":\"坏\",\"material\":\"solid\",\"background\":\"#10101800\",\"text\":\"#111111\",\"radius\":999,\"opacity\":0,\"blur\":-3,\"card\":\"#FFFFFFFF\",\"accent\":\"javascript:alert(1)\",\"font\":\"Arial; } <script>\",\"evil\":1}", out err, out warn);
    Check(bad != null, "坏字段不整份拒绝");
    Eq(bad!.Background!.Value.A, (byte)0xE6, "纯色底透明度下限（不会透明到看不见）");
    Check(bad.Text == null, "文字和底色对比度不够时退回默认");
    Eq(bad.Radius, ThemePack.MaxRadius, "圆角上限");
    Eq(bad.Opacity, ThemePack.MinOpacity, "不透明度下限 0.6");
    Eq(bad.Blur, 0.0, "模糊下限 0");
    Eq(bad.Card!.Value.A, (byte)0xC8, "卡片透明度上限");
    Check(bad.Accent == null && bad.Font == null, "非法强调色、带脚本字符的字体名都丢掉");
    Check(warn.Count >= 8 && warn.Any(w => w.Contains("evil")), "每一处忽略 / 钳位都有说明");

    var glass = ThemePack.Parse("{\"name\":\"雾\",\"material\":\"frosted\",\"background\":\"#FFFFFF05\"}", out _, out _);
    Eq(glass!.Background!.Value.A, (byte)0x26, "玻璃底透明度下限 15%");

    string?[] fatal = [null, "", "[]", "\"x\"", "{\"author\":\"a\"}", "{name:1", "{\"name\":\"\\u0000\\u0007\"}", "{\"name\":\"" + new string('x', 40000) + "\"}"];
    Check(fatal.All(f => ThemePack.Parse(f, out var e, out _) == null && e != null), "空 / 非对象 / 没名字 / 坏 JSON / 超过 32 KB 整份拒绝");
    Check(ThemePack.Parse("{\"name\":1}", out _, out _) == null, "name 不是文字");
    var deep = "{\"name\":\"d\",\"x\":" + string.Concat(Enumerable.Repeat("[", 50)) + string.Concat(Enumerable.Repeat("]", 50)) + "}";
    Check(ThemePack.Parse(deep, out var de, out _) == null && de != null, "嵌套过深拒绝");
    var rtl = ThemePack.Parse("{\"name\":\"a\\u202Eb\\nc\"}", out _, out _);
    Eq(rtl!.Name, "abc", "名字去掉控制字符和反向字符");
    var longName = ThemePack.Parse("{\"name\":\"" + new string('名', 50) + "\"}", out _, out var lw);
    Check(longName!.Name.Length == 24 && lw.Count == 1, "名字截到 24 字");

    var basePack = ThemePack.Parse("{\"name\":\"底\",\"background\":\"#101010\",\"backgroundEnd\":\"#000000\",\"accent\":\"#FF0000\",\"radius\":20}", out _, out _)!;
    var diff = ThemePack.Parse("{\"name\":\"差异\",\"accent\":\"#00FF00\"}", out _, out _)!;
    var merged = basePack.Merge(diff);
    Check(merged.Accent == new Argb(0xFF, 0, 0xFF, 0) && merged.Radius == 20 && merged.Background == basePack.Background && merged.BackgroundEnd == basePack.BackgroundEnd, "差异合并：只盖写了的字段");
    var diff2 = ThemePack.Parse("{\"name\":\"换底\",\"background\":\"#202020\"}", out _, out _)!;
    Check(basePack.Merge(diff2).BackgroundEnd == null, "只换上端底色时不沿用旧的渐变下端");

    var exp = p.ToJson();
    var back = ThemePack.Parse(exp, out _, out var bw);
    Check(back != null && bw.Count == 0 && back == p, "导出再导入完全一致");
    Check(exp.Contains("\"测试\"") && exp.Contains("qisland-theme/1"), "导出中文不转义、带格式标记");
    Eq(ThemePack.SafeFileName("极夜/曜石..\\x"), "极夜曜石x.json", "导入文件名只留安全字符");
    Eq(ThemePack.SafeFileName("../.."), "theme.json", "空文件名兜底");
    var noBg = ThemePack.Parse("{\"name\":\"黑字\",\"text\":\"#050505\"}", out _, out _)!;
    Check(noBg.Text != null && noBg.Sanitized(null, new Argb(0xFF, 0x12, 0x12, 0x14)).Text == null, "没写底色时按当前外观的底色检查对比度");

    // 仓库里的三套官方主题：都要能无警告通过校验，文字对比度合格
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 6 && !Directory.Exists(Path.Combine(dir, "themes")); i++) dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) ?? dir;
    var themeDir = Path.Combine(dir, "themes");
    var files = Directory.Exists(themeDir) ? Directory.GetFiles(themeDir, "*.json") : [];
    Eq(files.Length, 3, "仓库 themes/ 里 3 套官方主题");
    foreach (var f in files)
    {
        var tp = ThemePack.Parse(File.ReadAllText(f), out var te, out var tw);
        Check(tp != null && te == null && tw.Count == 0, "官方主题无警告：" + Path.GetFileName(f) + (tw.Count > 0 ? " " + string.Join("；", tw) : ""));
        Check(tp?.Text != null && tp.Subtext != null && tp.Accent != null && tp.Background != null, "官方主题字段齐全：" + Path.GetFileName(f));
    }
}

// ───────────── 5.0：快捷按钮 ─────────────
{
    Check(QuickActions.SafeWebUrl("https://github.com/Qiuizi/Qisland") != null, "https 网址");
    Check(QuickActions.SafeWebUrl("http://example.com/a?b=c") != null, "http 网址");
    string[] badUrls = ["javascript:alert(1)", "file:///C:/Windows/win.ini", "ftp://x.com", "https://", "github.com", "https://a.com/ b", "https://user:pw@a.com", "ms-settings:privacy", "https://a.com\n", " ", "vbscript:x"];
    Check(badUrls.All(u => QuickActions.SafeWebUrl(u) == null), "非 http(s)、不完整、带空白 / 账号的网址都拒绝");

    Check(QuickActions.SafeLoopbackUrl("http://127.0.0.1:9840/v1/island/push") != null, "本机 127.0.0.1");
    Check(QuickActions.SafeLoopbackUrl("http://localhost:8080/run") != null, "本机 localhost");
    Check(QuickActions.SafeLoopbackUrl("http://[::1]:9840/") != null, "本机 ::1");
    string[] badLocal = ["http://192.168.1.2:80/", "http://127.0.0.1.evil.com:80/", "http://localhost.evil.com:9840/", "http://127.0.0.1/", "https://example.com:443/", "http://0.0.0.0:9840/", "http://127.0.0.2:9840/"];
    Check(badLocal.All(u => QuickActions.SafeLoopbackUrl(u) == null), "非回环地址 / 没写端口都拒绝");

    Func<string, bool> exists = s => s is @"C:\Windows\System32\notepad.exe" or @"D:\Demo";
    Check(QuickActions.ValidatePath(@"C:\Windows\System32\notepad.exe", exists) == null, "存在的程序路径");
    Check(QuickActions.ValidatePath(@"D:\Demo", exists) == null, "存在的文件夹");
    Check(QuickActions.ValidatePath(@"C:\nope.exe", exists) == "这个路径不存在", "不存在的路径拒绝");
    string[] badPaths = ["notepad.exe", @"C:\a.exe"" & calc", @"C:\a.exe | calc", @"C:\a.exe > x", @"\\server\share\x.exe", @"C:\Windows\..\x.exe", @"%COMSPEC%", "cmd /c calc", @"C:\a^b.exe", "", @"C:", "C:x.exe", "//server/share"];
    Check(badPaths.All(b => QuickActions.ValidatePath(b, _ => true) != null), "相对路径、命令行字符、网络共享、.. 都拒绝");

    var ok = new QuickAction("★", "主页", QuickKind.Url, "https://github.com");
    Check(QuickActions.Validate(ok, null) == null, "合法网页按钮");
    Check(QuickActions.Validate(ok with { Title = "" }, null) != null, "没名字不行");
    Check(QuickActions.Validate(new QuickAction("☕", "歇会", QuickKind.Notify, "站起来走走"), null) == null, "岛上提醒");
    Check(QuickActions.Validate(new QuickAction("☕", "歇会", QuickKind.Notify, new string('a', 121)), null) != null, "提醒超长");
    Check(QuickActions.Validate(new QuickAction("⚡", "推送", QuickKind.Local, "http://127.0.0.1:9840/v1/island/push", new string('a', 5000)), null) != null, "请求内容超长");
    Eq(QuickActions.CleanTitle("一二三四五六七八九十"), "一二三四五六七八", "标题最多 8 个字");
    Eq(QuickActions.CleanIcon(""), "⚡", "空图标兜底");
    Eq(QuickActions.CleanIcon("👨‍👩‍👧🚀🎵"), "👨‍👩‍👧🚀", "图标最多两个字符元素（组合 emoji 算一个）");
    Eq(QuickActions.CleanTitle("a\u0000b\nc"), "abc", "标题去控制字符");

    var list = new List<QuickAction>
    {
        ok,
        new("✎", "记事本", QuickKind.Open, @"C:\Windows\System32\notepad.exe"),
        new("☕", "歇会", QuickKind.Notify, "站起来走走"),
        new("⚡", "推送", QuickKind.Local, "http://127.0.0.1:9840/v1/island/push", "{\"title\":\"hi\"}"),
    };
    var json = QuickActions.Serialize(list);
    var back = QuickActions.Parse(json);
    Check(back.SequenceEqual(list), "快捷按钮序列化往返一致");
    Check(json.Contains("记事本") && !json.Contains("\\u"), "存盘中文不转义");
    var mixed = QuickActions.Parse("[{\"icon\":\"★\",\"title\":\"坏\",\"kind\":\"url\",\"target\":\"javascript:alert(1)\"},{\"title\":\"x\",\"kind\":\"shell\",\"target\":\"calc\"},{\"title\":\"本机\",\"kind\":\"local\",\"target\":\"http://10.0.0.1:80/\"},{\"title\":\"好\",\"kind\":\"url\",\"target\":\"https://a.com\"},null]");
    Eq(mixed.Count, 1, "读文件时坏按钮（脚本网址、未知动作、非本机地址）跳过");
    Check(QuickActions.Parse("not json").Count == 0 && QuickActions.Parse(null).Count == 0 && QuickActions.Parse("{}").Count == 0, "坏文件 = 没按钮");
    var many = QuickActions.Serialize(Enumerable.Range(0, 20).Select(i => ok with { Title = "b" + i }));
    Eq(QuickActions.Parse(many).Count, QuickActions.Max, "最多 12 个");
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
