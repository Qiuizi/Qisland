using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qisland.Core;

// 5.0 插件市场：索引（registry.json）、撤销名单（revoked.json）、Ed25519 验签、下载校验与安装计划。
// 只有纯解析和判断，不联网、不碰文件；联网和落盘在 App 的 MarketClient / PluginHost 里。
//
// 哈希与签名的约定（社区仓库 README 里写的是同一套）：
// · JSON 文件（registry.json / revoked.json / plugin.json / theme.json）：先去掉 UTF-8 BOM 和所有 \r 再算。
//   JSON 字符串里不允许出现裸的 \r，去掉它不改变语义；这样 Windows 上 CRLF 检出、GitHub raw 返回 LF 都是同一个值。
// · zip 包：原始字节。
// · 签名文件 *.sig：一行 Base64（64 字节 Ed25519 签名，可带 "ed25519:" 前缀），签的是上面规整后的 JSON 字节。

public enum MarketKind { Plugin, Theme }

/// <summary>索引里一项声明的权限：给用户安装前看，也用来核对下载到的包有没有偷偷多要。</summary>
public sealed record MarketPermissions(IReadOnlyList<string> Domains, IReadOnlyList<string> Local, bool RunsProgram)
{
    public static readonly MarketPermissions None = new([], [], false);
    public bool Offline => Domains.Count == 0 && Local.Count == 0 && !RunsProgram;
}

public sealed record MarketItem(
    string Id, string Name, string Version, PluginTier Tier, string Category, string Author, string Description,
    string Icon, string DownloadUrl, string Sha256, MarketPermissions Permissions, string MinAppVersion, string Homepage)
{
    public MarketKind Kind => Category == "theme" ? MarketKind.Theme : MarketKind.Plugin;
    /// <summary>搜索用：id、名字、作者、描述，忽略大小写。</summary>
    public bool Matches(string? q) => string.IsNullOrWhiteSpace(q) || q.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w =>
        Id.Contains(w, StringComparison.OrdinalIgnoreCase) || Name.Contains(w, StringComparison.OrdinalIgnoreCase)
        || Author.Contains(w, StringComparison.OrdinalIgnoreCase) || Description.Contains(w, StringComparison.OrdinalIgnoreCase)
        || MarketCategories.Name(Category).Contains(w, StringComparison.OrdinalIgnoreCase));
}

public static class MarketCategories
{
    /// <summary>市场页的筛选顺序；「全部」不在这里。</summary>
    public static readonly string[] All = ["productivity", "dev", "info", "theme"];
    public static string Name(string c) => c switch { "productivity" => "效率", "dev" => "开发", "info" => "信息", "theme" => "主题包", _ => "其他" };
}

/// <summary>registry.json：schema_version、updated_at、items[]。坏掉的单项跳过并记一条警告，不让整份索引作废。</summary>
public sealed class MarketRegistry
{
    public const int SchemaVersion = 1;
    public const int MaxBytes = 1024 * 1024;
    public const int MaxItems = 500;
    public const long MaxPackageBytes = 32L * 1024 * 1024;

    public int Schema { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public List<MarketItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];
    public string? Error { get; private set; }
    public bool IsValid => Error == null;

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly JsonDocumentOptions Doc = new() { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false };

