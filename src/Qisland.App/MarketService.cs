using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 插件市场：内置离线目录 + 官方在线索引（Ed25519 验签）+ 用户自己加的第三方源（明确标「未验签」）。
/// · 没网、官方仓库还没上线时，市场照样能用内置目录（6 个官方插件 + 3 套官方主题），一键安装 / 应用 / 卸载。
/// · 在线刷新：先 raw.githubusercontent.com，不通再走 jsDelivr 镜像；签名对不上、比上次的旧（防回放）一律丢弃，继续用上次验过的缓存。
/// · 撤销名单：内置名单 + 验过签的官方名单对所有插件生效；第三方源自己的名单只管从这个源装的插件。
/// · 下载：只走 https、≤ 32 MB、逐字节核 SHA-256；包的内容还要和索引声明的 id / 分级 / 版本 / 权限一致（PluginHost 里查）。
/// </summary>
internal sealed class MarketService
{
    public static readonly MarketSource BuiltinSource = new("内置离线目录", "builtin:", true);
    private static string CacheDir => Path.Combine(App.DataDir, "market");
    private static string SourcesFile => Path.Combine(App.DataDir, "market-sources.json");
    private static string StateFile => Path.Combine(CacheDir, "state.json");

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 3,
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = System.Net.DecompressionMethods.All,
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class CacheState
    {
        public DateTimeOffset? OfficialRegistryAt { get; set; }
        public DateTimeOffset? OfficialRevokedAt { get; set; }
        public DateTimeOffset? LastRefresh { get; set; }
    }

    /// <summary>一个源当前的内容（内置 / 缓存 / 刚拉到的）。</summary>
    public sealed record SourceStatus(MarketSource Source, MarketRegistry? Registry, RevocationList? Revoked, bool Verified, DateTimeOffset? FetchedAt, string? Error);

    private readonly PluginHost? _host;
    private readonly Action<string?>? _applyTheme;
    private CacheState _cache = new();
    private readonly RevocationList _builtinRevoked;
    private readonly MarketRegistry _builtin;
    private readonly Dictionary<string, SourceStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private List<MarketListing> _listings = [];

    public bool Refreshing { get; private set; }
    public IReadOnlyList<MarketListing> Listings => _listings;
    public IReadOnlyList<MarketSource> CustomSources { get; private set; } = [];
    public SourceStatus? Status(MarketSource s) => _status.TryGetValue(s.Url, out var st) ? st : null;
    public DateTimeOffset? LastRefresh => _cache.LastRefresh;
    public event Action? Changed;
    /// <summary>正在用的主题包命中撤销名单、被换回默认时触发（App 在岛上提示）。</summary>
    public event Action<string, string>? ThemeRevoked;

    public MarketService(PluginHost? host, Action<string?>? applyTheme)
    {
        _host = host;
        _applyTheme = applyTheme;
        _builtin = MarketRegistry.Parse(Resource("market.builtin.registry.json"));
        _builtinRevoked = RevocationList.Parse(Resource("market.builtin.revoked.json")) is { IsValid: true } r ? r : RevocationList.Empty;
        if (!_builtin.IsValid) App.Log("市场: 内置目录 " + _builtin.Error);
        if (!DiyFiles.Snapshot) LoadCache();
        Rebuild();
    }

