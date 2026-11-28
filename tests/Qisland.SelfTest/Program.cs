using System.Text;
using Qisland.Core;

// Qisland 4.3 / 5.0 新增的核心解析逻辑自检：AI 额度解析与阈值防抖、IMAP 验证码（MIME 解码 + 假服务器走一遍只读流程）、外设电量；
// 5.0 零代码 DIY：收起胶囊编排、组件顺序迁移、theme.json 校验钳位与差异合并、快捷按钮的网址 / 路径 / 本机地址校验；
// 5.0 插件：L2 声明式卡片（解析、白名单、内网拒绝、重定向、间隔钳位、JSON 路径、模板转义、6 种原语）、L3 分级与权限升级检测、6 个官方示例插件；
// 5.0 插件市场：Ed25519（RFC 8032 官方向量）、registry.json / revoked.json 解析与验签、撤销命中、min_app_version、下载校验、Setup.exe 脚本与发布脚本检查。
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

// ───────────── 5.0：L2 声明式卡片 / L3 分级 ─────────────
{
    static System.Text.Json.JsonElement J(string s) => System.Text.Json.JsonDocument.Parse(s).RootElement.Clone();
    var now = new DateTime(2026, 11, 28, 10, 0, 0);

    // plugin.json 解析
    var good = PluginManifest.Parse("""
    { "id": "demo-card", "name": "演示", "tier": "declarative", "icon": "★",
      "permissions": { "domains": ["api.example.com"] },
      "config_schema": [ { "key": "repo", "label": "仓库", "type": "text", "default": "a/b", "pattern": "^[a-z]+/[a-z]+$" } ],
      "sources": { "r": { "type": "https", "url": "https://api.example.com/repos/{{config.repo|path}}", "interval_seconds": 5, "headers": { "Accept": "application/json" } } },
      "card": [ { "type": "header", "title": "{{config.repo}}" }, { "type": "metric", "value": "{{r.stars|compact}}" } ] }
    """);
    Check(good.IsValid, "L2 plugin.json 解析：" + string.Join("；", good.Errors));
    Eq(good.Tier, PluginTier.Declarative, "tier = declarative");
    Eq(good.Sources[0].IntervalSeconds, 60, "刷新间隔钳到 ≥ 60 秒");
    Eq(PluginManifest.Parse("""{ "id": "x", "type": "declarative-card", "card": [ { "type": "header", "title": "t" } ] }""").Tier, PluginTier.Declarative, "type: declarative-card 也认");
    Eq(PluginManifest.Parse("""{ "id": "old", "entry": "run.ps1", "permissions": ["network"] }""").Tier, PluginTier.Process, "旧版带 entry 的插件识别为 L3");
    Eq(UrlPolicy.ClampInterval(0), CardLimits.DefaultIntervalSeconds, "没写间隔用默认 300 秒");
    Eq(UrlPolicy.ClampInterval(999999), CardLimits.MaxIntervalSeconds, "间隔上限 1 天");

    string Bad(string json) => string.Join("；", PluginManifest.Parse(json).Errors);
    Check(Bad("""{ "id": "x", "tier": "declarative", "entry": "run.ps1", "card": [ { "type": "header", "title": "t" } ] }""").Contains("entry"), "L2 不能带 entry");
    Check(Bad("""{ "id": "x", "tier": "declarative", "script": "alert(1)", "card": [ { "type": "header", "title": "t" } ] }""").Contains("script"), "L2 不能带 script");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com","b.com","c.com","d.com"] }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("最多"), "域名最多 3 个");
    foreach (var d in new[] { "127.0.0.1", "localhost", "192.168.1.1", "printer.local", "nas.lan", "[::1]", "*.github.com", "intranet" })
        Check(!PluginManifest.Parse($$"""{ "id": "x", "permissions": { "domains": ["{{d}}"] }, "card": [ { "type": "header", "title": "t" } ] }""").IsValid, "白名单拒绝 " + d);
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "https://b.com/x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("白名单"), "数据源域名必须在白名单里");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "http://a.com/x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("https"), "只允许 https");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "https://a.com:8443/x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("443"), "只允许 443 端口");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "config_schema": [ { "key": "h", "type": "text" } ], "sources": { "r": { "type": "https", "url": "https://{{config.h}}/x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("模板"), "域名部分不能用模板");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "https://u:p@a.com/x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("用户名"), "网址不能带账号密码");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "https://a.com/x", "headers": { "Host": "evil.com" } } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("Host"), "不允许覆盖 Host 头");
    Check(Bad("""{ "id": "x", "permissions": { "domains": ["a.com"] }, "sources": { "r": { "type": "https", "url": "https://a.com/x", "headers": { "Authorization": "Bearer x" } } }, "card": [ { "type": "header", "title": "t" } ] }""").Length > 0, "不允许 Authorization 头");
    Check(UrlPolicy.IsHeaderAllowed("User-Agent", "Qisland", out _) && !UrlPolicy.IsHeaderAllowed("Accept", "a\r\nX-Evil: 1", out _), "请求头值不能换行注入");
    Check(Bad("""{ "id": "x", "sources": { "r": { "type": "ftp", "url": "x" } }, "card": [ { "type": "header", "title": "t" } ] }""").Contains("不支持"), "未知数据源类型");
    Check(Bad("""{ "id": "x", "card": [ { "type": "header", "title": "{{nope.a}}" } ] }""").Contains("nope"), "引用不存在的数据源");
    Check(Bad("""{ "id": "x", "card": [ { "type": "header", "title": "{{config.k}}" } ] }""").Contains("参数"), "引用没声明的参数");
    Check(Bad("""{ "id": "x", "card": [ { "type": "header", "title": "{{now.date|eval}}" } ] }""").Contains("过滤器"), "不认识的过滤器");
    Check(Bad("""{ "id": "../evil", "card": [ { "type": "header", "title": "t" } ] }""").Contains("id"), "id 防路径穿越");
    Check(Bad("{ not json").Contains("JSON"), "坏 JSON");
    Check(Bad(new string(' ', 70000)).Length > 0, "超大 plugin.json 拒绝");
    Check(Bad("""{ "id": "x", "card": [] }""").Length > 0, "空 card 拒绝");

    // 运行时网址检查 / 内网拒绝 / 重定向
    string[] dom = ["api.example.com"];
    Check(UrlPolicy.CheckHttps("https://api.example.com/a?b=1", dom, out _, out _), "白名单内 https 通过");
    foreach (var u in new[] { "http://api.example.com/", "https://api.example.com:8443/", "https://127.0.0.1/", "https://2130706433/", "https://0x7f000001/", "https://[::1]/", "https://evil.com/", "https://api.example.com.evil.com/", "https://user@api.example.com/", "https://localhost/" })
        Check(!UrlPolicy.CheckHttps(u, dom, out _, out _), "运行时拒绝 " + u);
    var from = new Uri("https://api.example.com/a");
    Check(UrlPolicy.CheckRedirect(from, "/b", dom, 1, out var nx, out _) && nx!.AbsoluteUri == "https://api.example.com/b", "白名单内相对重定向");
    Check(!UrlPolicy.CheckRedirect(from, "https://evil.com/", dom, 1, out _, out _), "禁止跨白名单重定向");
    Check(!UrlPolicy.CheckRedirect(from, "http://api.example.com/b", dom, 1, out _, out _), "禁止降级到 http 的重定向");
    Check(!UrlPolicy.CheckRedirect(from, "/b", dom, CardLimits.MaxRedirects + 1, out _, out _), "重定向次数有上限");
    foreach (var ip in new[] { "127.0.0.1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.0.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "fe80::1", "fd00::1", "::ffff:192.168.1.1", "::ffff:127.0.0.1" })
        Check(!UrlPolicy.IsPublicAddress(System.Net.IPAddress.Parse(ip)), "建连时拒绝内网 / 回环 " + ip);
    foreach (var ip in new[] { "140.82.112.6", "1.1.1.1", "2606:4700:4700::1111", "172.32.0.1" })
        Check(UrlPolicy.IsPublicAddress(System.Net.IPAddress.Parse(ip)), "公网地址放行 " + ip);
    Eq(CardLimits.MaxBodyBytes, 256 * 1024, "响应体上限 256 KiB");
    Eq(CardLimits.RequestTimeout, TimeSpan.FromSeconds(8), "单次超时 8 秒");

    // 本机数据源：必须声明 + 逐项同意（同意由宿主管，这里查声明）
    var local = PluginManifest.Parse("""{ "id": "lan", "permissions": { "local": ["127.0.0.1:9840"] }, "sources": { "s": { "type": "local", "url": "http://127.0.0.1:9840/v1/health" } }, "card": [ { "type": "header", "title": "t" } ] }""");
    Check(local.IsValid && local.LocalSources.Single() == "127.0.0.1:9840", "本机数据源声明");
    Check(!UrlPolicy.CheckLocal("http://127.0.0.1:9841/", local.LocalSources, out _, out _), "本机数据源换端口拒绝");
    Check(!PluginManifest.Parse("""{ "id": "lan", "permissions": { "local": ["8.8.8.8:53"] }, "card": [ { "type": "header", "title": "t" } ] }""").IsValid, "公网地址不能当本机数据源");

    // JSON 路径
    var doc = J("""{ "a": { "b": [ { "n": "x" }, { "n": "y" } ] }, "k-1": 5, "list": [1, 2, 3, 4], "deep": [[[1]]] }""");
    Eq(JsonPick.Select(doc, "$.a.b[0].n").Single().GetString(), "x", "点路径 + 数组下标");
    Eq(JsonPick.Select(doc, "$.a.b[-1].n").Single().GetString(), "y", "负下标取最后一个");
    Eq(JsonPick.Select(doc, "$['k-1']").Single().GetInt32(), 5, "引号字段名");
    Eq(JsonPick.Select(doc, "$.a.b[*].n").Count, 2, "[*] 展开");
    Eq(JsonPick.Select(doc, "$.nope.x").Count, 0, "不存在的路径为空");
    Check(!JsonPick.IsValidPath("$..a", out _) && !JsonPick.IsValidPath("$.a[?(@.x)]", out _) && !JsonPick.IsValidPath("$.a[*].b[*]", out _), "不支持递归下降 / 过滤表达式 / 多个 [*]");
    Check(!JsonPick.IsValidPath(string.Concat(Enumerable.Repeat(".a", 30)), out _), "路径深度有上限");

    // 模板与转义
    var ctx = new CardContext
    {
        Config = new Dictionary<string, string> { ["repo"] = "a/b", ["q"] = "x y&z=1#frag", ["evil"] = "../../admin", ["cur"] = "USD", ["inj"] = "{{config.repo}}" },
        Data = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["r"] = J("""{ "stars": 12345, "title": "Hello\u0007\nWorld", "rates": { "USD": 0.14 }, "pushed": "2026-11-27T10:00:00+08:00", "pts": [{ "d": 10 }, { "d": 20 }, { "d": 15 }, { "d": 30 }], "long": "%%LONG%%" }""".Replace("%%LONG%%", new string('a', 5000))),
        },
        Now = now,
    };
    Eq(CardTemplate.Render("{{r.stars|compact}} ★", ctx), "12.3k ★", "compact");
    Eq(CardTemplate.Render("{{r.stars|num}}", ctx), "12,345", "千分位");
    Eq(CardTemplate.Render("{{r.rates.{cur}|inv|fixed:2}}", ctx), "7.14", "路径里引用配置 + 倒数 + 小数位");
    Eq(CardTemplate.Render("{{r.stars|div:1000|round:1}}k", ctx), "12.3k", "简单算术");
    Eq(CardTemplate.Render("{{r.stars|mul:0|percent}}", ctx), "0%", "百分比映射");
    Eq(CardTemplate.Render("{{r:$.pts[*].d|sum}} / {{r:$.pts[*].d|max}} / {{r:$.pts[*].d|delta|sign}}", ctx), "75 / 30 / +20", "数组聚合");
    Eq(CardTemplate.Render("{{r:$.pts[*].d|growth:2|percent|sign}}", ctx), "+50%", "环比");
    Eq(CardTemplate.Render("{{r.title}}", ctx), "Hello World", "控制字符清掉");
    Check(CardTemplate.Render("{{r.long}}", ctx).Length <= CardLimits.MaxText + 1, "超长字符串截断");
    Eq(CardTemplate.Render("{{r.missing}}|{{r.missing|default:无}}", ctx), "—|无", "缺数据显示 —，default 兜底");
    Eq(CardTemplate.Render("{{config.inj}}", ctx), "{{config.repo}}", "值里的 {{ }} 不会被二次展开");
    Eq(CardTemplate.Render("{{r.pushed|ago}}", ctx), "1 天前", "相对时间");
    Eq(CardTemplate.RenderUrl("https://api.example.com/repos/{{config.repo|path}}", ctx), "https://api.example.com/repos/a/b", "path 按段编码保留 /");
    Eq(CardTemplate.RenderUrl("https://api.example.com/s?q={{config.q}}", ctx), "https://api.example.com/s?q=x%20y%26z%3D1%23frag", "默认整体 URL 编码，& = # 不能注入参数");
    Eq(CardTemplate.RenderUrl("https://api.example.com/x/{{config.repo}}", ctx), "https://api.example.com/x/a%2Fb", "默认编码连 / 也转义");
    Check(CardTemplate.RenderUrl("https://api.example.com/repos/{{config.evil|path}}", ctx) == null, "path 里的 .. 拒绝");
    Check(CardTemplate.RenderUrl("https://api.example.com/{{r.missing}}", ctx) == null, "缺参数不发请求");
    var bad = new CardContext { Config = new Dictionary<string, string> { ["cur"] = "../x" }, Data = ctx.Data, Now = now };
    Eq(CardTemplate.Render("{{r.rates.{cur}}}", bad), "—", "路径占位符只接受字母数字");

    // 6 种原语校验
    IReadOnlyList<string> noS = [], noC = [];
    Check(PluginManifest.PrimitiveTypes.Length == 6, "原语正好 6 种");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "metric" }"""), noS, noC).Count > 0, "metric 缺 value");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "progress", "value": 0.5 }"""), noS, noC).Count == 0, "progress 数字值");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "sparkline", "points": [1, 2, "x"] }"""), noS, noC).Count > 0, "sparkline 只收数字");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "sparkline", "points": [1, 2, 3] }"""), noS, noC).Count == 0, "sparkline 数字数组");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "badge_row", "badges": [ {"text":"1"},{"text":"2"},{"text":"3"},{"text":"4"},{"text":"5"},{"text":"6"},{"text":"7"} ] }"""), noS, noC).Count > 0, "徽章最多 6 个");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "badge_row", "badges": [ { "text": "a", "tone": "rainbow" } ] }"""), noS, noC).Count > 0, "徽章颜色白名单");
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "action_row", "actions": [ {"label":"a","kind":"refresh"},{"label":"b","kind":"refresh"},{"label":"c","kind":"refresh"},{"label":"d","kind":"refresh"},{"label":"e","kind":"refresh"} ] }"""), noS, noC).Count > 0, "按钮最多 4 个");
    foreach (var u in new[] { "http://a.com", "javascript:alert(1)", "file:///C:/x", "ms-settings:privacy", "https://{{config.x}}/", "https://u@a.com/" })
        Check(PluginManifest.ValidatePrimitive(J($$"""{ "type": "action_row", "actions": [ { "label": "x", "kind": "open_url", "url": "{{u}}" } ] }"""), noS, noC).Count > 0, "按钮拒绝 " + u);
    foreach (var k in new[] { "run", "exec", "shell", "open_file", "post" })
        Check(PluginManifest.ValidatePrimitive(J($$"""{ "type": "action_row", "actions": [ { "label": "x", "kind": "{{k}}" } ] }"""), noS, noC).Count > 0, "按钮动作只有 open_url / refresh：拒绝 " + k);
    Check(PluginManifest.ValidatePrimitive(J("""{ "type": "chart3d" }"""), noS, noC).Count > 0, "未知原语");

    // 求值：错误态 / 加载中 / 正常
    var st = new Dictionary<string, SourceState>();
    var cfg = good.EffectiveConfig(null);
    Eq(cfg["repo"], "a/b", "默认配置");
    Eq(good.EffectiveConfig(new Dictionary<string, string> { ["repo"] = "BAD VALUE" })["repo"], "a/b", "不合法的配置退回默认值");
    var v0 = CardEvaluator.Evaluate(good, cfg, st, now);
    Check(v0.Pending && v0.Notice == "加载中…" && ((HeaderNode)v0.Nodes[0]).Status == "idle", "加载中");
    st["r"] = new SourceState(null, "超时", now);
    var v1 = CardEvaluator.Evaluate(good, cfg, st, now);
    Check(v1.Notice != null && ((HeaderNode)v1.Nodes[0]).Status == "error" && ((MetricNode)v1.Nodes[1]).Value == "—", "错误态：状态点变红、数值显示 —");
    st["r"] = new SourceState(J("""{ "stars": 2048 }"""), null, now);
    var v2 = CardEvaluator.Evaluate(good, cfg, st, now);
    Check(v2.Notice == null && ((MetricNode)v2.Nodes[1]).Value == "2k" && ((HeaderNode)v2.Nodes[0]).Title == "a/b", "正常渲染");
    Eq(CardEvaluator.SourceUrl(good, good.Sources[0], cfg, new Dictionary<string, System.Text.Json.JsonElement>(), out _)!.AbsoluteUri, "https://api.example.com/repos/a/b", "宿主拼出的请求地址");

    // 倒计时
    var cd = CardCountdown.Compute("2026-12-01", "2026-11-01", now)!.Value;
    Eq(cd.GetProperty("days").GetInt32(), 3, "倒计时天数");
    Check(Math.Abs(cd.GetProperty("progress").GetDouble() - 27.4167 / 30) < 0.001, "倒计时进度");
    Check(CardCountdown.Compute("2026-01-01", "", now)!.Value.GetProperty("passed").GetBoolean(), "已过去的日期");
    Check(CardCountdown.Compute("明天", "", now) == null, "坏日期");

    // L3 输出
    var l3 = PluginManifest.Parse("""{ "id": "legacy", "entry": "run.ps1", "config": { "city": "x" } }""");
    Check(l3.IsValid && l3.Tier == PluginTier.Process, "L3 解析");
    var out1 = ProcessOutput.Parse(l3, """{ "components": [ { "text": "CPU 30%", "icon": "\uE950" }, { "text": "下载", "progress": 0.42 } ] }""", now);
    Check(out1 != null && out1.Nodes.OfType<ProgressNode>().Single().Fraction == 0.42 && out1.Nodes.OfType<BadgeRowNode>().Single().Badges[0].Text == "CPU 30%", "旧版组件输出转卡片");
    var out2 = ProcessOutput.Parse(l3, """{ "card": [ { "type": "metric", "value": "42", "unit": "℃" }, { "type": "action_row", "actions": [ { "label": "坏", "kind": "exec" } ] } ] }""", now);
    Check(out2 != null && out2.Nodes.Count == 1 && out2.Nodes[0] is MetricNode, "L3 输出 card 同样过原语校验（exec 按钮丢掉）");
    Check(ProcessOutput.Parse(l3, new string('x', 70 * 1024), now) == null, "L3 输出超限丢弃");
    Check(!PluginManifest.Parse("""{ "id": "x", "entry": "../../Windows/System32/cmd.exe" }""").IsValid, "L3 入口不能越出插件目录");
    Check(!PluginManifest.Parse("""{ "id": "x", "entry": "run.js" }""").IsValid, "L3 入口类型白名单");

    // 权限升级检测
    var g1 = PluginGrant.Of(good);
    Eq(PluginPermissions.Escalations(null, g1).Count, 1, "首次启用要确认");
    Eq(PluginPermissions.Escalations(g1, g1).Count, 0, "没变化不用再确认");
    var more = PluginManifest.Parse(good.Domains.Count == 1 ? """{ "id": "demo-card", "permissions": { "domains": ["api.example.com", "cdn.example.com"] }, "card": [ { "type": "header", "title": "t" } ] }""" : "");
    Check(PluginPermissions.Escalations(g1, PluginGrant.Of(more)).Any(x => x.Contains("cdn.example.com")), "域名增多要重新确认");
    var fewer = PluginManifest.Parse("""{ "id": "demo-card", "permissions": { "domains": [] }, "card": [ { "type": "header", "title": "t" } ] }""");
    Eq(PluginPermissions.Escalations(g1, PluginGrant.Of(fewer)).Count, 0, "域名变少不用确认");
    var up = PluginManifest.Parse("""{ "id": "demo-card", "tier": "process", "entry": "run.ps1" }""");
    Check(PluginPermissions.Escalations(g1, PluginGrant.Of(up, "AA")).Any(x => x.Contains("L3")), "L2 升级为 L3 要重新确认");
    var p1 = PluginGrant.Of(up, "AA");
    Check(PluginPermissions.Escalations(p1, PluginGrant.Of(up, "BB")).Any(x => x.Contains("SHA-256")), "L3 入口内容变了要重新确认");
    Check(PluginPermissions.Escalations(p1, PluginGrant.Of(PluginManifest.Parse("""{ "id": "demo-card", "tier": "process", "entry": "other.exe" }"""), "AA")).Any(x => x.Contains("入口文件")), "L3 换入口要重新确认");
    Check(PluginPermissions.Escalations(g1, PluginGrant.Of(local)).Any(x => x.Contains("本机数据源")), "新增本机数据源要重新确认");

    // 6 个官方示例插件：全量 Schema 校验 + 离线样例数据求值
    var root = AppContext.BaseDirectory;
    while (root != null && !Directory.Exists(Path.Combine(root, "plugins", "official"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
    Check(root != null, "找到 plugins/official");
    var officials = root == null ? [] : Directory.GetDirectories(Path.Combine(root, "plugins", "official")).OrderBy(x => x).ToArray();
    Eq(officials.Length, 6, "官方示例插件 6 个");
    var mocks = new Dictionary<string, Dictionary<string, string>>
    {
        ["github-repo-radar"] = new() { ["repo"] = """{ "stargazers_count": 1280, "forks_count": 96, "open_issues_count": 7, "subscribers_count": 21, "language": "C#", "description": "Windows 灵动岛", "pushed_at": "2026-11-27T12:00:00Z" }""" },
        ["exchange-rate-watch"] = new() { ["rx"] = """{ "result": "success", "time_last_update_unix": 1764288000, "rates": { "CNY": 1, "USD": 0.1406, "EUR": 0.1215, "JPY": 21.6, "HKD": 1.094 } }""" },
        ["hn-top-pulse"] = new() { ["top"] = "[101, 102, 103]", ["s1"] = """{ "id": 101, "title": "Show HN: 示例", "score": 512, "by": "demo", "descendants": 128, "time": 1764280000 }""", ["s2"] = """{ "id": 102, "title": "Second story", "score": 300 }""" },
        ["npm-package-stats"] = new() { ["range"] = "{ \"downloads\": [" + string.Join(",", Enumerable.Range(0, 30).Select(i => $"{{ \"day\": \"2026-10-{i + 1:00}\", \"downloads\": {1000 + i * 37 + (i % 7 == 5 ? -300 : 0)} }}")) + "] }" },
    };
    foreach (var dir in officials)
    {
        var id = Path.GetFileName(dir);
        var m = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")));
        Check(m.IsValid, $"官方插件 {id} 校验通过：" + string.Join("；", m.Errors));
        Eq(m.Id, id, $"官方插件 {id} 目录名 = id");
        Eq(m.Tier, PluginTier.Declarative, $"官方插件 {id} 是 L2");
        Check(m.Domains.Count <= 3 && m.LocalSources.Count == 0 && m.Entry.Length == 0, $"官方插件 {id} 权限最小");
        Check(m.Version.Length > 0 && m.Description.Length > 0 && m.Author.Length > 0, $"官方插件 {id} 元数据完整");
        Check(m.Sources.Where(s => s.Type == "https").All(s => s.IntervalSeconds >= 60), $"官方插件 {id} 刷新间隔 ≥ 60s");
        var states = new Dictionary<string, SourceState>();
        if (mocks.TryGetValue(id, out var mk)) foreach (var (k, v) in mk) states[k] = new SourceState(J(v), null, now);
        var view = CardEvaluator.Evaluate(m, m.EffectiveConfig(null), states, now);
        Check(view.Notice == null && view.Nodes.Count == m.Card.Count, $"官方插件 {id} 离线样例数据渲染完整");
        var texts = view.Nodes.SelectMany(n => n switch { HeaderNode h => new[] { h.Title, h.Subtitle }, MetricNode x => new[] { x.Value }, BadgeRowNode b => b.Badges.Select(y => y.Text).ToArray(), _ => [] });
        Check(!texts.Any(t => t.Contains("{{") || t == "—"), $"官方插件 {id} 没有漏填的字段");
        foreach (var s in m.Sources.Where(s => s.Type == "https"))
        {
            var data = CardEvaluator.LocalData(m, m.EffectiveConfig(null), now);
            foreach (var (k, v) in states) if (v.Data is { } d) data[k] = d;
            Check(CardEvaluator.SourceUrl(m, s, m.EffectiveConfig(null), data, out var why) != null, $"官方插件 {id} 数据源 {s.Id} 地址合法：{why}");
        }
        foreach (var a in view.Nodes.OfType<ActionRowNode>().SelectMany(r => r.Actions)) Check(a.Kind == "refresh" || a.Url?.StartsWith("https://") == true, $"官方插件 {id} 按钮 {a.Label} 只开 https");
    }
    var npm = PluginManifest.Parse(File.ReadAllText(Path.Combine(officials.First(o => o.EndsWith("npm-package-stats")), "plugin.json")));
    var scoped = CardEvaluator.SourceUrl(npm, npm.Sources[0], npm.EffectiveConfig(new Dictionary<string, string> { ["package"] = "@types/node" }), new Dictionary<string, System.Text.Json.JsonElement>(), out _);
    Eq(scoped?.AbsoluteUri, "https://api.npmjs.org/downloads/range/last-month/%40types/node", "npm 作用域包名按段编码");
}

// ───────────── 5.0 第三波：Ed25519（RFC 8032 7.1 官方测试向量） ─────────────
{
    static byte[] H(string s) => Convert.FromHexString(s);
    var vectors = new (string Sk, string Pk, string Msg, string Sig, string Name)[]
    {
        ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60", "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
         "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b", "TEST 1（空消息）"),
        ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb", "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
         "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00", "TEST 2（1 字节）"),
        ("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7", "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025", "af82",
         "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a", "TEST 3（2 字节）"),
        ("833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42", "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
         "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
         "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704", "TEST SHA(abc)"),
    };
    foreach (var (sk, pk, msg, sig, name) in vectors)
    {
        Eq(Convert.ToHexString(Ed25519.PublicKeyFromSeed(H(sk))).ToLowerInvariant(), pk, $"Ed25519 {name} 公钥");
        Eq(Convert.ToHexString(Ed25519.Sign(H(sk), H(msg))).ToLowerInvariant(), sig, $"Ed25519 {name} 签名逐字节一致");
        Check(Ed25519.Verify(H(pk), H(msg), H(sig)), $"Ed25519 {name} 验签通过");
        var m2 = H(msg + "00");
        Check(!Ed25519.Verify(H(pk), m2, H(sig)), $"Ed25519 {name} 消息多一个字节就不过");
        var s2 = H(sig); s2[5] ^= 0x01;
        Check(!Ed25519.Verify(H(pk), H(msg), s2), $"Ed25519 {name} 签名 R 改一位就不过");
        var s3 = H(sig); s3[40] ^= 0x80;
        Check(!Ed25519.Verify(H(pk), H(msg), s3), $"Ed25519 {name} 签名 S 改一位就不过");
    }
    // 非规范 S（S + L）：数学上也能验过，但必须拒绝（防签名延展）
    var (_, pk1, _, sig1, _) = vectors[0];
    var sBig = new System.Numerics.BigInteger(H(sig1)[32..], isUnsigned: true) + (System.Numerics.BigInteger.Pow(2, 252) + System.Numerics.BigInteger.Parse("27742317777372353535851937790883648493"));
    var sRaw = sBig.ToByteArray(isUnsigned: true); var malleable = H(sig1); Array.Clear(malleable, 32, 32); Array.Copy(sRaw, 0, malleable, 32, Math.Min(32, sRaw.Length));
    Check(!Ed25519.Verify(H(pk1), [], malleable), "Ed25519 拒绝非规范签名（S ≥ L）");
    Check(!Ed25519.Verify(H(pk1)[..31], [], H(sig1)), "Ed25519 公钥长度不对返回 false");
    Check(!Ed25519.Verify(H(pk1), [], H(sig1)[..63]), "Ed25519 签名长度不对返回 false");
    Check(!Ed25519.Verify(Enumerable.Repeat((byte)0xFF, 32).ToArray(), [], H(sig1)), "Ed25519 解不出的公钥点返回 false");
    Check(!Ed25519.Verify(H(vectors[1].Pk), [], H(sig1)), "Ed25519 换一把公钥就不过");
    var seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    var big = Encoding.UTF8.GetBytes(new string('x', 5000));
    Check(Ed25519.Verify(Ed25519.PublicKeyFromSeed(seed), big, Ed25519.Sign(seed, big)), "Ed25519 随机密钥签 5 KB 消息再验过");
}

// ───────────── 5.0 第三波：插件市场 · 签名与哈希规则 ─────────────
{
    var seed = Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
    var keys = new[] { Ed25519.PublicKeyFromSeed(seed) };
    var lf = Encoding.UTF8.GetBytes("{\n  \"a\": 1\n}\n");
    var crlf = Encoding.UTF8.GetBytes("\uFEFF{\r\n  \"a\": 1\r\n}\r\n");
    Eq(MarketTrust.PackageSha256(lf), MarketTrust.PackageSha256(crlf), "JSON 哈希：CRLF / BOM 和 LF 是同一个值");
    var sig = MarketTrust.Sign(seed, crlf);
    Check(MarketTrust.Verify(lf, sig, keys), "在 Windows（CRLF）上签的，GitHub raw（LF）返回的也能验过");
    Check(MarketTrust.Verify(lf, "ed25519:" + sig + "\n", keys), "签名文件可以带 ed25519: 前缀和换行");
    Check(!MarketTrust.Verify(Encoding.UTF8.GetBytes("{\n  \"a\": 2\n}\n"), sig, keys), "内容改一个字就验不过");
    Check(!MarketTrust.Verify(lf, sig, MarketTrust.OfficialKeys), "别人的私钥签的，官方公钥验不过");
    Check(!MarketTrust.Verify(lf, null, keys) && !MarketTrust.Verify(lf, "not base64!", keys) && !MarketTrust.Verify(lf, Convert.ToBase64String(new byte[10]), keys), "没有签名 / 坏签名都算不过");
    var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x0D, 0x0A, 0x0D };
    Eq(MarketTrust.PackageSha256(zip), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)).ToLowerInvariant(), "zip 包按原始字节算（不去 \\r）");
    Eq(MarketTrust.OfficialKeys.Count, 1, "内置 1 把官方公钥");
    Check(MarketTrust.OfficialKeys.All(k => k.Length == 32), "官方公钥 32 字节");
}

// ───────────── 5.0 第三波：registry.json / revoked.json 解析 ─────────────
{
    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "plugins", "registry-template", "registry.json"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
    Check(root != null, "找到 plugins/registry-template");
    if (root != null)
    {
        var tpl = Path.Combine(root, "plugins", "registry-template");
        var regBytes = File.ReadAllBytes(Path.Combine(tpl, "registry.json"));
        var revBytes = File.ReadAllBytes(Path.Combine(tpl, "revoked.json"));
        Check(MarketTrust.Verify(regBytes, File.ReadAllText(Path.Combine(tpl, "registry.json.sig"))), "模板 registry.json 的签名用内置官方公钥验过");
        Check(MarketTrust.Verify(revBytes, File.ReadAllText(Path.Combine(tpl, "revoked.json.sig"))), "模板 revoked.json 的签名用内置官方公钥验过");
        var tampered = (byte[])regBytes.Clone(); tampered[Array.IndexOf(tampered, (byte)'5')] = (byte)'6';
        Check(!MarketTrust.Verify(tampered, File.ReadAllText(Path.Combine(tpl, "registry.json.sig"))), "registry.json 被改了一个字节就验不过");

        var reg = MarketRegistry.Parse(Encoding.UTF8.GetString(regBytes));
        Check(reg.IsValid && reg.Warnings.Count == 0, "模板 registry.json 合格：" + reg.Error + string.Join("；", reg.Warnings));
        Eq(reg.Items.Count, 9, "内置目录 9 项");
        Eq(reg.Items.Count(i => i.Kind == MarketKind.Plugin && i.Tier == PluginTier.Declarative), 6, "6 个官方声明式插件");
        Eq(reg.Items.Count(i => i.Kind == MarketKind.Theme), 3, "3 套官方主题包");
        Check(reg.Items.All(i => MarketCategories.All.Contains(i.Category)), "分类都在 效率 / 开发 / 信息 / 主题包 里");
        Check(MarketCategories.All.All(c => reg.Items.Any(i => i.Category == c)), "四个分类都有内容");
        Check(reg.Items.All(i => i.DownloadUrl.StartsWith(MarketSources.OfficialRegistry[..^"registry.json".Length], StringComparison.Ordinal)), "下载地址都在官方索引仓库下");
        foreach (var i in reg.Items)
        {
            var src = i.Kind == MarketKind.Theme ? Path.Combine(root, "themes", i.Id + ".json") : Path.Combine(root, "plugins", "official", i.Id, "plugin.json");
            var copy = i.Kind == MarketKind.Theme ? Path.Combine(tpl, "themes", i.Id + ".json") : Path.Combine(tpl, "plugins", i.Id, "plugin.json");
            Check(File.Exists(src) && File.Exists(copy), $"{i.Id} 源文件和模板副本都在");
            if (!File.Exists(src) || !File.Exists(copy)) continue;
            Eq(MarketTrust.PackageSha256(File.ReadAllBytes(src)), i.Sha256, $"{i.Id} 的 sha256 和程序内置的文件一致");
            Eq(MarketTrust.PackageSha256(File.ReadAllBytes(copy)), i.Sha256, $"{i.Id} 的 sha256 和模板仓库里的副本一致");
            Check(MarketGate.CheckPackage(i, File.ReadAllBytes(copy)) == null, $"{i.Id} 下载校验通过");
            if (i.Kind == MarketKind.Plugin)
            {
                var m = PluginManifest.Parse(File.ReadAllText(src));
                Check(MarketGate.ManifestMismatch(i, m).Count == 0, $"{i.Id} 包内容和索引声明一致：" + string.Join("；", MarketGate.ManifestMismatch(i, m)));
            }
        }

        var rev = RevocationList.Parse(Encoding.UTF8.GetString(revBytes));
        Check(rev.IsValid && rev.Items.Count == 2, "模板 revoked.json 合格（2 个示例）");
        Check(rev.Match("example-revoked-card", "3.1.0", null) != null, "示例：按 id 撤销所有版本");
        Check(rev.Match("example-old-card", "0.9.0", null) != null && rev.Match("example-old-card", "1.0.0", null) == null, "示例：只撤销列出的版本");
        Check(reg.Items.All(i => rev.Match(i.Id, i.Version, i.Sha256) == null), "官方 9 项都不在撤销名单里");

        // 社区仓库模板的其他文件
        foreach (var f in new[] { "README.md", "AI_PLUGIN_PROMPT.md", ".github/workflows/validate-plugin.yml", "tools/qisland_registry.py", ".gitattributes" })
            Check(File.Exists(Path.Combine(tpl, f)), "模板仓库有 " + f);
        var wf = File.ReadAllText(Path.Combine(tpl, ".github", "workflows", "validate-plugin.yml"));
        Check(wf.Contains("qisland_registry.py validate") && wf.Contains("pull_request"), "PR 工作流会跑 validate");
        var py = File.ReadAllText(Path.Combine(tpl, "tools", "qisland_registry.py"));
        Check(MarketTrust.OfficialKeys.All(k => py.Contains(Convert.ToHexString(k).ToLowerInvariant())), "Python 工具里的官方公钥和客户端一致");
    }

    // 反例：整份作废 / 单项跳过
    Check(!MarketRegistry.Parse("""{ "schema_version": 2, "updated_at": "2026-11-28T00:00:00Z", "items": [] }""").IsValid, "schema_version 不对整份作废");
    Check(!MarketRegistry.Parse("""{ "schema_version": 1, "items": [] }""").IsValid, "缺 updated_at 整份作废");
    Check(!MarketRegistry.Parse("[1,2]").IsValid && !MarketRegistry.Parse("{oops").IsValid && !MarketRegistry.Parse("").IsValid, "坏 JSON / 非对象 / 空 都作废");
    string Item(string patch) => "{ \"id\": \"x-card\", \"name\": \"X\", \"version\": \"1.0.0\", \"tier\": \"declarative\", \"category\": \"info\", \"download_url\": \"https://example.com/x/plugin.json\", \"sha256\": \"" + new string('a', 64) + "\", \"permissions\": { \"domains\": [\"api.example.com\"] }" + patch + " }";
    MarketRegistry R(params string[] items) => MarketRegistry.Parse("{ \"schema_version\": 1, \"updated_at\": \"2026-11-28T00:00:00Z\", \"items\": [" + string.Join(",", items) + "] }");
    Eq(R(Item("")).Items.Count, 1, "合格的一项");
    foreach (var (patch, why) in new[]
    {
        (", \"download_url\": \"http://example.com/x.json\"", "http 下载地址"),
        (", \"download_url\": \"https://127.0.0.1/x.json\"", "IP 字面量下载地址"),
        (", \"download_url\": \"https://localhost/x.json\"", "localhost 下载地址"),
        (", \"download_url\": \"https://example.com:8443/x.json\"", "非默认端口"),
        (", \"download_url\": \"https://u:p@example.com/x.json\"", "带账号密码"),
        (", \"sha256\": \"abc\"", "sha256 太短"),
        (", \"tier\": \"native\"", "不认识的 tier"),
        (", \"category\": \"games\"", "不认识的分类"),
        (", \"category\": \"theme\", \"tier\": \"process\"", "主题包不能是 L3"),
        (", \"version\": \"latest\"", "版本号不合法"),
        (", \"min_app_version\": \"five\"", "min_app_version 不合法"),
        (", \"permissions\": { \"domains\": [\"a.com\",\"b.com\",\"c.com\",\"d.com\"] }", "L2 超过 3 个域名"),
        (", \"permissions\": { \"domains\": [\"192.168.1.1\"] }", "权限里写内网 IP"),
        (", \"id\": \"../evil\"", "id 带路径"),
    })
    {
        var r = R(Item(patch), Item(", \"id\": \"ok-card\""));
        Check(r.IsValid && r.Items.Count == 1 && r.Items[0].Id == "ok-card" && r.Warnings.Count == 1, "坏项跳过、好项保留：" + why);
    }
    var dup = R(Item(""), Item(", \"version\": \"2.0.0\""));
    Check(dup.Items.Count == 1 && dup.Items[0].Version == "1.0.0" && dup.Warnings.Count == 1, "重复 id 只留第一项");
    var l3 = R(Item(", \"tier\": \"process\", \"permissions\": {}")).Items.Single();
    Check(l3.Permissions.RunsProgram && !l3.Permissions.Offline, "L3 项标成「会运行本机程序」");
    Check(R(Item(", \"permissions\": { \"domains\": [] }")).Items.Single().Permissions.Offline, "没有域名的 L2 标成「不联网」");

    // 撤销名单
    var rl = RevocationList.Parse("""
    { "schema_version": 1, "updated_at": "2026-11-28T00:00:00Z", "items": [
      { "id": "bad-card", "reason": "发布者账号被盗" },
      { "id": "old-card", "versions": ["1.0.0", "1.0.1"], "reason": "请求太频繁" },
      { "sha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", "reason": "这个包被篡改过" },
      { "reason": "两者都没写，忽略" },
      { "id": "../x", "reason": "id 不合法，忽略" } ] }
    """);
    Check(rl.IsValid && rl.Items.Count == 3, "撤销名单解析（不合格的条目忽略）");
    Eq(rl.Match("BAD-CARD", "9.9.9", null), "发布者账号被盗", "按 id 命中（不分大小写、所有版本）");
    Check(rl.Match("old-card", "1.0.1", null) != null && rl.Match("old-card", "v1.0.0", null) != null && rl.Match("old-card", "1.1.0", null) == null, "只命中列出的版本");
    Check(rl.Match("any-id", "1.0.0", new string('b', 64)) != null, "按 sha256 精确命中（不管 id）");
    Check(rl.Match("any-id", "1.0.0", new string('c', 64)) == null && rl.Match("good-card", "1.0.0", null) == null, "不在名单里不命中");
    Check(!RevocationList.Parse("""{ "schema_version": 1, "items": [] }""").IsValid, "撤销名单缺 updated_at 作废");
}

// ───────────── 5.0 第三波：min_app_version / 安装计划 / 下载校验 / 来源 ─────────────
{
    MarketItem It(string ver = "1.2.0", string min = "5.0.0", PluginTier tier = PluginTier.Declarative, string cat = "dev", string sha = "", params string[] domains) =>
        new("demo-card", "演示", ver, tier, cat, "社区", "", "◆", "https://example.com/demo/plugin.json", sha.Length > 0 ? sha : new string('a', 64),
            new MarketPermissions(domains, [], tier == PluginTier.Process), min, "");
    Check(MarketGate.CheckAppVersion(It(), "4.3.0") is { } why && why.Contains("5.0.0"), "4.3.0 装不了 min 5.0.0 的插件，并说明原因");
    Check(MarketGate.CheckAppVersion(It(), "5.0.0") == null && MarketGate.CheckAppVersion(It(), "5.2.1") == null, "5.0.0 / 5.2.1 满足 min 5.0.0");
    Check(MarketGate.CheckAppVersion(It(), "5.0.0-preview.2") == null, "预览版按正式版号算（5.0.0-preview.2 满足 5.0.0）");
    Check(MarketGate.CheckAppVersion(It(min: "5.1.0"), "5.0.9") != null, "5.0.9 不满足 5.1.0");
    Check(MarketGate.CheckAppVersion(It(min: ""), "1.0.0") == null, "没写 min_app_version 不设门槛");
    Eq(MarketGate.Plan(It(), null, "5.0.0", null), MarketState.Install, "没装 → 安装");
    Eq(MarketGate.Plan(It(), "1.1.0", "5.0.0", null), MarketState.Update, "装了旧版 → 更新");
    Eq(MarketGate.Plan(It(), "1.2.0", "5.0.0", null), MarketState.Installed, "同版本 → 已安装");
    Eq(MarketGate.Plan(It(), "2.0.0", "5.0.0", null), MarketState.Installed, "本地更新 → 不降级");
    Eq(MarketGate.Plan(It(), null, "4.3.0", null), MarketState.Incompatible, "版本不够 → 需要新版");
    Eq(MarketGate.Plan(It(), "1.0.0", "4.3.0", null), MarketState.Installed, "版本不够但已装旧版 → 不提示更新");
    var rv = RevocationList.Parse("""{ "schema_version": 1, "updated_at": "2026-11-28T00:00:00Z", "items": [ { "id": "demo-card", "versions": ["1.2.0"], "reason": "x" } ] }""");
    Eq(MarketGate.Plan(It(), "1.1.0", "5.0.0", rv), MarketState.Revoked, "撤销的版本 → 不让装");

    var body = Encoding.UTF8.GetBytes("{ \"id\": \"demo-card\" }");
    var good = It(sha: MarketTrust.PackageSha256(body));
    Check(MarketGate.CheckPackage(good, body) == null, "SHA-256 一致 → 通过");
    Check(MarketGate.CheckPackage(It(), body)?.Contains("SHA-256") == true, "SHA-256 对不上 → 丢弃");
    Check(MarketGate.CheckPackage(good, []) != null, "空文件拒绝");
    var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
    Check(MarketGate.CheckPackage(It(cat: "theme", sha: MarketTrust.PackageSha256(zip)), zip) != null, "主题包不能是 zip");
    Check(MarketGate.CheckPackage(It(tier: PluginTier.Process, sha: MarketTrust.PackageSha256(body)), body) != null, "L3 必须是 zip");
    Check(MarketGate.CheckPackage(It(tier: PluginTier.Process, sha: MarketTrust.PackageSha256(zip)), zip) == null, "L3 zip 且哈希一致 → 通过");

    var l2 = PluginManifest.Parse("""{ "id": "demo-card", "version": "1.2.0", "tier": "declarative", "permissions": { "domains": ["api.example.com"] }, "card": [ { "type": "header", "title": "t" } ] }""");
    Check(MarketGate.ManifestMismatch(It("1.2.0", "5.0.0", PluginTier.Declarative, "dev", "", "api.example.com"), l2).Count == 0, "包和索引一致");
    Check(MarketGate.ManifestMismatch(It("1.2.0", "5.0.0", PluginTier.Declarative, "dev", ""), l2).Any(x => x.Contains("api.example.com")), "包里多要了域名 → 拒绝");
    Check(MarketGate.ManifestMismatch(It("1.3.0", "5.0.0", PluginTier.Declarative, "dev", "", "api.example.com"), l2).Any(x => x.Contains("版本")), "版本不一致 → 拒绝");
    var sneaky = PluginManifest.Parse("""{ "id": "demo-card", "version": "1.2.0", "entry": "run.ps1" }""");
    Check(MarketGate.ManifestMismatch(It(), sneaky).Any(x => x.Contains("L3")), "索引写 L2、包里是 L3 → 拒绝（不能偷偷升级成会运行程序的插件）");
    var other = PluginManifest.Parse("""{ "id": "other-card", "version": "1.2.0", "card": [ { "type": "header", "title": "t" } ] }""");
    Check(MarketGate.ManifestMismatch(It(), other).Any(x => x.Contains("id")), "包里的 id 不一致 → 拒绝");

    var t0 = DateTimeOffset.Parse("2026-11-28T00:00:00Z");
    Check(MarketGate.IsRollback(t0.AddDays(-1), t0), "比上次验过的旧 → 判为回放");
    Check(!MarketGate.IsRollback(t0, t0) && !MarketGate.IsRollback(t0.AddHours(1), t0) && !MarketGate.IsRollback(t0, null), "同一份 / 更新的 / 第一次 → 接受");

    foreach (var (u, ok, name) in new[]
    {
        ("https://raw.githubusercontent.com/a/b/main/registry.json", true, "GitHub raw"),
        ("http://example.com/registry.json", false, "http"),
        ("https://10.0.0.2/registry.json", false, "内网 IP"),
        ("https://nas.local/registry.json", false, ".local"),
        ("https://example.com:444/registry.json", false, "非 443 端口"),
        ("https://example.com/registry.txt", false, "不是 .json"),
        ("file:///C:/registry.json", false, "file://"),
    }) Eq(MarketSources.IsValidSourceUrl(u, out _), ok, "自定义索引源地址：" + name);
    Eq(MarketSources.Mirror("https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/plugins/x/plugin.json"), "https://cdn.jsdelivr.net/gh/Qiuizi/qisland-plugins@main/plugins/x/plugin.json", "GitHub raw → jsDelivr 镜像");
    Eq(MarketSources.Mirror(MarketSources.OfficialRegistry), MarketSources.OfficialRegistryMirror, "官方索引的镜像地址");
    Check(MarketSources.Mirror("https://example.com/a/b/c/d.json") == null && MarketSources.Mirror("https://raw.githubusercontent.com/a/b") == null, "不是 GitHub raw 不给镜像");
    Eq(MarketSources.Sibling(MarketSources.OfficialRegistry, "revoked.json"), "https://raw.githubusercontent.com/Qiuizi/qisland-plugins/main/revoked.json", "同目录的 revoked.json");
    Eq(MarketSources.Sibling(MarketSources.OfficialRegistryMirror, "registry.json.sig"), "https://cdn.jsdelivr.net/gh/Qiuizi/qisland-plugins@main/registry.json.sig", "镜像上的 .sig");

    // 合并：第三方源不能冒用官方 id（哪怕版本更高）；同为验签的取高版本；筛选与搜索
    var off = new MarketSource("官方索引", MarketSources.OfficialRegistry, true);
    var third = new MarketSource("某源", "https://example.com/registry.json", false);
    var merged = MarketCatalog.Merge([
        new MarketListing(It("1.0.0"), off, true, true),
        new MarketListing(It("9.0.0"), third, false, false),
        new MarketListing(It("1.1.0"), off, true, false),
        new MarketListing(It("1.0.0") with { Id = "theme-a", Name = "主题 A", Category = "theme" }, off, true, true),
        new MarketListing(It("0.1.0") with { Id = "pomodoro", Name = "番茄钟", Category = "productivity", Description = "专注计时" }, third, false, false),
    ]);
    Eq(merged.Count, 3, "合并后按 id 去重");
    var demo = merged.Single(x => x.Item.Id == "demo-card");
    Check(demo.Verified && demo.Item.Version == "1.1.0", "第三方源冒用官方 id 无效，验过签的里取最高版本");
    Eq(merged[0].Item.Category, "productivity", "按 效率 → 开发 → 信息 → 主题包 排");
    Eq(MarketCatalog.Filter(merged, "theme", null).Single().Item.Id, "theme-a", "分类筛选：主题包");
    Eq(MarketCatalog.Filter(merged, "all", "专注").Single().Item.Id, "pomodoro", "搜索描述");
    Eq(MarketCatalog.Filter(merged, null, "DEMO").Count, 1, "搜索 id 不分大小写");
    Eq(MarketCatalog.Filter(merged, "all", "开发").Single().Item.Id, "demo-card", "搜索分类名");
    Eq(MarketCatalog.Filter(merged, "all", "不存在的词").Count, 0, "搜不到返回空");

    Check(SemVer.IsValid("5.0.0") && SemVer.IsValid("v1.2") && SemVer.IsValid("5.0.0-preview.2") && !SemVer.IsValid("latest") && !SemVer.IsValid("1..2") && !SemVer.IsValid(null), "SemVer.IsValid");
}

// ───────────── 5.0 第三波：Setup.exe 安装脚本与发布脚本 ─────────────
{
    Eq(ReleaseAsset.Rank("Qisland-5.0.0-Setup.exe", true), -1, "自带运行时的更新器不会拿 Setup.exe");
    Eq(ReleaseAsset.Rank("Qisland-5.0.0-Setup.exe", false), -1, "依赖运行时的更新器不会拿 Setup.exe");
    Eq(ReleaseAsset.Rank("SHA256SUMS-5.0.0.txt", true), -1, "校验文件不是更新包");
    Eq(ReleaseAsset.Rank("Qisland-5.0.0-full-x64.zip", true), 2, "Setup.exe 装的（有 hostfxr.dll）只用 full 包更新");
    Eq(SetupInfo.SetupFileName("v5.0.0"), "Qisland-5.0.0-Setup.exe", "Setup.exe 文件名");
    Check(SetupInfo.SameDir(@"C:\Users\x\AppData\Local\Programs\Qisland\", @"c:/users/x/appdata/local/programs/qisland") && !SetupInfo.SameDir(@"C:\A\Qisland", @"C:\B\Qisland") && !SetupInfo.SameDir(null, "x"), "安装目录比较（大小写 / 斜杠 / 结尾）");

    var root = AppContext.BaseDirectory;
    while (root != null && !File.Exists(Path.Combine(root, "installer", "Qisland.iss"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
    Check(root != null, "找到 installer/Qisland.iss");
    if (root != null)
    {
        var issPath = Path.Combine(root, "installer", "Qisland.iss");
        var raw = File.ReadAllBytes(issPath);
        Check(raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, ".iss 是带 BOM 的 UTF-8（Inno Setup 才认得中文）");
        var iss = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n");
        bool Has(string s) => iss.Contains(s, StringComparison.Ordinal);
        Check(Has($"#define AppIdGuid \"{SetupInfo.AppId.Trim('{', '}')}\"") && Has("AppId={{{#AppIdGuid}}"), "AppId 和 SetupInfo.AppId 一致");
        Check(Has("DefaultDirName=" + SetupInfo.DefaultDirName + "\n"), "默认装到 {localappdata}\\Programs\\Qisland");
        Check(Has("PrivilegesRequired=lowest\n"), "免管理员权限");
        Check(Has("UsePreviousAppDir=yes") && Has("UsePreviousTasks=yes") && Has("DisableDirPage=auto"), "覆盖更新沿用上次的目录和选项");
        Check(Has("CloseApplications=yes") && Has("CloseApplicationsFilter={#AppExe}") && Has("RestartApplications=no"), "安装前关闭正在运行的 Qisland");
        Check(Has("function PrepareToInstall(") && Has("taskkill.exe'), '/IM {#AppExe}'"), "先温和请它退出，再强制结束");
        Check(Has("SetupIconFile=..\\src\\Qisland.App\\Assets\\qisland.ico") && File.Exists(Path.Combine(root, "src", "Qisland.App", "Assets", "qisland.ico")), "安装程序图标用 qisland.ico");
        Check(Has("UninstallDisplayIcon={app}\\{#AppExe}"), "卸载图标用程序图标");
        Check(Has("LicenseFile=..\\LICENSE") && File.Exists(Path.Combine(root, "LICENSE")) && File.ReadAllText(Path.Combine(root, "LICENSE")).Contains("MIT License"), "MIT 许可协议页");
        Check(Has("OutputBaseFilename=Qisland-{#AppVersion}-Setup") && string.Format(SetupInfo.SetupFileFormat, "{#AppVersion}") == "Qisland-{#AppVersion}-Setup.exe", "产物名 Qisland-<版本>-Setup.exe");
        Check(Has("Name: \"chinesesimplified\"; MessagesFile: \"{#ChineseIsl}\"") && Has("Name: \"english\"; MessagesFile: \"compiler:Default.isl\""), "中英文安装界面");
        var zhIsl = Path.Combine(root, "installer", "Languages", "ChineseSimplified.isl");
        Check(Has("#elif FileExists(AddBackslash(SourcePath) + \"Languages\\ChineseSimplified.isl\")") && File.Exists(zhIsl) && File.ReadAllText(zhIsl).Contains("LanguageID=$0804"), "仓库附带简体中文 .isl（官方 Inno Setup 安装包不带），编译器没有时自动改用它");
        Check(Has("chinesesimplified.TaskAutostart=") && Has("english.TaskAutostart=") && Has("chinesesimplified.AskDeleteData=") && Has("english.AskDeleteData="), "自定义文案中英文都有");
        Check(Has("Name: \"desktopicon\";") && Has("Flags: unchecked") && Has("Name: \"autostart\";"), "勾选：桌面快捷方式（默认不勾）、开机自启");
        Check(Has("ValueName: \"Qisland\"; ValueData: \"\"\"{app}\\{#AppExe}\"\" --autostart\"; Tasks: autostart; Flags: uninsdeletevalue"), "开机自启走程序自己的 Run 键约定（--autostart），卸载时删掉");
        Check(Has("autostart.off"), "不勾自启时写程序认的关闭标记，第一次启动不会把自己加回去");
        Check(Has("Description: \"{cm:LaunchNow}\"; Flags: nowait postinstall skipifsilent"), "装完可勾选「立即启动」");
        Check(Has("{userappdata}\\Qisland") && Has("MB_YESNO or MB_DEFBUTTON2") && Has("not UninstallSilent()"), "卸载时询问是否删数据，默认「否」，静默卸载从不删");
        Check(!Has("{localappdata}\\Qisland'") && !Has("DelTree(ExpandConstant('{localappdata}"), "不会去删 %LOCALAPPDATA% 下的其他东西");
        Check(Has("CompareText(ExtractFileName(RemoveBackslashUnlessRoot(ExpandConstant('{app}'))), 'Qisland') = 0"), "清理程序目录前确认目录名是 Qisland");
        Check(Has("Source: \"{#SourceDir}\\*\"") && Has("#define SourceDir \"..\\publish\\Qisland-full\""), "安装包基于自带运行时的发布目录");
        Check(Has("ArchitecturesInstallIn64BitMode=x64compatible") && Has("MinVersion=10.0.17763"), "只装 x64 的 Windows 10 1809+");
        var code = iss[(iss.IndexOf("[Code]", StringComparison.Ordinal) + 6)..];
        code = System.Text.RegularExpressions.Regex.Replace(code, @"\{[^}]*\}|'[^']*'|//[^\n]*", " ");
        Eq(System.Text.RegularExpressions.Regex.Matches(code, @"\bbegin\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count,
           System.Text.RegularExpressions.Regex.Matches(code, @"\bend\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count, "[Code] 的 begin / end 配得上");

        var rel = File.ReadAllText(Path.Combine(root, "scripts", "release.ps1"));
        var lib = File.ReadAllText(Path.Combine(root, "scripts", "release-lib.ps1"));
        Check(rel.Contains("[switch]$SkipInstaller") && rel.Contains("Find-Iscc $Iscc") && rel.Contains("installer\\Qisland.iss"), "release.ps1：检测 ISCC、支持 -SkipInstaller");
        Check(rel.Contains("$outFull") && rel.Contains("/DSourceDir=") && rel.Contains("Get-SetupFileName $Version"), "release.ps1：基于全量发布目录打 Setup.exe");
        Check(rel.Contains("Qisland-$Version-win-x64.zip") && rel.Contains("Qisland-$Version-full-x64.zip"), "release.ps1：两个 zip 照旧");
        Check(rel.Contains("SHA256SUMS-$Version.txt") && rel.Contains("Get-FileHash -Algorithm SHA256"), "release.ps1：输出并保存 SHA-256");
        Check(rel.Contains("if ($setupExe) { $assets += $setupExe }"), "release.ps1：Setup.exe 附到 Release");
        Check(lib.Contains("function Find-Iscc") && lib.Contains("Inno Setup 6\\ISCC.exe") && lib.Contains("function Get-SetupFileName") && lib.Contains("function Get-NumericVersion"), "release-lib.ps1：ISCC 探测与版本号工具");

        // 有 PowerShell 就做真正的语法解析（PC 上一定有 powershell.exe；没有就跳过这一项，不算通过）
        var ps = new[] { "pwsh", OperatingSystem.IsWindows() ? "powershell.exe" : "" }.FirstOrDefault(n => n.Length > 0 && Which(n));
        if (ps != null)
        {
            var files = string.Join(",", new[] { "release.ps1", "release-lib.ps1" }.Select(f => "'" + Path.Combine(root, "scripts", f).Replace("'", "''") + "'"));
            var cmd = $"$bad=0; foreach($f in @({files})){{ $t=$null;$e=$null; [void][System.Management.Automation.Language.Parser]::ParseFile($f,[ref]$t,[ref]$e); if($e.Count){{$bad++; $e|%{{ Write-Output \"$f $($_.Extent.StartLineNumber) $($_.Message)\" }} }} }}; exit $bad";
            var psi = new System.Diagnostics.ProcessStartInfo(ps) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", cmd }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            Check(p.ExitCode == 0, "release.ps1 / release-lib.ps1 PowerShell 语法解析通过 " + o.Trim());
        }
        else Console.WriteLine("SKIP  没有 PowerShell，跳过 release.ps1 语法解析");

        // 门面文件里不能出现本机路径（个人信息红线）
        var face = new[] { "README.md", "README_EN.md", "CHANGELOG.md" }.Select(f => Path.Combine(root, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.html", SearchOption.TopDirectoryOnly))
            .Append(Path.Combine(root, "plugins", "README.md"))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "plugins", "registry-template"), "*.*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".sig") && !f.Contains("__pycache__") && !f.EndsWith(".pyc")))
            .Append(issPath);
        var leak = new System.Text.RegularExpressions.Regex(@"[A-Za-z]:\\Users\\(?!x\\|<|%|你)|[D-Zd-z]:\\[^\s""'<>|*?]+\\|/home/\w|/workspace/|/cursor/|\\\\wsl", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (var f in face)
        {
            var hit = leak.Match(File.ReadAllText(f));
            Check(!hit.Success, $"{Path.GetRelativePath(root, f)} 没有本机路径" + (hit.Success ? "：" + hit.Value : ""));
        }
    }

    static bool Which(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => { try { return File.Exists(Path.Combine(d, exe)) || File.Exists(Path.Combine(d, exe + ".exe")); } catch { return false; } });
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