    public static MarketRegistry Parse(string? json)
    {
        var r = new MarketRegistry();
        if (string.IsNullOrWhiteSpace(json)) { r.Error = "索引是空的"; return r; }
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) { r.Error = "索引超过 1 MB"; return r; }
        try
        {
            using var doc = JsonDocument.Parse(json, Doc);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { r.Error = "索引根节点要是对象"; return r; }
            r.Schema = root.TryGetProperty("schema_version", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var n) ? n : 0;
            if (r.Schema != SchemaVersion) { r.Error = $"不认识的 schema_version {r.Schema}（这一版只认 {SchemaVersion}）"; return r; }
            if (!TryDate(root, "updated_at", out var at)) { r.Error = "缺少合法的 updated_at（ISO 8601）"; return r; }
            r.UpdatedAt = at;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) { r.Error = "缺少 items 数组"; return r; }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var e in items.EnumerateArray())
            {
                if (++i > MaxItems) { r.Warnings.Add($"索引超过 {MaxItems} 项，后面的忽略"); break; }
                var item = ParseItem(e, out var why);
                if (item == null) { r.Warnings.Add($"第 {i} 项跳过：{why}"); continue; }
                if (!seen.Add(item.Id)) { r.Warnings.Add($"{item.Id} 重复，只保留第一项"); continue; }
                r.Items.Add(item);
            }
        }
        catch (JsonException ex) { r.Error = "索引不是合法 JSON：" + PluginManifest.Short(ex.Message, 80); }
        return r;
    }

    internal static bool TryDate(JsonElement o, string key, out DateTimeOffset at)
    {
        at = default;
        return o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out at);
    }

    public static MarketItem? ParseItem(JsonElement e, out string why)
    {
        why = "";
        if (e.ValueKind != JsonValueKind.Object) { why = "不是对象"; return null; }
        var id = PluginManifest.Str(e, "id");
        if (!PluginManifest.IdRx.IsMatch(id)) { why = "id 不合法"; return null; }
        var name = PluginManifest.Clean(PluginManifest.Str(e, "name"), 40);
        if (name.Length == 0) { why = id + " 缺少 name"; return null; }
        var version = PluginManifest.Str(e, "version").Trim();
        if (!SemVer.IsValid(version)) { why = id + " 的 version 要像 1.2.0"; return null; }
        var tierText = PluginManifest.Str(e, "tier");
        PluginTier tier;
        if (tierText == "declarative") tier = PluginTier.Declarative;
        else if (tierText == "process") tier = PluginTier.Process;
        else { why = id + " 的 tier 只能是 declarative 或 process"; return null; }
        var category = PluginManifest.Str(e, "category").Trim().ToLowerInvariant();
        if (!MarketCategories.All.Contains(category)) { why = $"{id} 的 category 只能是 {string.Join(" / ", MarketCategories.All)}"; return null; }
        if (category == "theme" && tier != PluginTier.Declarative) { why = id + "：主题包只能是 declarative"; return null; }
        var url = PluginManifest.Str(e, "download_url").Trim();
        if (!MarketSources.IsSafeHttps(url, out var uw)) { why = $"{id} 的 download_url {uw}"; return null; }
        var sha = PluginManifest.Str(e, "sha256").Trim().ToLowerInvariant();
        if (!Hex64.IsMatch(sha)) { why = id + " 的 sha256 要是 64 位十六进制"; return null; }
        var min = PluginManifest.Str(e, "min_app_version").Trim();
        if (min.Length > 0 && !SemVer.IsValid(min)) { why = id + " 的 min_app_version 不合法"; return null; }
        var home = PluginManifest.Str(e, "homepage").Trim();
        if (home.Length > 0 && !MarketSources.IsSafeHttps(home, out _)) home = "";
        var perms = ParsePermissions(e, tier, out var pw);
        if (perms == null) { why = $"{id} 的 permissions {pw}"; return null; }
        var icon = PluginManifest.Clean(PluginManifest.Str(e, "icon"), 4);
        return new MarketItem(id, name, version, tier, category, PluginManifest.Clean(PluginManifest.Str(e, "author"), 40),
            PluginManifest.Clean(PluginManifest.Str(e, "description"), 160), icon.Length > 0 ? icon : category == "theme" ? "◐" : "◆",
            url, sha, perms, min, home);
    }

    private static MarketPermissions? ParsePermissions(JsonElement e, PluginTier tier, out string why)
    {
        why = "";
        var domains = new List<string>(); var local = new List<string>();
        if (e.TryGetProperty("permissions", out var p))
        {
            if (p.ValueKind != JsonValueKind.Object) { why = "要是对象 { \"domains\": [...], \"local\": [...] }"; return null; }
            if (p.TryGetProperty("domains", out var d) && d.ValueKind == JsonValueKind.Array)
                foreach (var x in d.EnumerateArray())
                {
                    var h = (x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Trim().TrimEnd('.').ToLowerInvariant();
                    if (!UrlPolicy.IsValidDomain(h, out var w)) { why = $"里的域名 {PluginManifest.Short(h, 40)} 不允许：{w}"; return null; }
                    if (!domains.Contains(h)) domains.Add(h);
                }
            if (p.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.Array)
                foreach (var x in l.EnumerateArray())
                {
                    var hp = (x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Trim().ToLowerInvariant();
                    if (!UrlPolicy.IsValidLocalHostPort(hp)) { why = $"里的本机数据源 {PluginManifest.Short(hp, 40)} 不合法"; return null; }
                    if (!local.Contains(hp)) local.Add(hp);
                }
        }
        if (tier == PluginTier.Declarative && (domains.Count > CardLimits.MaxDomains || local.Count > CardLimits.MaxDomains)) { why = $"超过 L2 上限（最多 {CardLimits.MaxDomains} 个域名）"; return null; }
        return new MarketPermissions(domains, local, tier == PluginTier.Process);
    }
}

public sealed record RevokedEntry(string Id, string Sha256, IReadOnlyList<string> Versions, string Reason);

/// <summary>
/// revoked.json：{ schema_version, updated_at, items: [ { id?, sha256?, versions?, reason } ] }。
/// 写了 sha256 就按包的哈希精确命中（不管 id）；只写 id 时命中这个 id 的所有版本，写了 versions 就只命中列出的版本。
/// </summary>
public sealed class RevocationList
{
    public const int MaxItems = 2000;
    public int Schema { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public List<RevokedEntry> Items { get; } = [];
    public string? Error { get; private set; }
    public bool IsValid => Error == null;

    public static readonly RevocationList Empty = new() { Schema = 1 };

    public static RevocationList Parse(string? json)
    {
        var r = new RevocationList();
        if (string.IsNullOrWhiteSpace(json)) { r.Error = "撤销名单是空的"; return r; }
        if (Encoding.UTF8.GetByteCount(json) > MarketRegistry.MaxBytes) { r.Error = "撤销名单超过 1 MB"; return r; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { r.Error = "根节点要是对象"; return r; }
            r.Schema = root.TryGetProperty("schema_version", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var n) ? n : 0;
            if (r.Schema != MarketRegistry.SchemaVersion) { r.Error = $"不认识的 schema_version {r.Schema}"; return r; }
            if (!MarketRegistry.TryDate(root, "updated_at", out var at)) { r.Error = "缺少合法的 updated_at"; return r; }
            r.UpdatedAt = at;
            if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) { r.Error = "缺少 items 数组"; return r; }
            foreach (var e in items.EnumerateArray().Take(MaxItems))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var id = PluginManifest.Str(e, "id").Trim();
                var sha = PluginManifest.Str(e, "sha256").Trim().ToLowerInvariant();
                if (id.Length > 0 && !PluginManifest.IdRx.IsMatch(id)) continue;
                if (sha.Length > 0 && !Regex.IsMatch(sha, "^[0-9a-f]{64}$")) continue;
                if (id.Length == 0 && sha.Length == 0) continue;
                var versions = e.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array
                    ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).Take(50).ToList()
                    : [];
                var reason = PluginManifest.Clean(PluginManifest.Str(e, "reason"), 120);
                r.Items.Add(new RevokedEntry(id, sha, versions, reason.Length > 0 ? reason : "已被维护者撤销"));
            }
        }
        catch (JsonException ex) { r.Error = "撤销名单不是合法 JSON：" + PluginManifest.Short(ex.Message, 80); }
        return r;
    }

    /// <summary>命中就返回撤销原因，否则 null。sha256 为空表示不知道包的哈希（只按 id / 版本比）。</summary>
    public string? Match(string id, string? version, string? sha256)
    {
        foreach (var e in Items)
        {
            if (e.Sha256.Length > 0)
            {
                if (!string.IsNullOrEmpty(sha256) && string.Equals(e.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) return e.Reason;
                continue;
            }
            if (!string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Versions.Count == 0) return e.Reason;
            if (version != null && e.Versions.Any(v => SemVer.Compare(v, version) == 0)) return e.Reason;
        }
        return null;
    }
}

