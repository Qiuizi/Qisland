using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qisland.Core;

// ───────────────────────── 网络边界 ─────────────────────────

/// <summary>
/// L2 声明式卡片的网络边界：只允许 https + 默认 443 端口 + 声明过的域名；拒绝 IP 字面量、本机 / 内网名字、
/// 跨白名单重定向；请求头只放行少数无害的几个。宿主在发请求前、每次重定向、以及真正建连时（解析出的 IP）各查一遍。
/// </summary>
public static class UrlPolicy
{
    private static readonly Regex Label = new("^(?!-)[a-z0-9-]{1,63}(?<!-)$", RegexOptions.CultureInvariant);
    private static readonly string[] PrivateSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa", ".intranet", ".corp", ".home", ".localdomain", ".test", ".invalid"];
    private static readonly string[] SafeHeaders = ["Accept", "Accept-Language", "User-Agent"];

    public static int ClampInterval(int seconds) => Math.Clamp(seconds <= 0 ? CardLimits.DefaultIntervalSeconds : seconds, CardLimits.MinIntervalSeconds, CardLimits.MaxIntervalSeconds);

    /// <summary>白名单里能写的域名：普通公网 DNS 名（至少两段），不能是 IP、localhost、.local 这类内网名。</summary>
    public static bool IsValidDomain(string host, out string? why)
    {
        why = null;
        host = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length is < 4 or > 253) { why = "长度不对"; return false; }
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) { why = "不允许 IP 字面量"; return false; }
        if (host == "localhost" || PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal))) { why = "不允许本机 / 内网名字"; return false; }
        if (host.Contains('*')) { why = "不支持通配符，逐个写全"; return false; }
        var labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(l => !Label.IsMatch(l))) { why = "不是合法域名"; return false; }
        if (labels[^1].All(char.IsDigit)) { why = "不是合法域名"; return false; }
        return true;
    }

    /// <summary>
    /// 198.18.0.0/15（基准测试段）不拦：Clash 等代理的 fake-ip 模式把所有域名解析到这里，拦了会让开代理的用户全部失败。
    /// 公网地址才放行：回环、私网、链路本地、CGNAT、组播、保留段、IPv6 ULA / 链路本地 / 映射的私网地址都拒绝。</summary>
    public static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any)) return false;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                 // fc00::/7 ULA
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 文档段
            if (b.Take(12).All(x => x == 0)) return false;           // ::/96（含 ::1、IPv4 兼容地址）
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B) return false; // 64:ff9b::/96 NAT64，可能指向内网
            return true;
        }
        return false;
    }

    private static bool IsPrivateLiteral(IPAddress ip) => IPAddress.IsLoopback(ip) || !IsPublicAddress(ip);

    /// <summary>「本机数据」源声明：127.0.0.1:端口、localhost:端口 或内网 IP:端口，必须带端口。</summary>
    public static bool IsValidLocalHostPort(string hp)
    {
        var i = hp.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(hp[(i + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) return false;
        var host = hp[..i].Trim('[', ']');
        if (host == "localhost") return true;
        return IPAddress.TryParse(host, out var ip) && IsPrivateLiteral(ip);
    }

    /// <summary>网址模板里 scheme://authority 部分（第一个 / ? # 之前）。authority 必须是写死的，不许带模板。</summary>
    private static bool SplitTemplate(string url, string scheme, out string authority)
    {
        authority = "";
        if (!url.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = url[(scheme.Length + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        authority = (end < 0 ? rest : rest[..end]).ToLowerInvariant();
        return true;
    }

    /// <summary>静态检查 https 数据源的网址模板：https、域名写死且在白名单里、端口只能是 443、不带用户名密码。</summary>
    public static bool CheckTemplate(string url, IReadOnlyCollection<string> domains, out string? why)
    {
        why = null;
        if (url.Length is 0 or > 400) { why = "网址为空或太长"; return false; }
        if (!SplitTemplate(url, "https", out var auth)) { why = "只允许 https:// 网址"; return false; }
        if (auth.Contains("{{") || auth.Contains('{') || auth.Contains('}')) { why = "域名部分不能用模板"; return false; }
        if (auth.Contains('@')) { why = "网址里不能带用户名密码"; return false; }
        var host = auth;
        if (auth.Contains(':') && !auth.StartsWith('['))
        {
            var port = auth[(auth.LastIndexOf(':') + 1)..];
            if (port != "443") { why = "只允许默认 443 端口"; return false; }
            host = auth[..auth.LastIndexOf(':')];
        }
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) { why = "不允许 IP 字面量"; return false; }
        if (!domains.Contains(host.TrimEnd('.'), StringComparer.OrdinalIgnoreCase)) { why = $"域名 {host} 不在 permissions.domains 白名单里"; return false; }
        return true;
    }

    /// <summary>运行时检查拼好的网址（每次请求、每次重定向都查）。</summary>
    public static bool CheckHttps(string url, IReadOnlyCollection<string> domains, out Uri? uri, out string? why)
    {
        why = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) { why = "网址无效"; return false; }
        if (uri.Scheme != Uri.UriSchemeHttps) { why = "只允许 https"; return false; }
        if (uri.UserInfo.Length > 0) { why = "网址里不能带用户名密码"; return false; }
        if (uri.Port != 443) { why = "只允许默认 443 端口"; return false; }
        if (uri.HostNameType != UriHostNameType.Dns) { why = "不允许 IP 字面量"; return false; }
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!IsValidDomain(host, out var w)) { why = w; return false; }
        if (!domains.Contains(host, StringComparer.OrdinalIgnoreCase)) { why = $"域名 {host} 不在白名单里"; return false; }
        return true;
    }

    /// <summary>重定向：最多 3 跳，每一跳都必须仍然是白名单里的 https 地址。</summary>
    public static bool CheckRedirect(Uri from, string? location, IReadOnlyCollection<string> domains, int hop, out Uri? next, out string? why)
    {
        next = null;
        if (hop > CardLimits.MaxRedirects) { why = $"重定向超过 {CardLimits.MaxRedirects} 次"; return false; }
        if (string.IsNullOrWhiteSpace(location) || !Uri.TryCreate(from, location, out var abs)) { why = "重定向地址无效"; return false; }
        return CheckHttps(abs.AbsoluteUri, domains, out next, out why) || Fail(out next, why);
    }

    private static bool Fail(out Uri? u, string? why) { u = null; return false; }

    public static bool CheckLocalTemplate(string url, IReadOnlyCollection<string> locals, out string? why)
    {
        why = null;
        if (url.Length is 0 or > 400) { why = "网址为空或太长"; return false; }
        if (!SplitTemplate(url, "http", out var auth) && !SplitTemplate(url, "https", out auth)) { why = "本机数据源只允许 http(s)://"; return false; }
        if (auth.Contains('{') || auth.Contains('@')) { why = "地址部分不能用模板或带用户名"; return false; }
        if (!IsValidLocalHostPort(auth)) { why = "本机数据源要写成 127.0.0.1:端口"; return false; }
        if (!locals.Contains(auth, StringComparer.OrdinalIgnoreCase)) { why = $"{auth} 不在 permissions.local 里"; return false; }
        return true;
    }

    public static bool CheckLocal(string url, IReadOnlyCollection<string> locals, out Uri? uri, out string? why)
    {
        why = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https")) { why = "网址无效"; return false; }
        if (uri.UserInfo.Length > 0) { why = "网址里不能带用户名密码"; return false; }
        var auth = (uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.IdnHost.Trim('[', ']') + "]" : uri.IdnHost).ToLowerInvariant() + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        if (!locals.Contains(auth, StringComparer.OrdinalIgnoreCase) || !IsValidLocalHostPort(auth)) { why = $"{auth} 不在本机数据源白名单里"; return false; }
        return true;
    }

    /// <summary>按钮链接模板：只允许 https，域名写死。</summary>
    public static bool CheckOpenTemplate(string url, out string? why)
    {
        why = null;
        if (url.Length is 0 or > 400) { why = "链接为空或太长"; return false; }
        if (!SplitTemplate(url, "https", out var auth)) { why = "按钮只能打开 https:// 链接"; return false; }
        if (auth.Length == 0 || auth.Contains('{') || auth.Contains('@')) { why = "链接的域名部分要写死"; return false; }
        return true;
    }

    public static bool CheckOpen(string url, out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.HostNameType != UriHostNameType.Dns) { uri = null; return false; }
        return true;
    }

    public static bool IsHeaderAllowed(string name, string value, out string? why)
    {
        why = null;
        if (!SafeHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)) { why = "只允许 Accept / Accept-Language / User-Agent"; return false; }
        if (value.Length is 0 or > 200 || value.Any(c => char.IsControl(c))) { why = "值为空、太长或含换行"; return false; }
        return true;
    }
}

