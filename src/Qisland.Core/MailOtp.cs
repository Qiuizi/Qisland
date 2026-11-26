using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>常见邮箱的 IMAP 预设：一键填服务器。授权码都要去邮箱网页版设置里单独生成，不是登录密码。</summary>
public sealed record MailPreset(string Id, string Name, string Host, int Port, string Hint)
{
    public static readonly MailPreset[] All =
    [
        new("qq", "QQ 邮箱", "imap.qq.com", 993, "QQ 邮箱网页版 → 设置 → 账号 → 开启 IMAP/SMTP 服务，生成授权码"),
        new("163", "网易 163", "imap.163.com", 993, "163 邮箱网页版 → 设置 → POP3/SMTP/IMAP → 开启 IMAP，生成授权码"),
        new("126", "网易 126", "imap.126.com", 993, "126 邮箱网页版 → 设置 → POP3/SMTP/IMAP → 开启 IMAP，生成授权码"),
        new("gmail", "Gmail", "imap.gmail.com", 993, "Google 账号 → 安全性 → 两步验证 → 应用专用密码"),
        new("icloud", "iCloud", "imap.mail.me.com", 993, "appleid.apple.com → 登录与安全 → App 专用密码"),
        new("custom", "其他", "", 993, "填你邮箱服务商给的 IMAP 服务器；993 走 SSL，143 必须支持 STARTTLS"),
    ];

    public static MailPreset? Guess(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0) return null;
        var domain = address[(at + 1)..].Trim().ToLowerInvariant();
        return domain switch
        {
            "qq.com" or "foxmail.com" or "vip.qq.com" => All[0],
            "163.com" or "vip.163.com" => All[1],
            "126.com" => All[2],
            "gmail.com" or "googlemail.com" => All[3],
            "icloud.com" or "me.com" or "mac.com" => All[4],
            _ => null,
        };
    }
}

/// <summary>一封新邮件里能用来认验证码的部分：只在内存里，用完即丢，不落盘、不写日志。</summary>
public sealed record MailPeek(long Uid, string From, string Subject, string Text, DateTime? Date);

/// <summary>从邮件主题 + 正文片段认验证码。复用通知验证码的规则（4–8 位数字、要有验证码关键字）。</summary>
public static partial class MailOtp
{
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptTags();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();

    /// <summary>HTML 转纯文本（够认验证码就行）：去 script/style、标签换空格、解常见实体。</summary>
    public static string HtmlToText(string html)
    {
        var s = ScriptTags().Replace(html, " ");
        s = Tags().Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        return Spaces().Replace(s, " ").Trim();
    }

    public static string? Extract(string subject, string text)
    {
        // 主题里有就直接用（「您的验证码 123456」最常见）
        if (Otp.Extract(subject) is { } s) return s;
        var body = Spaces().Replace(text ?? "", " ").Trim();
        // 通知验证码规则对 >2000 字的文本直接放弃：邮件只看开头一段，验证码基本都在前面
        if (body.Length > 1800) body = body[..1800];
        return Otp.Extract(subject + " " + body);
    }

    /// <summary>发件人显示名：「"招商银行" &lt;x@y&gt;」→ 招商银行；没有显示名用邮箱域名。</summary>
    public static string SenderName(string from)
    {
        var f = Mime.DecodeHeader(from).Trim();
        var lt = f.IndexOf('<');
        var name = (lt > 0 ? f[..lt] : "").Trim().Trim('"', '\'').Trim();
        if (name.Length > 0) return name.Length > 18 ? name[..18] + "…" : name;
        var addr = lt >= 0 ? f[(lt + 1)..].TrimEnd('>') : f;
        var at = addr.LastIndexOf('@');
        return at >= 0 ? addr[(at + 1)..].Trim() : addr.Trim();
    }
}

/// <summary>最小的 MIME 解码：编码字（RFC 2047）、quoted-printable / base64、multipart 里挑 text/plain（没有就 text/html）。</summary>
public static partial class Mime
{
    static Mime() { try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { } }

    public static Encoding Charset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Encoding.UTF8;
        var n = name.Trim().Trim('"').ToLowerInvariant();
        if (n is "gb2312" or "gbk" or "x-gbk" or "cp936") n = "gb18030";   // 国内邮件常把 GBK 标成 gb2312
        try { return Encoding.GetEncoding(n); } catch { return Encoding.UTF8; }
    }

    [GeneratedRegex(@"=\?([^?]+)\?([BbQq])\?([^?]*)\?=")] private static partial Regex EncodedWord();
    [GeneratedRegex(@"(=\?[^?]+\?[BbQq]\?[^?]*\?=)\s+(?==\?)")] private static partial Regex BetweenWords();

    public static string DecodeHeader(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var s = BetweenWords().Replace(raw, "$1");   // 相邻编码字之间的空白不算内容
        return EncodedWord().Replace(s, m =>
        {
            try
            {
                var enc = Charset(m.Groups[1].Value);
                var data = m.Groups[3].Value;
                byte[] bytes = m.Groups[2].Value is "B" or "b"
                    ? FromBase64Loose(data)
                    : QuotedPrintable(data.Replace('_', ' '), header: true);
                return enc.GetString(bytes);
            }
            catch { return m.Value; }
        });
    }

    public static byte[] FromBase64Loose(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/') sb.Append(c);
        var len = sb.Length - sb.Length % 4;   // 截断在半截的尾巴丢掉
        if (sb.Length % 4 is 2 or 3) { sb.Append('=', 4 - sb.Length % 4); len = sb.Length; }
        try { return Convert.FromBase64String(sb.ToString(0, len)); } catch { return []; }
    }

    public static byte[] QuotedPrintable(string s, bool header = false)
    {
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '=')
            {
                if (!header && i + 2 < s.Length && s[i + 1] == '\r' && s[i + 2] == '\n') { i += 2; continue; }   // 软换行
                if (!header && i + 1 < s.Length && s[i + 1] == '\n') { i += 1; continue; }
                if (i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                {
                    bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; continue;
                }
            }
            if (c < 0x100) bytes.Add((byte)c); else bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));   // 原始字节按 Latin1 一字一字节存的
        }
        return bytes.ToArray();
    }

    /// <summary>
    /// 头部里直接写 8 位字节（不按 RFC 2047 编码）的国内邮件：按 Latin1 读进来的字节先试 UTF-8，不合法再按 GB18030。
    /// </summary>
    public static string Raw8(string latin1)
    {
        var bytes = Encoding.Latin1.GetBytes(latin1);
        if (bytes.All(b => b < 0x80)) return latin1;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Charset("gb18030").GetString(bytes); }
    }

    /// <summary>头部解析成字典（折行合并，键小写），只留第一次出现的。</summary>
    public static Dictionary<string, string> Headers(string head)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? key = null; var val = new StringBuilder();
        void Flush() { if (key != null && !d.ContainsKey(key)) d[key] = val.ToString().Trim(); key = null; val.Clear(); }
        foreach (var raw in head.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length == 0) { Flush(); break; }
            if ((raw[0] == ' ' || raw[0] == '\t') && key != null) { val.Append(' ').Append(raw.Trim()); continue; }
            Flush();
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            key = raw[..colon].Trim(); val.Append(raw[(colon + 1)..]);
        }
        Flush();
        return d;
    }

    /// <summary>Content-Type 的主类型和某个参数（boundary / charset）。</summary>
    public static (string Type, string? Param) ContentType(string? ct, string param)
    {
        if (string.IsNullOrWhiteSpace(ct)) return ("text/plain", null);
        var parts = ct.Split(';');
        var type = parts[0].Trim().ToLowerInvariant();
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq <= 0) continue;
            if (p[..eq].Trim().Equals(param, StringComparison.OrdinalIgnoreCase)) return (type, p[(eq + 1)..].Trim().Trim('"'));
        }
        return (type, null);
    }

    /// <summary>
    /// 把一段 MIME 内容（头部已知）解成可读文本：multipart 递归挑 text/plain，没有就 text/html 转文本。
    /// raw 是按 Latin1 读进来的原始字节（一个 char 一个字节），解码时再按 charset 转。
    /// </summary>
    public static string BodyText(IReadOnlyDictionary<string, string> headers, string raw, int depth = 0)
    {
        var (type, boundary) = ContentType(headers.GetValueOrDefault("Content-Type"), "boundary");
        if (type.StartsWith("multipart/") && !string.IsNullOrEmpty(boundary) && depth < 4)
        {
            string? html = null;
            foreach (var part in SplitParts(raw, boundary))
            {
                var sep = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var sepLen = 4;
                if (sep < 0) { sep = part.IndexOf("\n\n", StringComparison.Ordinal); sepLen = 2; }
                if (sep < 0) continue;
                var h = Headers(part[..sep] + "\n\n");
                var (pt, _) = ContentType(h.GetValueOrDefault("Content-Type"), "charset");
                if (h.TryGetValue("Content-Disposition", out var cd) && cd.StartsWith("attachment", StringComparison.OrdinalIgnoreCase)) continue;
                var body = part[(sep + sepLen)..];
                if (pt.StartsWith("multipart/"))
                {
                    var inner = BodyText(h, body, depth + 1);
                    if (inner.Length > 0) return inner;
                }
                else if (pt == "text/plain") { var t = Leaf(h, body); if (t.Trim().Length > 0) return t; }
                else if (pt == "text/html" && html == null) html = MailOtp.HtmlToText(Leaf(h, body));
            }
            return html ?? "";
        }
        var text = Leaf(headers, raw);
        return type == "text/html" ? MailOtp.HtmlToText(text) : type.StartsWith("text/") ? text : "";
    }

    private static IEnumerable<string> SplitParts(string raw, string boundary)
    {
        var marker = "--" + boundary;
        var idx = raw.IndexOf(marker, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var start = idx + marker.Length;
            if (start + 1 < raw.Length && raw[start] == '-' && raw[start + 1] == '-') yield break;   // 结束标记
            var next = raw.IndexOf(marker, start, StringComparison.Ordinal);
            var part = next < 0 ? raw[start..] : raw[start..next];   // 截断的最后一段也给（只取了正文前一截）
            yield return part.TrimStart('\r', '\n');
            idx = next;
        }
    }

    private static string Leaf(IReadOnlyDictionary<string, string> h, string raw)
    {
        var (_, charset) = ContentType(h.GetValueOrDefault("Content-Type"), "charset");
        var cte = h.GetValueOrDefault("Content-Transfer-Encoding")?.Trim().ToLowerInvariant() ?? "";
        var bytes = cte switch
        {
            "base64" => FromBase64Loose(raw),
            "quoted-printable" => QuotedPrintable(raw),
            _ => Encoding.Latin1.GetBytes(raw),
        };
        return Charset(charset).GetString(bytes);
    }
}