/// <summary>验签与哈希。官方公钥编进程序里；换钥时往 OfficialKeys 里加新钥，旧钥留一个版本再删。</summary>
public static class MarketTrust
{
    /// <summary>官方索引签名公钥（Ed25519，32 字节十六进制）。私钥不在仓库里。</summary>
    public static readonly IReadOnlyList<byte[]> OfficialKeys =
    [
        Convert.FromHexString("1126a0023f533493f8120ec9be40e5831d387c63b520f6359fe4cb1b2f08933d"),
    ];

    /// <summary>JSON 文件的规整字节：去 UTF-8 BOM、去掉所有 \r。</summary>
    public static byte[] Canonical(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bytes = bytes[3..];
        var r = new byte[bytes.Length];
        var n = 0;
        foreach (var b in bytes) if (b != 0x0D) r[n++] = b;
        Array.Resize(ref r, n);
        return r;
    }

    public static bool IsZip(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;

    /// <summary>包的 SHA-256（小写十六进制）：zip 按原始字节，其余按 JSON 规整字节。</summary>
    public static string PackageSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(IsZip(bytes) ? bytes : Canonical(bytes))).ToLowerInvariant();

    public static byte[]? ParseSignature(string? sig)
    {
        if (string.IsNullOrWhiteSpace(sig)) return null;
        var s = sig.Trim();
        if (s.StartsWith("ed25519:", StringComparison.OrdinalIgnoreCase)) s = s[8..].Trim();
        if (s.Length > 200) return null;
        try { var b = Convert.FromBase64String(s); return b.Length == Ed25519.SignatureSize ? b : null; }
        catch (FormatException) { return null; }
    }