// ───────────────────────── JSON 路径 ─────────────────────────

/// <summary>
/// 安全轻量的 JSON 路径：$.a.b、$.items[0].name、$['key']、[-1] 取最后一个、[*] 展开数组（只允许一次）。
/// 不支持过滤表达式、递归下降、脚本；路径最多 24 段，结果最多 400 项。
/// </summary>
public static class JsonPick
{
    private abstract record Tok;
    private sealed record Name(string N) : Tok;
    private sealed record Index(int I) : Tok;
    private sealed record Wild : Tok;

    private static bool Tokenize(string path, List<Tok> toks, out string? why)
    {
        why = null;
        var s = path.Trim();
        var i = 0;
        if (s.StartsWith('$')) i = 1;
        while (i < s.Length)
        {
            if (toks.Count >= CardLimits.MaxPathTokens) { why = "路径太深"; return false; }
            var c = s[i];
            if (c == '.')
            {
                i++;
                var st = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                var n = s[st..i];
                if (n.Length is 0 or > 64) { why = "路径里有空的或太长的字段名"; return false; }
                if (n == "*") toks.Add(new Wild()); else toks.Add(new Name(n));
            }
            else if (c == '[')
            {
                var end = s.IndexOf(']', i);
                if (end < 0) { why = "路径里的 [ 没有闭合"; return false; }
                var inner = s[(i + 1)..end].Trim();
                i = end + 1;
                if (inner == "*") toks.Add(new Wild());
                else if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
                {
                    var n = inner[1..^1];
                    if (n.Length is 0 or > 64) { why = "字段名太长"; return false; }
                    toks.Add(new Name(n));
                }
                else if (int.TryParse(inner, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var idx) && idx is > -10000 and < 10000) toks.Add(new Index(idx));
                else { why = $"路径里的 [{inner}] 不支持"; return false; }
            }
            else if (toks.Count == 0 && i == (s.StartsWith('$') ? 1 : 0))
            {
                // 允许省略开头的点：a.b[0]
                var st = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                var n = s[st..i];
                if (n.Length > 64) { why = "字段名太长"; return false; }
                toks.Add(n == "*" ? new Wild() : new Name(n));
            }
            else { why = $"路径在第 {i + 1} 个字符处无法识别"; return false; }
        }
        if (toks.Count(t => t is Wild) > 1) { why = "[*] 只能用一次"; return false; }
        return true;
    }

    public static bool IsValidPath(string path, out string? why) => Tokenize(path, [], out why);