    private static string? Resource(string name)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
        catch { return null; }
    }

    // ───────────── 源与缓存 ─────────────

    private static string Key(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToLowerInvariant())))[..12].ToLowerInvariant();
    private static string CachePath(MarketSource s, string file) => Path.Combine(CacheDir, Key(s.Url), file);

    private void LoadCache()
    {
        try { if (DiyFiles.Read(StateFile, 8192) is { } t) _cache = JsonSerializer.Deserialize<CacheState>(t, Json) ?? new(); } catch { _cache = new(); }
        try
        {
            if (DiyFiles.Read(SourcesFile, 16 * 1024) is { } t && JsonSerializer.Deserialize<List<MarketSource>>(t, Json) is { } list)
                CustomSources = list.Where(x => MarketSources.IsValidSourceUrl(x.Url, out _) && !string.Equals(x.Url, MarketSources.OfficialRegistry, StringComparison.OrdinalIgnoreCase))
                    .Select(x => x with { Official = false, Name = PluginManifest.Clean(x.Name, 30) is { Length: > 0 } n ? n : new Uri(x.Url).Host })
                    .Take(MarketSources.MaxCustomSources).ToList();
        }
        catch (Exception ex) { App.Log("市场: 读索引源 " + ex.Message); }
        foreach (var s in AllSources()) LoadCached(s);
    }

    private IEnumerable<MarketSource> AllSources() => new[] { MarketSources.Official }.Concat(CustomSources);

    /// <summary>读本地缓存：每次都重新验签（缓存文件被改过也会被发现）。</summary>
    private void LoadCached(MarketSource s)
    {
        try
        {
            var reg = ReadBytes(CachePath(s, "registry.json"));
            if (reg == null) return;
            var regOk = MarketTrust.Verify(reg, ReadText(CachePath(s, "registry.json.sig")));
            if (s.Official && !regOk) { App.Log("市场: 官方缓存验签失败，忽略"); return; }
            var rev = ReadBytes(CachePath(s, "revoked.json"));
            RevocationList? revoked = null;
            if (rev != null && (!s.Official || MarketTrust.Verify(rev, ReadText(CachePath(s, "revoked.json.sig")))))
                revoked = RevocationList.Parse(Encoding.UTF8.GetString(rev)) is { IsValid: true } rl ? rl : null;
            var parsed = MarketRegistry.Parse(Encoding.UTF8.GetString(reg));
            if (!parsed.IsValid) return;
            _status[s.Url] = new SourceStatus(s, parsed, revoked, regOk, File.GetLastWriteTime(CachePath(s, "registry.json")), null);
        }
        catch (Exception ex) { App.Log("市场: 读缓存 " + ex.Message); }
    }

    private static byte[]? ReadBytes(string path) { try { return File.Exists(path) && new FileInfo(path).Length <= MarketRegistry.MaxBytes ? File.ReadAllBytes(path) : null; } catch { return null; } }
    private static string? ReadText(string path) { try { return File.Exists(path) && new FileInfo(path).Length < 4096 ? File.ReadAllText(path) : null; } catch { return null; } }

    private void SaveCache(MarketSource s, string file, byte[]? bytes)
    {
        try
        {
            var p = CachePath(s, file);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (bytes == null) { if (File.Exists(p)) File.Delete(p); return; }
            var tmp = p + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, p, true);
        }
        catch (Exception ex) { App.Log("市场: 写缓存 " + ex.Message); }
    }

    public string? AddSource(string url, string? name)
    {
        url = url.Trim();
        if (!MarketSources.IsValidSourceUrl(url, out var why)) return "地址不行：" + why;
        if (string.Equals(url, MarketSources.OfficialRegistry, StringComparison.OrdinalIgnoreCase) || CustomSources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase))) return "已经在列表里了";
        if (CustomSources.Count >= MarketSources.MaxCustomSources) return $"最多 {MarketSources.MaxCustomSources} 个第三方源";
        var n = PluginManifest.Clean(name, 30);
        CustomSources = [.. CustomSources, new MarketSource(n.Length > 0 ? n : new Uri(url).Host, url, false)];
        SaveSources();
        return null;
    }

    public void RemoveSource(MarketSource s)
    {
        CustomSources = CustomSources.Where(x => !string.Equals(x.Url, s.Url, StringComparison.OrdinalIgnoreCase)).ToList();
        _status.Remove(s.Url);
        try { var d = Path.Combine(CacheDir, Key(s.Url)); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        SaveSources();
        Rebuild();
        Changed?.Invoke();
    }

    private void SaveSources() => DiyFiles.Write(SourcesFile, JsonSerializer.Serialize(CustomSources, Json));

    // ───────────── 合并与状态 ─────────────

    private void Rebuild()
    {
        var all = new List<MarketListing>();
        if (_builtin.IsValid) all.AddRange(_builtin.Items.Select(i => new MarketListing(i, BuiltinSource, true, true)));
        foreach (var st in _status.Values)
            if (st.Registry is { IsValid: true } reg)
                all.AddRange(reg.Items.Select(i => new MarketListing(i, st.Source, st.Verified, false)));
        // 内置目录排在前面：同 id 同版本时保留内置（不用下载）；验过签的在线索引版本更高时取在线
        _listings = MarketCatalog.Merge(all);
    }

    /// <summary>某个插件当前命中的撤销原因（给 PluginHost.RevocationCheck 用）。</summary>
    public string? RevocationFor(PluginEntry e)
    {
        var v = e.Manifest.Version; var sha = e.PackageSha256;
        if (_builtinRevoked.Match(e.Id, v, sha) is { } a) return a;
        if (Status(MarketSources.Official)?.Revoked?.Match(e.Id, v, sha) is { } b) return b;
        if (e.Receipt is { } r && !r.Verified && _status.TryGetValue(r.SourceUrl, out var st) && st.Revoked?.Match(e.Id, v, sha) is { } c) return c;
        return null;
    }

    private string? RevocationFor(MarketListing l)
    {
        if (_builtinRevoked.Match(l.Item.Id, l.Item.Version, l.Item.Sha256) is { } a) return a;
        if (Status(MarketSources.Official)?.Revoked?.Match(l.Item.Id, l.Item.Version, l.Item.Sha256) is { } b) return b;
        if (!l.Verified && Status(l.Source)?.Revoked?.Match(l.Item.Id, l.Item.Version, l.Item.Sha256) is { } c) return c;
        return null;
    }

    /// <summary>内置目录跟程序一起编译，天然兼容当前版本（开发版号可能还是旧的），不再卡 min_app_version。</summary>
    private static string AppVersionFor(MarketListing l) => l.Builtin ? "999.0.0" : Updater.Current;

    public sealed record ItemState(MarketState State, string? InstalledVersion, PluginEntry? Plugin, string? ThemeKey, bool Active, string? Note);

    public ItemState StateOf(MarketListing l)
    {
        var item = l.Item;
        var revoked = RevocationFor(l);
        if (item.Kind == MarketKind.Theme)
        {
            var builtin = ThemeStore.BuiltinNames.Contains(item.Id, StringComparer.OrdinalIgnoreCase);
            var key = builtin ? "builtin:" + item.Id : "user:" + item.Id + ".json";
            string? ver = builtin ? ThemeStore.Builtins.FirstOrDefault(b => b.Key == key)?.Pack.Version ?? item.Version : ThemeStore.InstalledVersion(item.Id);
            var active = ThemeStore.ActiveKey == key;
            if (revoked != null) return new ItemState(MarketState.Revoked, ver, null, ver != null ? key : null, active, revoked);
            if (builtin) return new ItemState(MarketState.Installed, ver, null, key, active, "内置");
            var st = MarketGate.Plan(item, ver, AppVersionFor(l), null);
            return new ItemState(st, ver, null, ver != null ? key : null, active, st == MarketState.Incompatible ? MarketGate.CheckAppVersion(item, AppVersionFor(l)) : null);
        }
        var e = _host?.Find(item.Id);
        var installed = e != null && !_host!.IsHidden(e) ? e.Manifest.Version : null;
        if (revoked != null || (e != null && _host!.RevokedReason(e) is { } rr && (revoked = rr) != null))
            return new ItemState(MarketState.Revoked, installed, e, null, false, revoked);
        var state = MarketGate.Plan(item, installed, AppVersionFor(l), null);
        // 内置官方插件被卸载（隐藏）后：市场里显示「安装」，点了直接恢复，不用下载
        return new ItemState(state, installed, e, null, e != null && _host!.IsEnabled(e), state == MarketState.Incompatible ? MarketGate.CheckAppVersion(item, AppVersionFor(l)) : null);
    }

    // ───────────── 在线刷新 ─────────────

    private static async Task<byte[]?> Get(string url, long max, TimeSpan timeout, bool optional = false)
    {
        using var cts = new CancellationTokenSource(timeout);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd($"Qisland/{Updater.Current} (+https://github.com/Qiuizi/Qisland; plugin-market)");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (resp.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("被重定向到了非 https 地址");
        if (optional && resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len && len > max) throw new InvalidDataException($"文件超过 {max / 1024} KB");
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new InvalidDataException($"文件超过 {max / 1024} KB");
        }
        return ms.ToArray();
    }

    /// <summary>依次试主地址和镜像（GitHub raw → jsDelivr）；全部失败抛最后一个错误。</summary>
    private static async Task<(byte[]? Bytes, string Url)> GetAny(IEnumerable<string> urls, long max, TimeSpan timeout, bool optional = false)
    {
        Exception? last = null;
        foreach (var u in urls.Distinct())
        {
            try { return (await Get(u, max, timeout, optional), u); }
            catch (Exception ex) { last = ex; App.Log($"市场: {u} {ex.GetType().Name} {ex.Message}"); }
        }
        throw last ?? new HttpRequestException("没有可用的地址");
    }

    private static IEnumerable<string> Routes(string url) => MarketSources.Mirror(url) is { } m ? [url, m] : [url];

    /// <summary>刷新所有在线源。返回给用户看的一句话。</summary>
    public async Task<string> Refresh()
    {
        if (Refreshing) return "正在刷新…";
        Refreshing = true;
        Changed?.Invoke();
        var notes = new List<string>();
        try
        {
            foreach (var s in AllSources().ToList())
            {
                var r = await RefreshOne(s);
                if (r != null) notes.Add(r);
            }
            _cache.LastRefresh = DateTimeOffset.Now;
            DiyFiles.Write(StateFile, JsonSerializer.Serialize(_cache, Json));
        }
        finally
        {
            Refreshing = false;
            Rebuild();
            _host?.ApplyRevocations();
            CheckThemeRevocation();
            Changed?.Invoke();
        }
        return notes.Count == 0 ? "已刷新，在线目录是最新的" : string.Join("\n", notes);
    }

    private async Task<string?> RefreshOne(MarketSource s)
    {
        try
        {
            var (reg, used) = await GetAny(Routes(s.Url), MarketRegistry.MaxBytes, TimeSpan.FromSeconds(15));
            var sigUrl = MarketSources.Sibling(used, Path.GetFileName(new Uri(used).AbsolutePath) + ".sig");
            var (sig, _) = await GetAny([sigUrl], 4096, TimeSpan.FromSeconds(10), optional: true);
            var sigText = sig == null ? null : Encoding.UTF8.GetString(sig);
            var verified = MarketTrust.Verify(reg!, sigText);
            if (s.Official && !verified) return "官方索引签名校验失败，已丢弃这次下载，继续用上次验过的目录";
            var parsed = MarketRegistry.Parse(Encoding.UTF8.GetString(reg!));
            if (!parsed.IsValid) return $"{s.Name}：{parsed.Error}";
            if (s.Official)
            {
                if (MarketGate.IsRollback(parsed.UpdatedAt, _cache.OfficialRegistryAt)) return "官方索引比上次的旧（可能是被回放的旧文件），已忽略";
                _cache.OfficialRegistryAt = parsed.UpdatedAt;
            }

            // 撤销名单：官方的必须验签且不能比上次旧；第三方的有就用（只管从它装的插件）
            RevocationList? revoked = Status(s)?.Revoked;
            byte[]? rev = null, revSig = null;
            try
            {
                (rev, _) = await GetAny([MarketSources.Sibling(used, "revoked.json")], MarketRegistry.MaxBytes, TimeSpan.FromSeconds(15), optional: true);
                if (rev != null) (revSig, _) = await GetAny([MarketSources.Sibling(used, "revoked.json.sig")], 4096, TimeSpan.FromSeconds(10), optional: true);
            }
            catch (Exception ex) { App.Log("市场: 撤销名单 " + ex.Message); }
            if (rev != null)
            {
                var rl = RevocationList.Parse(Encoding.UTF8.GetString(rev));
                var revOk = MarketTrust.Verify(rev, revSig == null ? null : Encoding.UTF8.GetString(revSig));
                if (s.Official && !revOk) App.Log("市场: 官方撤销名单验签失败，保留上次的");
                else if (s.Official && MarketGate.IsRollback(rl.UpdatedAt, _cache.OfficialRevokedAt)) App.Log("市场: 官方撤销名单比上次旧，忽略");
                else if (rl.IsValid)
                {
                    revoked = rl;
                    if (s.Official) _cache.OfficialRevokedAt = rl.UpdatedAt;
                    SaveCache(s, "revoked.json", rev); SaveCache(s, "revoked.json.sig", revSig);
                }
            }
            SaveCache(s, "registry.json", reg); SaveCache(s, "registry.json.sig", sig);
            _status[s.Url] = new SourceStatus(s, parsed, revoked, verified, DateTimeOffset.Now, null);
            foreach (var w in parsed.Warnings.Take(5)) App.Log($"市场: {s.Name} {w}");
            return null;
        }
        catch (Exception ex)
        {
            var old = Status(s);
            _status[s.Url] = new SourceStatus(s, old?.Registry, old?.Revoked, old?.Verified ?? false, old?.FetchedAt, ex is HttpRequestException or TaskCanceledException or OperationCanceledException ? "连不上" : ex.Message);
            return s.Official ? (old?.Registry != null ? "官方索引暂时连不上，先用上次的目录" : "官方索引暂时连不上（仓库可能还没上线），先用内置离线目录") : $"{s.Name} 连不上";
        }
    }

    // ───────────── 安装 / 卸载 / 应用 ─────────────

    /// <summary>安装或更新。内置目录里的东西不用下载；其余下载 → 核 SHA-256 → 按类型落盘。</summary>
    public async Task<(bool Ok, string Message)> Install(MarketListing l)
    {
        var item = l.Item;
        if (MarketGate.CheckAppVersion(item, AppVersionFor(l)) is { } tooOld) return (false, tooOld);
        if (RevocationFor(l) is { } rv) return (false, "已被撤销：" + rv);
        if (item.Kind == MarketKind.Plugin && _host?.Find(item.Id) is { Bundled: true } bundled && SemVer.Compare(bundled.Manifest.Version, item.Version) >= 0)
        {
            _host.Restore(item.Id);
            return (true, $"已安装 {bundled.Manifest.Name}（内置版本，默认关闭）");
        }
        if (item.Kind == MarketKind.Theme && ThemeStore.BuiltinNames.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) return (true, "内置主题，直接应用就行");
        if (l.Builtin) return (false, "内置目录里没有这一项的内容，刷新在线目录后再试");
        byte[] bytes;
        try { (bytes, _) = await GetAny(Routes(item.DownloadUrl), MarketRegistry.MaxPackageBytes, TimeSpan.FromSeconds(60)) is { Bytes: { } b } ? (b, "") : throw new InvalidDataException("下载到的是空文件"); }
        catch (Exception ex) { return (false, "下载失败：" + (ex is HttpRequestException or TaskCanceledException ? "网络不通或文件不存在" : ex.Message)); }
        if (MarketGate.CheckPackage(item, bytes) is { } bad) return (false, bad);
        if (item.Kind == MarketKind.Theme)
        {
            var err = ThemeStore.InstallFromMarket(item.Id, bytes);
            Changed?.Invoke();
            return err == null ? (true, $"已安装主题「{item.Name}」") : (false, err);
        }
        if (_host == null) return (false, "插件还没启动");
        var r = _host.InstallFromMarket(l, bytes);
        Changed?.Invoke();
        return r;
    }

    public string Uninstall(MarketListing l, ItemState st)
    {
        if (l.Item.Kind == MarketKind.Theme)
        {
            if (st.ThemeKey == null || st.ThemeKey.StartsWith("builtin:", StringComparison.Ordinal)) return "内置主题不能卸载";
            if (st.Active) _applyTheme?.Invoke(null);
            var ok = ThemeStore.Uninstall(l.Item.Id);
            Changed?.Invoke();
            return ok ? $"已卸载主题「{l.Item.Name}」" : "卸载失败";
        }
        if (_host == null || st.Plugin == null) return "没有安装";
        var name = st.Plugin.Manifest.Name;
        var done = _host.Remove(st.Plugin);
        Changed?.Invoke();
        return done ? $"已卸载 {name}" : "卸载失败";
    }

    /// <summary>正在用的主题包被撤销：换回默认外观并提示。</summary>
    public void CheckThemeRevocation()
    {
        var key = ThemeStore.ActiveKey;
        if (key == null) return;
        var id = key.StartsWith("builtin:", StringComparison.Ordinal) ? key[8..] : key.StartsWith("user:", StringComparison.Ordinal) ? Path.GetFileNameWithoutExtension(key[5..]) : null;
        if (id == null) return;
        var reason = _builtinRevoked.Match(id, null, null) ?? Status(MarketSources.Official)?.Revoked?.Match(id, null, ThemeStore.FileSha256(key));
        if (reason == null) return;
        App.Log($"市场: 主题 {id} 命中撤销名单，换回默认：{reason}");
        _applyTheme?.Invoke(null);
        ThemeRevoked?.Invoke(id, reason);
    }

    /// <summary>渲染素材用：模拟「刚从官方索引刷新过」，并给一项第三方源的演示条目。不联网、不落盘。</summary>
    public void Demo()
    {
        var third = MarketRegistry.Parse("""
        { "schema_version": 1, "updated_at": "2026-11-27T08:00:00Z", "items": [
          { "id": "pomodoro-stats", "name": "番茄钟统计", "version": "0.3.1", "tier": "declarative", "category": "productivity", "author": "社区示例",
            "description": "按天汇总专注时长的小卡片（第三方源演示数据）。", "icon": "◔",
            "download_url": "https://example.com/qisland/pomodoro-stats/plugin.json", "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
            "permissions": { "domains": [] }, "min_app_version": "5.0.0" },
          { "id": "gpu-temp-probe", "name": "显卡温度探针", "version": "1.2.0", "tier": "process", "category": "dev", "author": "社区示例",
            "description": "运行本机脚本读取显卡温度（第三方源演示数据，L3 高级插件）。", "icon": "♨",
            "download_url": "https://example.com/qisland/gpu-temp-probe.zip", "sha256": "1111111111111111111111111111111111111111111111111111111111111111",
            "permissions": {}, "min_app_version": "5.0.0" } ] }
        """);
        var src = new MarketSource("社区示例源", "https://example.com/qisland/registry.json", false);
        CustomSources = [src];
        _status[src.Url] = new SourceStatus(src, third, null, false, DateTimeOffset.Now.AddHours(-3), null);
        if (_builtin.IsValid) _status[MarketSources.OfficialRegistry] = new SourceStatus(MarketSources.Official, _builtin, _builtinRevoked, true, DateTimeOffset.Now.AddMinutes(-5), null);
        _cache.LastRefresh = DateTimeOffset.Now.AddMinutes(-5);
        Rebuild();
    }
}