    /// <summary>用任一把信任的公钥验过就算通过。content 是下载到的原始字节（内部先规整）。</summary>
    public static bool Verify(ReadOnlySpan<byte> content, string? sigText, IEnumerable<byte[]>? keys = null)
    {
        if (ParseSignature(sigText) is not { } sig) return false;
        var msg = Canonical(content);
        foreach (var k in keys ?? OfficialKeys) if (Ed25519.Verify(k, msg, sig)) return true;
        return false;
    }

    /// <summary>维护者签名用（自检也用它造反例）。</summary>
    public static string Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> content) => Convert.ToBase64String(Ed25519.Sign(seed, Canonical(content)));
}

public sealed record MarketSource(string Name, string Url, bool Official);

public static class MarketSources
{
    public const string OfficialRepo = "Qiuizi/qisland-plugins";
    public const string OfficialRegistry = "https://raw.githubusercontent.com/" + OfficialRepo + "/main/registry.json";
    public const string OfficialRegistryMirror = "https://cdn.jsdelivr.net/gh/" + OfficialRepo + "@main/registry.json";
    public static readonly MarketSource Official = new("官方索引", OfficialRegistry, true);
    public const int MaxCustomSources = 8;

    /// <summary>只允许 https、公网域名（不许 IP、localhost、内网名）、默认端口、不带账号密码。</summary>
    public static bool IsSafeHttps(string? url, out string why)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(url) || url.Length > 400) { why = "为空或太长"; return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) { why = "不是完整网址"; return false; }
        if (u.Scheme != Uri.UriSchemeHttps) { why = "只允许 https://"; return false; }
        if (!u.IsDefaultPort) { why = "只允许默认 443 端口"; return false; }
        if (u.UserInfo.Length > 0) { why = "不能带账号密码"; return false; }
        if (!UrlPolicy.IsValidDomain(u.Host, out var w)) { why = "域名不允许：" + w; return false; }
        return true;
    }

    /// <summary>自定义索引源地址：https + 以 .json 结尾。</summary>
    public static bool IsValidSourceUrl(string? url, out string why)
    {
        if (!IsSafeHttps(url, out why)) return false;
        if (!new Uri(url!).AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) { why = "要指向一个 .json 索引文件"; return false; }
        return true;
    }

    /// <summary>同目录下的兄弟文件：…/registry.json → …/revoked.json、…/registry.json.sig。</summary>
    public static string Sibling(string registryUrl, string fileName)
    {
        var u = new Uri(registryUrl);
        return new Uri(u, fileName).AbsoluteUri;
    }

    /// <summary>raw.githubusercontent.com/{owner}/{repo}/{ref}/{path} → cdn.jsdelivr.net/gh/{owner}/{repo}@{ref}/{path}；不是 GitHub raw 返回 null。</summary>
    public static string? Mirror(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;
        var seg = u.AbsolutePath.Trim('/').Split('/', 4);
        if (seg.Length < 4 || seg.Any(s => s.Length == 0)) return null;
        return $"https://cdn.jsdelivr.net/gh/{seg[0]}/{seg[1]}@{seg[2]}/{seg[3]}";
    }
}

public enum MarketState { Install, Update, Installed, Incompatible, Revoked }

/// <summary>安装前的所有判断：版本门槛、撤销、下载包校验、包内容和索引声明是否一致。</summary>
public static class MarketGate
{
    /// <summary>
    /// min_app_version 门槛：满足返回 null，否则返回给用户看的原因。
    /// 预览版按它的正式版号算（5.0.0-preview.2 满足 min 5.0.0），这样预览测试的人也能装为正式版准备的插件。
    /// </summary>
    public static string? CheckAppVersion(MarketItem item, string appVersion)
    {
        if (item.MinAppVersion.Length == 0) return null;
        var core = appVersion.Split('+')[0].Split('-')[0];
        return SemVer.Compare(core, item.MinAppVersion) >= 0 ? null : $"需要 Qisland {item.MinAppVersion} 或更新（当前 {appVersion}）";
    }