    /// <summary>取值。没有 [*] 时最多一个结果；找不到返回空列表。</summary>
    public static List<JsonElement> Select(JsonElement root, string path)
    {
        var toks = new List<Tok>();
        if (!Tokenize(path, toks, out _)) return [];
        var cur = new List<JsonElement> { root };
        foreach (var t in toks)
        {
            var next = new List<JsonElement>();
            foreach (var e in cur)
            {
                switch (t)
                {
                    case Name n when e.ValueKind == JsonValueKind.Object:
                        if (e.TryGetProperty(n.N, out var v)) next.Add(v);
                        break;
                    case Index ix when e.ValueKind == JsonValueKind.Array:
                        var len = e.GetArrayLength();
                        var k = ix.I < 0 ? len + ix.I : ix.I;
                        if (k >= 0 && k < len) next.Add(e[k]);
                        break;
                    case Wild when e.ValueKind == JsonValueKind.Array:
                        foreach (var x in e.EnumerateArray()) { if (next.Count >= CardLimits.MaxPicked) break; next.Add(x); }
                        break;
                    case Wild when e.ValueKind == JsonValueKind.Object:
                        foreach (var x in e.EnumerateObject()) { if (next.Count >= CardLimits.MaxPicked) break; next.Add(x.Value); }
                        break;
                }
                if (next.Count >= CardLimits.MaxPicked) break;
            }
            cur = next;
            if (cur.Count == 0) break;
        }
        return cur;
    }
}

// ───────────────────────── 模板 ─────────────────────────

/// <summary>模板求值的上下文：用户配置、各数据源的 JSON、当前时间。</summary>
public sealed class CardContext
{
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, JsonElement> Data { get; init; } = new Dictionary<string, JsonElement>();
    public DateTime Now { get; init; } = DateTime.Now;
}

/// <summary>
/// {{表达式|过滤器:参数|...}} 模板。表达式：config.键、now.date / now.time、数据源名.路径 或 数据源名:$.JSON路径；
/// 路径里的 {键} 换成对应配置值（只允许字母数字 _-）。只做一遍替换，值里的 {{ }} 不会再被展开；输出是纯文本。
/// </summary>
public static class CardTemplate
{
    private static readonly Regex Binding = new(@"\{\{((?:[^{}]|\{[a-z][a-z0-9_]{0,31}\})*?)\}\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Placeholder = new(@"\{([a-z][a-z0-9_]{0,31})\}", RegexOptions.CultureInvariant);
    private static readonly Regex SafeKey = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.CultureInvariant);
    private static readonly Regex DateFmt = new("^[yMdHhms:/ .\\-年月日周]{1,24}$", RegexOptions.CultureInvariant);

    public static readonly string[] Filters =
    [
        "num", "fixed", "compact", "percent", "pct", "mul", "div", "add", "sub", "round", "abs", "inv", "sign",
        "default", "trim", "upper", "lower", "date", "ago", "days", "unix",
        "first", "last", "tail", "head", "sum", "avg", "min", "max", "count", "delta", "growth", "url", "path",
    ];

    /// <summary>求值中间结果：文字、数字、数字列表或文字列表。</summary>
    public sealed class Val
    {
        public string? S; public double? N; public List<double>? Ns; public List<string>? Ss; public bool Missing; public bool Invalid;
        public static Val None => new() { Missing = true };
        public double? Num => N ?? (S != null && double.TryParse(S.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null);
        public List<double>? Nums => Ns ?? Ss?.Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (double?)d : null).Where(x => x != null).Select(x => x!.Value).ToList();
        public override string ToString()
        {
            if (Missing || Invalid) return "—";
            if (S != null) return S;
            if (N is { } n) return FormatPlain(n);
            if (Ns != null) return string.Join(", ", Ns.Take(5).Select(FormatPlain));
            if (Ss != null) return string.Join(", ", Ss.Take(5));
            return "—";
        }
    }

    private static string FormatPlain(double n) => double.IsFinite(n) ? n.ToString("0.####", CultureInfo.InvariantCulture) : "—";

    private static (string head, string path, List<(string f, string arg)> filters) Split(string binding)
    {
        var parts = binding.Split('|');
        var expr = parts[0].Trim();
        var filters = parts.Skip(1).Select(p =>
        {
            p = p.Trim();
            var c = p.IndexOf(':');
            return c < 0 ? (p.ToLowerInvariant(), "") : (p[..c].Trim().ToLowerInvariant(), p[(c + 1)..].Trim());
        }).ToList();
        string head, path;
        if (expr.StartsWith("config.", StringComparison.Ordinal)) { head = "config"; path = expr[7..]; }
        else if (expr == "now" || expr.StartsWith("now.", StringComparison.Ordinal)) { head = "now"; path = expr.Length > 4 ? expr[4..] : ""; }
        else
        {
            var colon = expr.IndexOf(':');
            if (colon > 0) { head = expr[..colon]; path = expr[(colon + 1)..]; }
            else
            {
                var cut = expr.IndexOfAny(['.', '[']);
                head = cut < 0 ? expr : expr[..cut];
                path = cut < 0 ? "" : expr[cut..];
            }
        }
        return (head.Trim(), path.Trim(), filters);
    }

    private static bool CheckOne(string binding, IReadOnlyList<string> sources, IReadOnlyList<string> config, bool allowNow, out string? why)
    {
        why = null;
        var (head, path, filters) = Split(binding);
        if (filters.Count > CardLimits.MaxFilters) { why = "过滤器太多"; return false; }
        foreach (var (f, _) in filters) if (!Filters.Contains(f)) { why = $"不认识的过滤器 {f}"; return false; }
        switch (head)
        {
            case "config":
                if (!config.Contains(path)) { why = $"没有声明参数 {path}"; return false; }
                return true;
            case "now":
                if (!allowNow) { why = "这里不能用 now"; return false; }
                if (path is not ("" or "date" or "time" or "weekday" or "iso")) { why = "now 只有 date / time / weekday / iso"; return false; }
                return true;
            default:
                if (!sources.Contains(head)) { why = $"没有数据源 {head}"; return false; }
                foreach (Match m in Placeholder.Matches(path)) if (!config.Contains(m.Groups[1].Value)) { why = $"路径里的 {{{m.Groups[1].Value}}} 不是已声明参数"; return false; }
                var p = Placeholder.Replace(path, "x");
                if (p.Length > 0 && !JsonPick.IsValidPath(p.StartsWith('$') || p.StartsWith('.') || p.StartsWith('[') ? p : "." + p, out var w)) { why = w; return false; }
                return true;
        }
    }