/// <summary>IMAP 返回里的一行（或带字面量 {n} 的多段）。</summary>
public sealed class ImapLine
{
    public string Text = "";
    public readonly List<string> Literals = [];   // 按 Latin1 读进来：一个 char 一个字节
}

/// <summary>
/// 只读的轻量 IMAP 客户端：纯 .NET SslStream，不引第三方库。
/// 只用 EXAMINE（只读打开收件箱）和 BODY.PEEK（不改「已读」标记），从不 STORE / EXPUNGE / COPY / APPEND。
/// 密码只在 LOGIN 那一条命令里出现一次，不进任何日志；异常消息里也不会带上命令原文。
/// </summary>
public sealed class ImapClient : IDisposable
{
    private Stream? _stream;
    private TcpClient? _tcp;
    private int _tag;
    private readonly byte[] _buf = new byte[16384];
    private int _bufLen, _bufPos;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(20);
    public long UidValidity { get; private set; }
    public bool Connected => _stream != null;
    public List<string> Capabilities { get; } = [];

    public sealed class ImapException(string message, bool auth = false) : Exception(message) { public bool Auth { get; } = auth; }

    /// <summary>测试用：直接接一条现成的流（假服务器）。</summary>
    public void Attach(Stream s) { _stream = s; _bufLen = _bufPos = 0; }

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        Dispose();
        _tcp = new TcpClient { ReceiveTimeout = (int)Timeout.TotalMilliseconds, SendTimeout = (int)Timeout.TotalMilliseconds };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(Timeout);
            await _tcp.ConnectAsync(host, port, cts.Token);
        }
        Stream s = _tcp.GetStream();
        if (port != 143)
        {
            var ssl = new SslStream(s, false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);   // 证书按系统规则校验，不放行无效证书
            s = ssl;
        }
        Attach(s);
        var greet = await ReadLineAsync(ct);
        if (!greet.Text.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) && !greet.Text.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
            throw new ImapException("服务器没有正常应答");
        if (port == 143)
        {
            // 明文端口：必须先升级到 TLS，不支持就不登录（不在明文里发授权码）
            var (ok, _) = await CommandAsync("STARTTLS", ct);
            if (!ok) throw new ImapException("这个服务器的 143 端口不支持 STARTTLS，为了不明文发送授权码已停止；请改用 993");
            var ssl = new SslStream(_tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            Attach(ssl);
        }
    }

    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        if (user.Any(char.IsControl) || password.Any(char.IsControl)) throw new ImapException("账号或授权码里有不能用的字符", true);
        var (ok, lines) = await CommandAsync("LOGIN " + Quote(user) + " " + Quote(password), ct, sensitive: true);
        if (!ok)
        {
            var last = lines.Count > 0 ? lines[^1].Text : "";
            throw new ImapException(Friendly(last), true);
        }
        await CapabilityAsync(ct);
        // 网易邮箱要求先报客户端身份（ID，RFC 2971），否则 SELECT/EXAMINE 报 Unsafe Login
        if (Capabilities.Contains("ID")) await CommandAsync("ID (\"name\" \"Qisland\" \"version\" \"4.3\" \"vendor\" \"Qisland\")", ct);
    }

    private static string Friendly(string serverLine)
    {
        var l = serverLine.ToLowerInvariant();
        if (l.Contains("authenticationfailed") || l.Contains("invalid") || l.Contains("password") || l.Contains("login fail") || l.Contains("auth"))
            return "登录失败：账号或授权码不对（注意是邮箱设置里生成的授权码，不是登录密码），或邮箱没开 IMAP";
        if (l.Contains("unsafe")) return "邮箱服务商拦下了这次登录（Unsafe Login），去邮箱设置里确认 IMAP 已开启";
        return "登录失败：服务器拒绝了";
    }

    public async Task CapabilityAsync(CancellationToken ct)
    {
        var (_, lines) = await CommandAsync("CAPABILITY", ct);
        Capabilities.Clear();
        foreach (var l in lines)
            if (l.Text.StartsWith("* CAPABILITY ", StringComparison.OrdinalIgnoreCase))
                Capabilities.AddRange(l.Text[13..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToUpperInvariant()));
    }

    /// <summary>只读打开收件箱。</summary>
    public async Task<long> ExamineInboxAsync(CancellationToken ct)
    {
        var (ok, lines) = await CommandAsync("EXAMINE INBOX", ct);
        if (!ok) throw new ImapException("打不开收件箱：" + (lines.Count > 0 && lines[^1].Text.Contains("Unsafe", StringComparison.OrdinalIgnoreCase) ? "服务商拦截（Unsafe Login）" : "服务器拒绝"));
        foreach (var l in lines)
        {
            var m = Regex.Match(l.Text, @"UIDVALIDITY (\d+)", RegexOptions.IgnoreCase);
            if (m.Success) UidValidity = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return UidValidity;
    }

    public async Task NoopAsync(CancellationToken ct)
    {
        var (ok, _) = await CommandAsync("NOOP", ct);
        if (!ok) throw new ImapException("连接已失效");
    }

    /// <summary>UID 大于 after 的邮件。「n:*」在没有新邮件时也会回最后一封，要再过滤一次。</summary>
    public async Task<List<long>> UidsAfterAsync(long after, CancellationToken ct)
    {
        var (ok, lines) = await CommandAsync($"UID SEARCH UID {after + 1}:*", ct);
        if (!ok) throw new ImapException("搜索失败");
        return ParseSearch(lines.Select(l => l.Text)).Where(u => u > after).OrderBy(u => u).ToList();
    }

    /// <summary>收件箱里最大的 UID（第一次连上时用来跳过已有的邮件）。</summary>
    public async Task<long> MaxUidAsync(CancellationToken ct)
    {
        var (ok, lines) = await CommandAsync("UID SEARCH ALL", ct);
        if (!ok) throw new ImapException("搜索失败");
        var all = ParseSearch(lines.Select(l => l.Text));
        return all.Count > 0 ? all.Max() : 0;
    }

    public static List<long> ParseSearch(IEnumerable<string> lines)
    {
        var res = new List<long>();
        foreach (var l in lines)
        {
            if (!l.StartsWith("* SEARCH", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var p in l[8..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var u)) res.Add(u);
        }
        return res;
    }

    /// <summary>只读取一封信的头部和正文前 maxBytes 字节（BODY.PEEK 不会把信标成已读）。</summary>
    public async Task<MailPeek?> PeekAsync(long uid, int maxBytes, CancellationToken ct)
    {
        var (ok, lines) = await CommandAsync($"UID FETCH {uid} (UID INTERNALDATE BODY.PEEK[HEADER] BODY.PEEK[TEXT]<0.{maxBytes}>)", ct);
        if (!ok) return null;
        return ParsePeek(uid, lines);
    }

    public static MailPeek? ParsePeek(long uid, List<ImapLine> lines)
    {
        foreach (var l in lines)
        {
            if (!l.Text.StartsWith("* ", StringComparison.Ordinal) || !l.Text.Contains("FETCH", StringComparison.OrdinalIgnoreCase) || l.Literals.Count == 0) continue;
            string? head = null, text = null;
            // 字面量的顺序跟着 Text 里 {n} 的顺序：按前面的段名分配
            var marks = Regex.Matches(l.Text, @"BODY\[(HEADER|TEXT)\](?:<\d+>)?\s*\{\d+\}", RegexOptions.IgnoreCase);
            var allLits = Regex.Matches(l.Text, @"\{\d+\}");
            for (var i = 0; i < marks.Count; i++)
            {
                var litIdx = -1;
                for (var k = 0; k < allLits.Count; k++)
                    if (allLits[k].Index > marks[i].Index && allLits[k].Index < marks[i].Index + marks[i].Length) { litIdx = k; break; }
                if (litIdx < 0 || litIdx >= l.Literals.Count) continue;
                if (marks[i].Groups[1].Value.Equals("HEADER", StringComparison.OrdinalIgnoreCase)) head = l.Literals[litIdx]; else text = l.Literals[litIdx];
            }
            if (head == null) continue;
            var h = Mime.Headers(Mime.Raw8(head));
            DateTime? date = null;
            var dm = Regex.Match(l.Text, "INTERNALDATE \"([^\"]+)\"", RegexOptions.IgnoreCase);
            if (dm.Success && DateTimeOffset.TryParseExact(dm.Groups[1].Value.Trim(), ["d-MMM-yyyy HH:mm:ss zzz", "dd-MMM-yyyy HH:mm:ss zzz"], CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto))
                date = dto.UtcDateTime;
            var subject = Mime.DecodeHeader(h.GetValueOrDefault("Subject") ?? "");
            var from = h.GetValueOrDefault("From") ?? "";
            var body = text != null ? Mime.BodyText(h, text) : "";
            return new MailPeek(uid, from, subject, body, date);
        }
        return null;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        try { if (_stream != null) await CommandAsync("LOGOUT", ct); } catch { }
        Dispose();
    }

    // ───────────── 收发 ─────────────

    public async Task<(bool Ok, List<ImapLine> Lines)> CommandAsync(string command, CancellationToken ct, bool sensitive = false)
    {
        if (_stream == null) throw new ImapException("还没连上");
        var tag = "Q" + (++_tag).ToString("D3", CultureInfo.InvariantCulture);
        var bytes = Encoding.UTF8.GetBytes(tag + " " + command + "\r\n");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            await _stream.WriteAsync(bytes, cts.Token);
            await _stream.FlushAsync(cts.Token);
        }
        finally { if (sensitive) Array.Clear(bytes); }
        var lines = new List<ImapLine>();
        while (true)
        {
            var line = await ReadLineAsync(ct);
            lines.Add(line);
            if (line.Text.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                var rest = line.Text[(tag.Length + 1)..];
                return (rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase), lines);
            }
            if (lines.Count > 5000) throw new ImapException("服务器返回太多");
        }
    }

    private async Task<int> ReadByteAsync(CancellationToken ct)
    {
        if (_bufPos >= _bufLen)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            _bufLen = await _stream!.ReadAsync(_buf, cts.Token);
            _bufPos = 0;
            if (_bufLen <= 0) throw new ImapException("服务器断开了连接");
        }
        return _buf[_bufPos++];
    }

    private async Task<string> ReadRawLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = await ReadByteAsync(ct);
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
            if (sb.Length > 1_000_000) throw new ImapException("服务器返回的行太长");
        }
        return sb.ToString();
    }

    /// <summary>读一条完整应答：行尾是 {n} 就接着读 n 个字节的字面量，再读下一段，直到一行不以 {n} 结尾。</summary>
    public async Task<ImapLine> ReadLineAsync(CancellationToken ct)
    {
        var line = new ImapLine();
        var text = new StringBuilder();
        while (true)
        {
            var raw = await ReadRawLineAsync(ct);
            text.Append(raw);
            var m = Regex.Match(raw, @"\{(\d+)\}$");
            if (!m.Success) break;
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n > 4_000_000) throw new ImapException("邮件太大");
            var lit = new StringBuilder(n);
            for (var i = 0; i < n; i++) lit.Append((char)await ReadByteAsync(ct));
            line.Literals.Add(lit.ToString());
        }
        line.Text = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(text.ToString()));
        return line;
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null; _tcp = null; _bufLen = _bufPos = 0;
        Capabilities.Clear();
    }
}