    public static MarketState Plan(MarketItem item, string? installedVersion, string appVersion, RevocationList? revoked)
    {
        if (revoked?.Match(item.Id, item.Version, item.Sha256) != null) return MarketState.Revoked;
        if (installedVersion != null && SemVer.Compare(installedVersion, item.Version) >= 0) return MarketState.Installed;
        if (CheckAppVersion(item, appVersion) != null) return installedVersion != null ? MarketState.Installed : MarketState.Incompatible;
        return installedVersion == null ? MarketState.Install : MarketState.Update;
    }

    /// <summary>下载到的字节：大小、SHA-256。通过返回 null。</summary>
    public static string? CheckPackage(MarketItem item, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "下载到的是空文件";
        if (bytes.Length > MarketRegistry.MaxPackageBytes) return "包超过 32 MB";
        var h = MarketTrust.PackageSha256(bytes);
        if (!string.Equals(h, item.Sha256, StringComparison.OrdinalIgnoreCase)) return $"SHA-256 对不上（索引 {item.Sha256[..12]}…，下载到 {h[..12]}…），已丢弃";
        if (item.Kind == MarketKind.Theme && MarketTrust.IsZip(bytes)) return "主题包应该是一个 theme.json，不是 zip";
        if (item.Tier == PluginTier.Process && !MarketTrust.IsZip(bytes)) return "高级插件要用 zip 包发布";
        return null;
    }

    /// <summary>
    /// 包里的 plugin.json 和索引的声明要对得上：id、分级、版本一致；包里要的域名 / 本机数据源不能超出索引里给用户看过的。
    /// 返回所有不一致之处；空 = 一致。
    /// </summary>
    public static List<string> ManifestMismatch(MarketItem item, PluginManifest m)
    {
        var r = new List<string>();
        if (!m.IsValid) { r.Add("plugin.json 不合格：" + string.Join("；", m.Errors.Take(3))); return r; }
        if (!string.Equals(m.Id, item.Id, StringComparison.OrdinalIgnoreCase)) r.Add($"包里的 id 是 {m.Id}，索引写的是 {item.Id}");
        if (m.Tier != item.Tier) r.Add(m.Tier == PluginTier.Process ? "包里是会运行程序的高级插件（L3），索引标的是安全卡片（L2）" : "包里的分级和索引不一致");
        if (SemVer.Compare(m.Version, item.Version) != 0) r.Add($"包里的版本是 {m.Version}，索引写的是 {item.Version}");
        foreach (var d in m.Domains) if (!item.Permissions.Domains.Contains(d, StringComparer.OrdinalIgnoreCase)) r.Add("包里多要了域名 " + d);
        foreach (var l in m.LocalSources) if (!item.Permissions.Local.Contains(l, StringComparer.OrdinalIgnoreCase)) r.Add("包里多要了本机数据源 " + l);
        return r;
    }

    /// <summary>防回放：新拿到的索引 / 撤销名单不能比上次验过的旧（旧索引可能缺少后来的撤销项）。</summary>
    public static bool IsRollback(DateTimeOffset fresh, DateTimeOffset? lastSeen) => lastSeen is { } last && fresh < last;
}

/// <summary>市场里的一项：来自哪个源、验没验签。</summary>
public sealed record MarketListing(MarketItem Item, MarketSource Source, bool Verified, bool Builtin);

public static class MarketCatalog
{
    /// <summary>
    /// 合并多个源：同一个 id 优先取验过签的（官方 / 内置），其次取版本高的；第三方源不能冒用验过签的 id（防止仿冒官方插件）。
    /// </summary>
    public static List<MarketListing> Merge(IEnumerable<MarketListing> all)
    {
        var best = new Dictionary<string, MarketListing>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in all)
        {
            if (!best.TryGetValue(l.Item.Id, out var cur)) { best[l.Item.Id] = l; continue; }
            if (cur.Verified && !l.Verified) continue;
            if (!cur.Verified && l.Verified) { best[l.Item.Id] = l; continue; }
            if (SemVer.Compare(l.Item.Version, cur.Item.Version) > 0) best[l.Item.Id] = l;
        }
        return best.Values
            .OrderBy(l => Array.IndexOf(MarketCategories.All, l.Item.Category) is var i && i < 0 ? 99 : i)
            .ThenBy(l => l.Verified ? 0 : 1)
            .ThenBy(l => l.Item.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>category 为 null / "all" 表示全部。</summary>
    public static List<MarketListing> Filter(IEnumerable<MarketListing> list, string? category, string? query) =>
        list.Where(l => (category is null or "all" || l.Item.Category == category) && l.Item.Matches(query)).ToList();
}