    private static bool CheckAll(string template, Func<string, (bool ok, string? why)> one, out string? why)
    {
        why = null;
        if (template.Length > 400) { why = "模板太长"; return false; }
        var ms = Binding.Matches(template);
        if (ms.Count > 16) { why = "模板里的 {{ }} 太多"; return false; }
        foreach (Match m in ms) { var (ok, w) = one(m.Groups[1].Value); if (!ok) { why = w; return false; } }
        return true;
    }

    /// <summary>展示文字里的模板：数据源、配置、now 都能用。</summary>
    public static bool CheckBindings(string template, IReadOnlyList<string> sources, IReadOnlyList<string> config, out string? why) =>
        CheckAll(template, b => (CheckOne(b, sources, config, true, out var w), w), out why);

    /// <summary>网址模板：只能引用配置和排在前面的数据源（链式请求），不能用 now。</summary>
    public static bool CheckUrlBindings(string template, IReadOnlyList<string> config, IReadOnlyList<string> earlierSources, out string? why) =>
        CheckAll(template, b => (CheckOne(b, earlierSources, config, false, out var w), w), out why);

    public static Val Resolve(string binding, CardContext ctx)
    {
        var (head, path, filters) = Split(binding);
        Val v;
        switch (head)
        {
            case "config":
                v = ctx.Config.TryGetValue(path, out var s) ? new Val { S = s } : Val.None;
                break;
            case "now":
                v = new Val
                {
                    S = path switch
                    {
                        "time" => ctx.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
                        "weekday" => Week(ctx.Now),
                        "iso" => ctx.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                        _ => ctx.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    },
                };
                break;
            default:
                v = ctx.Data.TryGetValue(head, out var root) ? Pick(root, path, ctx) : Val.None;
                break;
        }
        foreach (var (f, arg) in filters.Take(CardLimits.MaxFilters)) v = Apply(v, f, arg, ctx);
        return v;
    }

    private static Val Pick(JsonElement root, string path, CardContext ctx)
    {
        var bad = false;
        var p = Placeholder.Replace(path, m =>
        {
            var val = ctx.Config.TryGetValue(m.Groups[1].Value, out var s) ? s : "";
            if (!SafeKey.IsMatch(val)) { bad = true; return "_"; }
            return val;
        });
        if (bad) return new Val { Invalid = true };
        if (p.Length > 0 && !(p.StartsWith('$') || p.StartsWith('.') || p.StartsWith('['))) p = "." + p;
        var wild = p.Contains("[*]") || p.Contains(".*");
        var hits = p.Length == 0 ? [root] : JsonPick.Select(root, p);
        if (hits.Count == 0) return Val.None;
        if (!wild && hits.Count == 1)
        {
            var e = hits[0];
            if (e.ValueKind == JsonValueKind.Array) return FromList(e.EnumerateArray().Take(CardLimits.MaxPicked).ToList());
            return FromElement(e);
        }
        return FromList(hits);
    }

    private static Val FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => new Val { N = e.TryGetDouble(out var d) ? d : null },
        JsonValueKind.String => new Val { S = PluginManifest.Clean(e.GetString(), CardLimits.MaxText) },
        JsonValueKind.True => new Val { S = "true" },
        JsonValueKind.False => new Val { S = "false" },
        JsonValueKind.Object => new Val { S = "{…}" },
        _ => Val.None,
    };

    private static Val FromList(List<JsonElement> list)
    {
        if (list.All(x => x.ValueKind == JsonValueKind.Number)) return new Val { Ns = list.Select(x => x.TryGetDouble(out var d) ? d : 0).ToList() };
        return new Val { Ss = list.Select(x => x.ValueKind == JsonValueKind.String ? PluginManifest.Clean(x.GetString(), CardLimits.MaxText) : x.ValueKind == JsonValueKind.Number ? x.GetRawText() : "").ToList() };
    }

    private static double Arg(string a, double d) => double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) ? x : d;
    private static int Digits(string a, int d) => Math.Clamp((int)Arg(a, d), 0, 6);
    private static string Week(DateTime t) => "周" + "日一二三四五六"[(int)t.DayOfWeek];

    private static Val Num(double? n) => n is { } x && double.IsFinite(x) ? new Val { N = x } : Val.None;

    private static DateTime? AsDate(Val v)
    {
        if (v.N is { } n)
        {
            if (n > 1e11) n /= 1000;   // 毫秒时间戳
            if (n is > 0 and < 1e10) return DateTimeOffset.FromUnixTimeSeconds((long)n).LocalDateTime;
            return null;
        }
        if (v.S is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dto))
            return s.Length <= 10 ? dto.DateTime : dto.LocalDateTime;
        return null;
    }

    public static string Compact(double n)
    {
        var a = Math.Abs(n);
        string F(double x, string u) => (x >= 100 ? x.ToString("0", CultureInfo.InvariantCulture) : x.ToString("0.#", CultureInfo.InvariantCulture)) + u;
        var sign = n < 0 ? "-" : "";
        return a >= 1e9 ? sign + F(a / 1e9, "B") : a >= 1e6 ? sign + F(a / 1e6, "M") : a >= 1e3 ? sign + F(a / 1e3, "k") : n.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string Ago(DateTime t, DateTime now)
    {
        var d = now - t;
        if (d.TotalSeconds < 0)
        {
            var f = -d;
            return f.TotalHours < 1 ? $"{Math.Max(1, (int)f.TotalMinutes)} 分钟后" : f.TotalDays < 1 ? $"{(int)f.TotalHours} 小时后" : $"{(int)Math.Ceiling(f.TotalDays)} 天后";
        }
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} 天前";
        return t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static Val Apply(Val v, string f, string arg, CardContext ctx)
    {
        if (v.Invalid) return v;
        if (f == "default") return v.Missing || (v.S?.Length == 0) ? new Val { S = PluginManifest.Clean(arg, 40) } : v;
        if (v.Missing) return v;
        switch (f)
        {
            case "num": return v.Num is { } n0 ? new Val { S = n0.ToString("N" + Digits(arg, 0), CultureInfo.InvariantCulture) } : Val.None;
            case "fixed": return v.Num is { } n1 ? new Val { S = n1.ToString("F" + Digits(arg, 2), CultureInfo.InvariantCulture) } : Val.None;
            case "compact": return v.Num is { } n2 ? new Val { S = Compact(n2) } : Val.None;
            case "percent": return v.Num is { } n3 ? new Val { S = (n3 * 100).ToString("F" + Digits(arg, 0), CultureInfo.InvariantCulture) + "%" } : Val.None;
            case "pct": return v.Num is { } n4 ? new Val { S = n4.ToString("F" + Digits(arg, 0), CultureInfo.InvariantCulture) + "%" } : Val.None;
            case "mul": return Num(v.Num * Arg(arg, 1));
            case "div": { var d = Arg(arg, 1); return d == 0 ? Val.None : Num(v.Num / d); }
            case "add": return Num(v.Num + Arg(arg, 0));
            case "sub": return Num(v.Num - Arg(arg, 0));
            case "round": return v.Num is { } n5 ? Num(Math.Round(n5, Digits(arg, 0), MidpointRounding.AwayFromZero)) : Val.None;
            case "abs": return Num(v.Num is { } n6 ? Math.Abs(n6) : null);
            case "inv": return v.Num is { } n7 && n7 != 0 ? Num(1 / n7) : Val.None;
            case "sign":
                if (v.N is { } n8) return new Val { S = (n8 > 0 ? "+" : "") + FormatPlain(n8) };
                return v.S is { Length: > 0 } s8 && v.Num is > 0 && !s8.StartsWith('+') ? new Val { S = "+" + s8 } : v;
            case "trim":
            {
                var n = Math.Clamp((int)Arg(arg, 40), 1, CardLimits.MaxText);
                var s = v.ToString();
                return new Val { S = s.Length <= n ? s : s[..n].TrimEnd() + "…" };
            }
            case "upper": return new Val { S = v.ToString().ToUpperInvariant() };
            case "lower": return new Val { S = v.ToString().ToLowerInvariant() };
            case "unix": return v.Num is { } u ? new Val { S = DateTimeOffset.FromUnixTimeSeconds((long)Math.Clamp(u, 0, 253402300799)).LocalDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) } : Val.None;
            case "date":
            {
                if (AsDate(v) is not { } t) return Val.None;
                var fmt = arg.Length > 0 && DateFmt.IsMatch(arg) ? arg : "M月d日";
                return new Val { S = t.ToString(fmt.Replace("周", "'周'"), CultureInfo.InvariantCulture) };
            }
            case "ago": return AsDate(v) is { } t2 ? new Val { S = Ago(t2, ctx.Now) } : Val.None;
            case "days": return AsDate(v) is { } t3 ? Num(Math.Ceiling((t3.Date - ctx.Now.Date).TotalDays)) : Val.None;
            case "first": return v.Nums is { Count: > 0 } a1 && v.Ss == null ? Num(a1[0]) : v.Ss is { Count: > 0 } s1 ? new Val { S = s1[0] } : Val.None;
            case "last": return v.Nums is { Count: > 0 } a2 && v.Ss == null ? Num(a2[^1]) : v.Ss is { Count: > 0 } s2 ? new Val { S = s2[^1] } : Val.None;
            case "tail": { var n = Math.Clamp((int)Arg(arg, 7), 1, CardLimits.MaxPicked); return v.Nums is { } a ? new Val { Ns = a.Skip(Math.Max(0, a.Count - n)).ToList() } : v; }
            case "head": { var n = Math.Clamp((int)Arg(arg, 7), 1, CardLimits.MaxPicked); return v.Nums is { } a ? new Val { Ns = a.Take(n).ToList() } : v; }
            case "sum": return v.Nums is { } b1 ? Num(b1.Sum()) : v.N is { } ? v : Val.None;
            case "avg": return v.Nums is { Count: > 0 } b2 ? Num(b2.Average()) : v.N is { } ? v : Val.None;
            case "min": return v.Nums is { Count: > 0 } b3 ? Num(b3.Min()) : v.N is { } ? v : Val.None;
            case "max": return v.Nums is { Count: > 0 } b4 ? Num(b4.Max()) : v.N is { } ? v : Val.None;
            case "count": return Num(v.Ns?.Count ?? v.Ss?.Count ?? 1);
            case "delta": return v.Nums is { Count: > 1 } b5 ? Num(b5[^1] - b5[0]) : Val.None;
            case "growth":
            {
                // 最近 n 个之和 对比 再往前 n 个之和的增长率（0.12 = +12%）
                var n = Math.Clamp((int)Arg(arg, 7), 1, 200);
                if (v.Nums is not { } a || a.Count < 2 * n) return Val.None;
                var cur = a.Skip(a.Count - n).Sum(); var prev = a.Skip(a.Count - 2 * n).Take(n).Sum();
                return prev == 0 ? Val.None : Num((cur - prev) / prev);
            }
            case "url": return new Val { S = Uri.EscapeDataString(v.ToString()) };
            case "path":
            {
                var segs = v.ToString().Split('/');
                if (segs.Any(x => x is "" or "." or "..")) return new Val { Invalid = true };
                return new Val { S = string.Join("/", segs.Select(Uri.EscapeDataString)) };
            }
        }
        return v;
    }

    /// <summary>展示文字：替换所有 {{ }}，缺数据显示「—」；结果去控制字符并截断。</summary>
    public static string Render(string template, CardContext ctx)
    {
        if (string.IsNullOrEmpty(template)) return "";
        if (template.Length > 400) template = template[..400];
        string r;
        try { r = Binding.Replace(template, m => Resolve(m.Groups[1].Value, ctx).ToString()); }
        catch (RegexMatchTimeoutException) { r = "—"; }
        return PluginManifest.Clean(r, CardLimits.MaxText);
    }

    /// <summary>
    /// 网址模板：每个值默认整体 URL 编码（/ ? # &amp; 都会被转义），最后一个过滤器是 path 时按段编码（保留 /，拒绝 . 和 ..）。
    /// 值无效（缺数据、path 不合法）时返回 null，不发请求。
    /// </summary>
    public static string? RenderUrl(string template, CardContext ctx)
    {
        var bad = false;
        string r;
        try
        {
            r = Binding.Replace(template, m =>
            {
                var b = m.Groups[1].Value;
                var v = Resolve(b, ctx);
                if (v.Missing || v.Invalid) { bad = true; return ""; }
                var last = Split(b).filters.LastOrDefault().f;
                var s = v.ToString();
                if (s.Length > CardLimits.MaxConfigValue * 3) { bad = true; return ""; }
                return last is "path" or "url" ? s : Uri.EscapeDataString(s);
            });
        }
        catch (RegexMatchTimeoutException) { return null; }
        return bad ? null : r;
    }

    public static double? Number(JsonElement? e, CardContext ctx)
    {
        if (e is not { } x) return null;
        if (x.ValueKind == JsonValueKind.Number) return x.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        if (x.ValueKind != JsonValueKind.String) return null;
        var s = x.GetString()!.Trim();
        var m = Binding.Match(s);
        if (m.Success && m.Index == 0 && m.Length == s.Length) return Resolve(m.Groups[1].Value, ctx).Num is { } n && double.IsFinite(n) ? n : null;
        return double.TryParse(Render(s, ctx).TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) ? r : null;
    }

    public static List<double> Numbers(JsonElement e, CardContext ctx)
    {
        if (e.ValueKind == JsonValueKind.Array)
            return e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).Where(double.IsFinite).Take(CardLimits.MaxPoints).ToList();
        if (e.ValueKind != JsonValueKind.String) return [];
        var v = Resolve(e.GetString()!.Trim().Trim('{', '}'), ctx);
        return (v.Nums ?? []).Where(double.IsFinite).ToList();
    }
}

// ───────────────────────── 倒计时（本地计算） ─────────────────────────

public static class CardCountdown
{
    private static DateTime? ParseDate(string s)
    {
        string[] fmts = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss"];
        return DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>算出 days / hours / minutes / progress 等字段，给模板用。target 解析不了返回 null。</summary>
    public static JsonElement? Compute(string target, string start, DateTime now)
    {
        if (ParseDate(target) is not { } t) return null;
        var s = ParseDate(start) ?? now.Date;
        var left = t - now;
        var passed = left.TotalSeconds <= 0;
        var total = Math.Max(1, (t - s).TotalSeconds);
        var progress = Math.Clamp((now - s).TotalSeconds / total, 0, 1);
        var d = new Dictionary<string, object>
        {
            ["days"] = passed ? 0 : (int)Math.Ceiling((t.Date - now.Date).TotalDays),
            ["hours"] = passed ? 0 : left.Hours,
            ["minutes"] = passed ? 0 : left.Minutes,
            ["total_hours"] = passed ? 0 : Math.Floor(left.TotalHours),
            ["total_days"] = Math.Round((t.Date - s.Date).TotalDays),
            ["elapsed_days"] = Math.Max(0, Math.Floor((now.Date - s.Date).TotalDays)),
            ["since_days"] = passed ? Math.Floor((now.Date - t.Date).TotalDays) : 0,
            ["progress"] = Math.Round(progress, 4),
            ["passed"] = passed,
            ["target"] = t.ToString(t.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["start"] = s.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["weekday"] = "周" + "日一二三四五六"[(int)t.DayOfWeek],
        };
        return JsonSerializer.SerializeToElement(d);
    }
}

// ───────────────────────── 求值结果 ─────────────────────────

public abstract record CardNode;
public sealed record HeaderNode(string Icon, string Title, string Subtitle, string Status) : CardNode;
public sealed record MetricNode(string Value, string Unit, string Label, string Delta, string Tone) : CardNode;
public sealed record ProgressNode(double Fraction, string Label, string Text, string Tone) : CardNode;
public sealed record SparkNode(IReadOnlyList<double> Points, string Label, string Caption, string Tone) : CardNode;
public sealed record CardBadge(string Text, string Tone);
public sealed record BadgeRowNode(IReadOnlyList<CardBadge> Badges) : CardNode;
public sealed record CardAction(string Label, string Icon, string Kind, string? Url);
public sealed record ActionRowNode(IReadOnlyList<CardAction> Actions) : CardNode;

/// <summary>一个数据源的最近状态（宿主维护）。</summary>
public sealed record SourceState(JsonElement? Data, string? Error, DateTime? UpdatedAt);

/// <summary>求值后的卡片：纯数据，渲染器只管画。</summary>
public sealed record CardView(string Title, string Icon, string Accent, IReadOnlyList<CardNode> Nodes, string? Notice, DateTime? UpdatedAt, bool Pending);

public static class CardEvaluator
{
    /// <summary>不联网的数据：static 原样、countdown 现算。</summary>
    public static Dictionary<string, JsonElement> LocalData(PluginManifest m, IReadOnlyDictionary<string, string> config, DateTime now)
    {
        var d = new Dictionary<string, JsonElement>();
        var ctx = new CardContext { Config = config, Now = now };
        foreach (var s in m.Sources)
        {
            if (s.Type == "static" && s.Data is { } data) d[s.Id] = data;
            else if (s.Type == "countdown" && CardCountdown.Compute(CardTemplate.Render(s.Target, ctx), CardTemplate.Render(s.Start, ctx), now) is { } c) d[s.Id] = c;
        }
        return d;
    }

    /// <summary>某个联网数据源这次该请求的地址：模板求值 → 再按白名单查一遍。返回 null 表示这次不能发（附原因）。</summary>
    public static Uri? SourceUrl(PluginManifest m, CardSource s, IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, JsonElement> data, out string? why)
    {
        why = null;
        var url = CardTemplate.RenderUrl(s.Url, new CardContext { Config = config, Data = data });
        if (url == null) { why = "网址里的参数缺失或不合法"; return null; }
        Uri? uri;
        var ok = s.Type == "local" ? UrlPolicy.CheckLocal(url, m.LocalSources, out uri, out why) : UrlPolicy.CheckHttps(url, m.Domains, out uri, out why);
        return ok ? uri : null;
    }

    public static CardView Evaluate(PluginManifest m, IReadOnlyDictionary<string, string> config, IReadOnlyDictionary<string, SourceState> states, DateTime now)
    {
        var data = LocalData(m, config, now);
        var errors = new List<string>();
        var pending = false;
        var stale = false;
        DateTime? updated = null;
        foreach (var s in m.Sources.Where(s => s.Type is "https" or "local"))
        {
            states.TryGetValue(s.Id, out var st);
            if (st?.Data is { } d) data[s.Id] = d;
            if (st?.UpdatedAt is { } u && (updated == null || u < updated)) updated = u;
            if (st?.Error is { } e) { if (st.Data == null) errors.Add(e); else stale = true; }
            else if (st?.Data == null) pending = true;
        }
        var ctx = new CardContext { Config = config, Data = data, Now = now };
        string? notice = errors.Count > 0 ? (m.ErrorText.Length > 0 ? m.ErrorText : "暂时拿不到数据，稍后自动重试")
            : stale ? "刷新失败，显示的是上次的数据" : pending ? "加载中…" : null;
        var autoStatus = errors.Count > 0 ? "error" : stale ? "warn" : pending ? "idle" : "ok";
        var nodes = new List<CardNode>();
        foreach (var p in m.Card) if (Node(p, ctx, autoStatus, m) is { } n) nodes.Add(n);
        return new CardView(m.Name, m.Icon, m.Accent, nodes, notice, updated, pending && errors.Count == 0);
    }

    private static string T(JsonElement o, string key, CardContext ctx) =>
        o.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? CardTemplate.Render(v.GetString()!, ctx) : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "" : "";

    private static string Tone(JsonElement o, string key, CardContext ctx, string fallback)
    {
        var t = T(o, key, ctx);
        return PluginManifest.Tones.Contains(t) ? t : fallback;
    }

    private static string AutoTone(string delta) =>
        delta.StartsWith('+') ? "green" : delta.StartsWith('-') || delta.StartsWith('−') ? "red" : "neutral";

    /// <summary>单个原语求值（L3 进程插件输出的 card 也走这里，那时 ctx 里没有数据源）。</summary>
    public static CardNode? Node(PrimitiveSpec p, CardContext ctx, string autoStatus, PluginManifest? m)
    {
        var o = p.Props;
        switch (p.Type)
        {
            case "header":
            {
                var status = T(o, "status", ctx);
                if (!PluginManifest.Statuses.Contains(status) || status == "auto") status = autoStatus;
                var icon = T(o, "icon", ctx);
                return new HeaderNode(icon.Length > 0 ? PluginManifest.Clean(icon, 8) : m?.Icon ?? "◆", T(o, "title", ctx), T(o, "subtitle", ctx), status);
            }
            case "metric":
            {
                var delta = T(o, "delta", ctx);
                var tone = Tone(o, "tone", ctx, "auto");
                return new MetricNode(T(o, "value", ctx), T(o, "unit", ctx), T(o, "label", ctx), delta == "—" ? "" : delta, tone == "auto" ? AutoTone(delta) : tone);
            }
            case "progress":
            {
                var v = CardTemplate.Number(o.TryGetProperty("value", out var ve) ? ve : null, ctx);
                var max = CardTemplate.Number(o.TryGetProperty("max", out var me) ? me : null, ctx) ?? (v > 1 ? 100 : 1);
                var frac = v is { } x && max > 0 ? Math.Clamp(x / max, 0, 1) : 0;
                var text = T(o, "text", ctx);
                if (text.Length == 0) text = v == null ? "—" : $"{frac * 100:0}%";
                var tone = Tone(o, "tone", ctx, "auto");
                if (tone == "auto") tone = frac >= 0.9 ? "red" : frac >= 0.7 ? "amber" : "green";
                return new ProgressNode(frac, T(o, "label", ctx), text, tone);
            }
            case "sparkline":
            {
                var pts = o.TryGetProperty("points", out var pe) ? CardTemplate.Numbers(pe, ctx) : [];
                var last = o.TryGetProperty("last", out var le) && le.TryGetInt32(out var n) ? Math.Clamp(n, 2, CardLimits.MaxPoints) : CardLimits.MaxPoints;
                pts = pts.Skip(Math.Max(0, pts.Count - last)).ToList();
                var cap = T(o, "caption", ctx);
                if (cap.Length == 0 && pts.Count > 0) cap = CardTemplate.Compact(pts[^1]);
                var tone = Tone(o, "tone", ctx, "blue");
                if (tone == "auto") tone = pts.Count > 1 && pts[^1] < pts[0] ? "red" : "green";
                return new SparkNode(pts, T(o, "label", ctx), cap, tone);
            }
            case "badge_row":
            {
                var list = new List<CardBadge>();
                if (o.TryGetProperty("badges", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var b in bs.EnumerateArray().Take(CardLimits.MaxBadges))
                    {
                        var text = T(b, "text", ctx);
                        if (text.Length == 0) continue;
                        var tone = Tone(b, "tone", ctx, "neutral");
                        if (tone == "auto") tone = AutoTone(text);
                        list.Add(new CardBadge(PluginManifest.Clean(text, 40), tone));
                    }
                return list.Count == 0 ? null : new BadgeRowNode(list);
            }
            case "action_row":
            {
                var list = new List<CardAction>();
                if (o.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array)
                    foreach (var a in acts.EnumerateArray().Take(CardLimits.MaxActions))
                    {
                        var kind = PluginManifest.Str(a, "kind");
                        string? url = null;
                        if (kind == "open_url")
                        {
                            var raw = CardTemplate.RenderUrl(PluginManifest.Str(a, "url"), ctx);
                            url = raw != null && UrlPolicy.CheckOpenTemplate(raw, out _) && UrlPolicy.CheckOpen(raw, out var u) ? u!.AbsoluteUri : null;
                        }
                        else if (kind != "refresh") continue;
                        list.Add(new CardAction(PluginManifest.Clean(T(a, "label", ctx), 16), PluginManifest.Clean(T(a, "icon", ctx), 4), kind, url));
                    }
                return list.Count == 0 ? null : new ActionRowNode(list);
            }
        }
        return null;
    }
}

// ───────────────────────── L3 进程插件输出 ─────────────────────────

/// <summary>
/// 高级插件（L3）进程的 stdout：
/// · 新格式 {"card":[原语...]}：和 L2 同一套原语，只能写字面量（没有数据源），按钮同样只允许 https / 刷新；
/// · 旧 WinIslands 格式 {"components":[{text, icon, progress}]} / 数组 / 每行一个对象：转成徽章行和进度条。
/// </summary>
public static class ProcessOutput
{
    public static CardView? Parse(PluginManifest m, string? stdout, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var text = stdout.Trim();
        var max = m.MaxOutputKb * 1024;
        if (text.Length > max) return null;
        var opts = new JsonDocumentOptions { MaxDepth = CardLimits.MaxJsonDepth, AllowTrailingCommas = true };
        var comps = new List<JsonElement>();
        try
        {
            using var doc = JsonDocument.Parse(text, opts);
            var r = doc.RootElement;
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Array)
            {
                var nodes = new List<CardNode>();
                var ctx = new CardContext { Now = now };
                foreach (var p in card.EnumerateArray().Take(CardLimits.MaxPrimitives))
                {
                    if (PluginManifest.ValidatePrimitive(p, [], []).Count > 0) continue;
                    if (CardEvaluator.Node(new PrimitiveSpec(PluginManifest.Str(p, "type"), p.Clone()), ctx, "ok", m) is { } n) nodes.Add(n);
                }
                return nodes.Count == 0 ? null : new CardView(m.Name, m.Icon, m.Accent, nodes, null, now, false);
            }
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("components", out var cs) && cs.ValueKind == JsonValueKind.Array) comps.AddRange(cs.EnumerateArray().Take(12).Select(x => x.Clone()));
            else if (r.ValueKind == JsonValueKind.Array) comps.AddRange(r.EnumerateArray().Take(12).Select(x => x.Clone()));
            else if (r.ValueKind == JsonValueKind.Object) comps.Add(r.Clone());
        }
        catch (JsonException)
        {
            foreach (var line in text.Split('\n').Take(12))
            {
                try { using var d = JsonDocument.Parse(line.Trim(), opts); if (d.RootElement.ValueKind == JsonValueKind.Object) comps.Add(d.RootElement.Clone()); }
                catch (JsonException) { }
            }
        }
        var badges = new List<CardBadge>();
        var list = new List<CardNode> { new HeaderNode(m.Icon, m.Name, "", "ok") };
        foreach (var c in comps)
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            var t = PluginManifest.Clean(PluginManifest.Str(c, "text"), 60);
            if (t.Length == 0) continue;
            var icon = PluginManifest.Clean(PluginManifest.Str(c, "icon"), 4);
            if (icon.Length > 0 && icon[0] >= '\uE000' && icon[0] <= '\uF8FF') icon = "";   // 旧版 Segoe 私有区字形在卡片里不画
            if (c.TryGetProperty("progress", out var pv) && pv.TryGetDouble(out var p) && double.IsFinite(p))
            {
                var f = p > 1 ? p / 100 : p;
                list.Add(new ProgressNode(Math.Clamp(f, 0, 1), t, $"{Math.Clamp(f, 0, 1) * 100:0}%", f >= 0.9 ? "red" : f >= 0.7 ? "amber" : "green"));
            }
            else if (badges.Count < CardLimits.MaxBadges) badges.Add(new CardBadge((icon.Length > 0 ? icon + " " : "") + t, "neutral"));
        }
        if (badges.Count > 0) list.Add(new BadgeRowNode(badges));
        return list.Count <= 1 ? null : new CardView(m.Name, m.Icon, m.Accent, list, null, now, false);
    }
}
