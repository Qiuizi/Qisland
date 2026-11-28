using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Qisland.Core;

namespace Qisland.App;

/// <summary>一个已发现的插件：清单 + 来源（官方内置 / 用户目录）+ 运行状态。</summary>
internal sealed class PluginEntry
{
    public required PluginManifest Manifest { get; init; }
    /// <summary>插件目录；官方内置插件没有目录（清单编进程序里）。</summary>
    public string? Dir { get; init; }
    public bool Official { get; init; }
    /// <summary>L3 入口文件当前的 SHA-256（大写十六进制）；找不到文件时为空。</summary>
    public string EntryHash { get; init; } = "";
    public string? LoadError { get; init; }
    /// <summary>5.0 市场：包的 SHA-256（JSON 按规整字节，zip 按安装时记下的原包），撤销名单按它精确命中。</summary>
    public string PackageSha256 { get; init; } = "";
    /// <summary>从插件市场装的才有：来源、验没验签、包哈希。手动导入 / 新建的为 null。</summary>
    public MarketReceipt? Receipt { get; init; }
    /// <summary>编进程序里的官方插件（没有目录，不能删除，只能「卸载」= 从已安装里隐藏）。</summary>
    public bool Bundled => Official && Dir == null;
    public string Id => Manifest.Id;
    public PluginTier Tier => Manifest.Tier;

    // 运行状态（只在 UI 线程改）
    public Dictionary<string, SourceState> States { get; } = new();
    public CardView? ProcessView { get; set; }
    public DateTime NextRun { get; set; } = DateTime.MinValue;
    public int Failures { get; set; }
    public int Unchanged { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastOk { get; set; }
    public bool Running { get; set; }
    public string Signature { get; set; } = "";
    public string BodyHash { get; set; } = "";
}

/// <summary>每个插件的用户状态：开关、上次确认的授权、参数、逐项同意的本机数据源。存在 plugins-state.json。</summary>
internal sealed class PluginUserState
{
    public bool Enabled { get; set; }
    public PluginGrant? Grant { get; set; }
    public Dictionary<string, string> Config { get; set; } = new();
    public List<string> LocalConsent { get; set; } = [];
    /// <summary>内置官方插件被「卸载」：从已安装列表隐藏、不运行；市场里点「安装」恢复。</summary>
    public bool Hidden { get; set; }
    /// <summary>命中撤销名单时的原因；不为空就强制停用、不能再打开。</summary>
    public string? Revoked { get; set; }
}

/// <summary>插件目录里的 .market.json：从哪个索引源装的、验没验签、原包哈希。</summary>
internal sealed record MarketReceipt(string SourceName, string SourceUrl, bool Verified, string Sha256, string Version, DateTimeOffset InstalledAt)
{
    public const string FileName = ".market.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static MarketReceipt? Read(string dir)
    {
        try
        {
            var f = Path.Combine(dir, FileName);
            return File.Exists(f) && new FileInfo(f).Length < 8192 ? JsonSerializer.Deserialize<MarketReceipt>(File.ReadAllText(f), Json) : null;
        }
        catch { return null; }
    }
    public void Write(string dir) => File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
}

/// <summary>
/// 插件宿主：发现插件、管授权、调度刷新、代发 HTTPS 请求、运行 L3 进程、把结果求值成 <see cref="CardView"/>。
/// · L2：Qisland 自己发请求（https + 443 + 白名单域名，建连时再查一次解析出的 IP 不是内网；重定向逐跳检查；
///   响应 ≤ 256 KiB、8 秒超时、不带 Cookie / 凭据），插件本身不执行任何代码。
/// · L3：沿用旧版进程插件机制（超时杀进程树、SHA-256 校验），但必须一次性确认；入口文件变了要重新确认。
/// · 插件拿不到微信消息、剪贴板、其他软件配置、审批接口：宿主不给它们任何这类数据，L3 进程的环境变量里也去掉 Qisland 的令牌。
/// 调度：岛不可见 / 锁屏 / 刚唤醒没网时不发请求；数据没变化逐步放慢；失败指数退避。
/// </summary>
internal sealed class PluginHost
{
    public static PluginHost? Current { get; private set; }
    public static string UserDir => Path.Combine(App.DataDir, "plugins");
    private static string StateFile => Path.Combine(App.DataDir, "plugins-state.json");
    public static readonly string[] OfficialIds = ["github-repo-radar", "countdown-milestone", "exchange-rate-watch", "hn-top-pulse", "npm-package-stats", "quick-dev-links"];

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string UserAgent = $"Qisland/{Updater.Current} (+https://github.com/Qiuizi/Qisland; declarative-card)";

    private readonly Dispatcher _ui;
    private readonly List<PluginEntry> _all = [];
    private Dictionary<string, PluginUserState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _tick;
    private bool _demo;
    private bool _locked;
    private DateTime _lastManual = DateTime.MinValue;

    /// <summary>岛现在看得见吗（不可见时不发请求）。</summary>
    public Func<bool> Active { get; set; } = () => true;
    /// <summary>确认弹窗：传入插件和「为什么要确认」，返回用户逐项同意的本机数据源；取消返回 null。</summary>
    public Func<PluginEntry, IReadOnlyList<string>, List<string>?> Confirm { get; set; } = (_, _) => null;

    public event Action<string>? ViewChanged;
    public event Action? ListChanged;

    public IReadOnlyList<PluginEntry> All => _all;

    public PluginHost(Dispatcher ui)
    {
        _ui = ui;
        Current = this;
        _tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(15) };
        _tick.Tick += (_, _) => _ = Tick();
    }

    public void Start()
    {
        Reload();
        try
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock) _locked = true;
                else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock) _locked = false;
            };
        }
        catch (Exception ex) { App.Log("插件: 锁屏事件 " + ex.Message); }
        _tick.Start();
        _ui.BeginInvoke(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); await Tick(); });
    }

    // ───────────── 发现与状态 ─────────────

    public void Reload()
    {
        _all.Clear();
        if (!_demo) _state = LoadState();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var id in OfficialIds)
        {
            try
            {
                using var s = asm.GetManifestResourceStream("plugin.official." + id + ".json");
                if (s == null) continue;
                using var r = new StreamReader(s, Encoding.UTF8);
                var text = r.ReadToEnd();
                var m = PluginManifest.Parse(text);
                _all.Add(new PluginEntry { Manifest = m, Official = true, PackageSha256 = MarketTrust.PackageSha256(Encoding.UTF8.GetBytes(text)) });
            }
            catch (Exception ex) { App.Log($"插件: 官方 {id} " + ex.Message); }
        }
        if (!_demo && Directory.Exists(UserDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(UserDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(dir).StartsWith('.')) continue;
                var e = LoadDir(dir);
                if (e == null) continue;
                var same = _all.FindIndex(x => string.Equals(x.Id, e.Id, StringComparison.OrdinalIgnoreCase));
                if (same >= 0 && _all[same].Bundled && e.Receipt is { Verified: true } && e.Manifest.IsValid && SemVer.Compare(e.Manifest.Version, _all[same].Manifest.Version) > 0)
                {
                    // 官方索引（验过签）里的新版官方插件：盖过编进程序的旧版；卸载它就回到内置版本
                    _all[same] = new PluginEntry { Manifest = e.Manifest, Dir = e.Dir, Official = true, EntryHash = e.EntryHash, LoadError = e.LoadError, PackageSha256 = e.PackageSha256, Receipt = e.Receipt };
                    continue;
                }
                if (same >= 0) { App.Log($"插件: {e.Id} 和已有插件重名，跳过 {dir}"); continue; }
                _all.Add(e);
            }
        }
        if (!_demo) ApplyRevocations(deferred: true);
        ListChanged?.Invoke();
    }

    private static PluginEntry? LoadDir(string dir)
    {
        var path = Path.Combine(dir, "plugin.json");
        if (!File.Exists(path)) return null;
        try
        {
            var fi = new FileInfo(path);
            var raw = fi.Length > CardLimits.MaxManifestBytes ? null : File.ReadAllBytes(path);
            var m = PluginManifest.Parse(raw == null ? new string(' ', CardLimits.MaxManifestBytes + 1) : Encoding.UTF8.GetString(raw));
            var receipt = MarketReceipt.Read(dir);
            string hash = "", error = "";
            if (m.IsValid && m.Tier == PluginTier.Process)
            {
                var entry = ResolveEntry(dir, m.Entry);
                if (entry == null || !File.Exists(entry)) error = "找不到入口文件 " + m.Entry;
                else
                {
                    using var fs = File.OpenRead(entry);
                    hash = Convert.ToHexString(SHA256.HashData(fs));
                    if (m.Sha256.Length > 0 && !string.Equals(hash, m.Sha256, StringComparison.OrdinalIgnoreCase)) error = "入口文件的 SHA-256 和 plugin.json 里声明的不一致，可能被改过";
                }
            }
            return new PluginEntry
            {
                Manifest = m, Dir = dir, EntryHash = hash, LoadError = error.Length > 0 ? error : null, Receipt = receipt,
                PackageSha256 = receipt?.Sha256 is { Length: 64 } rs ? rs : raw != null ? MarketTrust.PackageSha256(raw) : "",
            };
        }
        catch (Exception ex) { App.Log("插件: 读取 " + dir + ": " + ex.Message); return null; }
    }

    private static string? ResolveEntry(string dir, string entry)
    {
        try
        {
            var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, entry));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }

    private static Dictionary<string, PluginUserState> LoadState()
    {
        try
        {
            var text = DiyFiles.Read(StateFile);
            if (text != null && JsonSerializer.Deserialize<Dictionary<string, PluginUserState>>(text) is { } d)
                return new Dictionary<string, PluginUserState>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { App.Log("插件: 状态文件 " + ex.Message); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveState()
    {
        if (_demo) return;
        DiyFiles.Write(StateFile, JsonSerializer.Serialize(_state, StateJson));
    }

    private PluginUserState StateOf(string id) => _state.TryGetValue(id, out var s) ? s : _state[id] = new PluginUserState();

    public PluginEntry? Find(string id) => _all.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    public PluginEntry? FindByKey(string key) => _all.FirstOrDefault(e => e.Manifest.WidgetKey == key);

    public bool IsEnabled(PluginEntry e) => _state.TryGetValue(e.Id, out var s) && s.Enabled && s.Revoked == null && !s.Hidden;
    public bool IsHidden(PluginEntry e) => _state.TryGetValue(e.Id, out var s) && s.Hidden;
    public string? RevokedReason(PluginEntry e) => _state.TryGetValue(e.Id, out var s) ? s.Revoked : null;

    /// <summary>已安装：内置官方插件没被卸载的 + 插件目录里的。</summary>
    public IEnumerable<PluginEntry> Installed => _all.Where(e => !IsHidden(e));

    /// <summary>和上次确认的授权相比需要重新确认的地方；空 = 授权仍然有效。</summary>
    public List<string> PendingConsent(PluginEntry e)
    {
        var s = _state.TryGetValue(e.Id, out var st) ? st : null;
        return PluginPermissions.Escalations(s?.Grant, PluginGrant.Of(e.Manifest, e.EntryHash));
    }

    /// <summary>能跑：清单有效、开着、授权没过期。</summary>
    public bool Runnable(PluginEntry e) => e.Manifest.IsValid && e.LoadError == null && IsEnabled(e) && PendingConsent(e).Count == 0;

    public IEnumerable<PluginEntry> Cards => _all.Where(Runnable);

    public IReadOnlyDictionary<string, string> ConfigOf(PluginEntry e) =>
        e.Manifest.EffectiveConfig(_state.TryGetValue(e.Id, out var s) ? s.Config : null);

    public IReadOnlyList<string> LocalConsentOf(PluginEntry e) => _state.TryGetValue(e.Id, out var s) ? s.LocalConsent : [];

    /// <summary>开 / 关插件。打开时如果授权是新的或能力变多了，先弹确认；取消就不开。</summary>
    public bool SetEnabled(PluginEntry e, bool on)
    {
        var s = StateOf(e.Id);
        if (on)
        {
            if (!e.Manifest.IsValid || e.LoadError != null || s.Revoked != null) return false;
            s.Hidden = false;
            var why = PendingConsent(e);
            if (why.Count > 0 || (e.Manifest.LocalSources.Count > 0 && s.LocalConsent.Count == 0))
            {
                if (why.Count == 0) why.Add("选择允许读取的本机数据源");
                var consent = Confirm(e, why);
                if (consent == null) return false;
                s.Grant = PluginGrant.Of(e.Manifest, e.EntryHash);
                s.LocalConsent = consent.Where(e.Manifest.LocalSources.Contains).ToList();
            }
        }
        s.Enabled = on;
        e.NextRun = DateTime.MinValue;
        e.Failures = 0;
        SaveState();
        ListChanged?.Invoke();
        if (on) _ = RunNow(e);
        return true;
    }

    public string? SetConfig(PluginEntry e, Dictionary<string, string> values)
    {
        foreach (var f in e.Manifest.ConfigFields)
            if (values.TryGetValue(f.Key, out var v) && !f.Validate(v, out var why)) return why;
        var s = StateOf(e.Id);
        s.Config = values.Where(kv => e.Manifest.ConfigFields.Any(f => f.Key == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        e.States.Clear();
        e.BodyHash = "";
        SaveState();
        Changed(e, force: true);
        _ = RunNow(e);
        return null;
    }

    // ───────────── 导入 / 新建 / 删除 ─────────────

    /// <summary>从 zip 导入（或更新）插件。返回给用户看的结果；更新后能力变多会自动停用，等用户重新确认。</summary>
    public string Import(string zipPath) => Import(zipPath, null, null);

    private string Import(string zipPath, MarketItem? item, MarketReceipt? receipt)
    {
        var staging = Path.Combine(UserDir, ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var fi = new FileInfo(zipPath);
            if (!fi.Exists) return "文件不存在";
            if (fi.Length > 32L * 1024 * 1024) return "压缩包超过 32 MB";
            Directory.CreateDirectory(staging);
            var prefix = Path.GetFullPath(staging).TrimEnd('\\') + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = zip.Entries.FirstOrDefault(x => x.FullName.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
                    ?? zip.Entries.Where(x => x.FullName.EndsWith("/plugin.json", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.FullName.Length).FirstOrDefault();
                if (manifest == null) return "压缩包里没有 plugin.json";
                var baseFolder = manifest.FullName.Length > "plugin.json".Length ? manifest.FullName[..^"plugin.json".Length] : "";
                long total = 0;
                var count = 0;
                foreach (var x in zip.Entries)
                {
                    if (x.FullName.EndsWith('/')) continue;
                    if (baseFolder.Length > 0 && !x.FullName.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase)) continue;
                    if (++count > 200) return "压缩包里的文件太多（最多 200 个）";
                    total += x.Length;
                    if (total > 64L * 1024 * 1024) return "解压后超过 64 MB";
                    var rel = x.FullName[baseFolder.Length..].Replace('/', Path.DirectorySeparatorChar);
                    var dest = Path.GetFullPath(Path.Combine(staging, rel));
                    if (!dest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "压缩包里有越界路径，已拒绝";
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    x.ExtractToFile(dest, overwrite: true);
                }
            }
            var fresh = LoadDir(staging);
            if (fresh == null) return "plugin.json 读不出来";
            if (!fresh.Manifest.IsValid) return "plugin.json 不合格：\n· " + string.Join("\n· ", fresh.Manifest.Errors.Take(6));
            if (fresh.LoadError != null) return fresh.LoadError;
            if (item != null && MarketGate.ManifestMismatch(item, fresh.Manifest) is { Count: > 0 } bad)
                return "包的内容和索引里的声明对不上，已拒绝安装：\n· " + string.Join("\n· ", bad);
            var officialOverride = item != null && receipt is { Verified: true } && OfficialIds.Contains(fresh.Id, StringComparer.OrdinalIgnoreCase);
            if (!officialOverride && OfficialIds.Contains(fresh.Id, StringComparer.OrdinalIgnoreCase)) return $"{fresh.Id} 是官方插件的 id，不能被覆盖";
            receipt?.Write(staging);
            var target = Path.Combine(UserDir, fresh.Id);
            var old = Find(fresh.Id);
            string note;
            if (officialOverride && !Directory.Exists(target))
            {
                Directory.Move(staging, target);
                note = $"已更新 {fresh.Manifest.Name} → {fresh.Manifest.Version}";
            }
            else if (old != null && Directory.Exists(target))
            {
                var backup = target + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Directory.Move(target, backup);
                Directory.Move(staging, target);
                try { Directory.Delete(backup, true); } catch (Exception ex) { App.Log("插件: 删旧版本 " + ex.Message); }
                note = $"已更新 {fresh.Manifest.Name} → {fresh.Manifest.Version}";
            }
            else
            {
                if (Directory.Exists(target)) return "插件目录已存在：" + fresh.Id;
                Directory.Move(staging, target);
                note = item != null ? $"已安装 {fresh.Manifest.Name} {fresh.Manifest.Version}" : $"已导入 {fresh.Manifest.Name}（默认关闭，打开时会让你确认权限）";
            }
            Reload();
            if (Find(fresh.Id) is { } now && IsEnabled(now) && PendingConsent(now) is { Count: > 0 } why)
                note += "\n新版本申请了更多能力，已暂停，确认后才会生效：\n· " + string.Join("\n· ", why);
            return note;
        }
        catch (Exception ex) { App.Log("插件: 导入 " + ex); return "导入失败：" + ex.Message; }
        finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { } }
    }

    /// <summary>在用户插件目录新建一个最小 L2 卡片模板（纯 JSON，零权限），方便照着改。</summary>
    public string CreateTemplate()
    {
        var n = 1;
        string id;
        do id = n == 1 ? "my-card" : $"my-card-{n}"; while ((Find(id) != null || Directory.Exists(Path.Combine(UserDir, id))) && n++ < 99);
        var dir = Path.Combine(UserDir, id);
        Directory.CreateDirectory(dir);
        var json = $$$"""
        {
          "id": "{{{id}}}",
          "name": "我的卡片",
          "version": "0.1.0",
          "author": "me",
          "description": "纯 JSON 声明式卡片：改这里的 sources 和 card 就行，规范见仓库 plugins/README.md。",
          "icon": "✦",
          "accent": "#40C8E0",
          "tier": "declarative",
          "permissions": { "domains": [] },
          "config_schema": [
            { "key": "title", "label": "标题", "type": "text", "default": "我的卡片" }
          ],
          "sources": {
            "info": { "type": "static", "data": { "value": 42, "points": [3, 5, 4, 6, 8, 7, 9] } }
          },
          "card": [
            { "type": "header", "title": "{{config.title}}", "subtitle": "{{now.weekday}}" },
            { "type": "metric", "value": "{{info.value}}", "unit": "个", "label": "静态示例数据" },
            { "type": "sparkline", "points": "info.points", "label": "趋势" },
            { "type": "action_row", "actions": [ { "label": "规范", "icon": "↗", "kind": "open_url", "url": "https://github.com/Qiuizi/Qisland/tree/main/plugins" } ] }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(dir, "plugin.json"), json, new UTF8Encoding(false));
        Reload();
        return id;
    }

    /// <summary>
    /// 卸载：插件目录里的删掉目录；内置官方插件没有目录，改成「从已安装里隐藏 + 停用」，参数保留，市场里一键装回。
    /// 盖过内置版本的官方新版：删掉目录后回到内置版本。
    /// </summary>
    public bool Remove(PluginEntry e)
    {
        try
        {
            if (e.Bundled)
            {
                var s = StateOf(e.Id);
                s.Hidden = true; s.Enabled = false;
                SaveState();
                Reload();
                return true;
            }
            if (e.Dir == null) return false;
            Directory.Delete(e.Dir, true);
            if (!OfficialIds.Contains(e.Id, StringComparer.OrdinalIgnoreCase)) _state.Remove(e.Id);
            SaveState();
            Reload();
            return true;
        }
        catch (Exception ex) { App.Log("插件: 删除 " + ex.Message); return false; }
    }

    /// <summary>市场里「安装」一个内置官方插件：取消隐藏（默认关闭，打开时照常确认权限）。</summary>
    public bool Restore(string id)
    {
        if (Find(id) is not { Bundled: true } e) return false;
        StateOf(e.Id).Hidden = false;
        SaveState();
        ListChanged?.Invoke();
        return true;
    }

    // ───────────── 5.0 插件市场：安装 / 撤销 ─────────────

    /// <summary>撤销名单查询（App 的 MarketService 提供：内置名单 + 验过签的官方名单 + 各第三方源自己的名单）。</summary>
    public Func<PluginEntry, string?> RevocationCheck { get; set; } = _ => null;

    /// <summary>新命中撤销名单、被自动停用的插件（App 在岛上提示一次）。</summary>
    public event Action<IReadOnlyList<(PluginEntry Entry, string Reason)>>? Revoked;

    /// <summary>逐个比对撤销名单：命中就停用并记下原因；不再命中就清掉原因（仍保持关闭，等用户自己打开）。</summary>
    public IReadOnlyList<(PluginEntry Entry, string Reason)> ApplyRevocations(bool deferred = false)
    {
        var hits = new List<(PluginEntry, string)>();
        var dirty = false;
        foreach (var e in _all)
        {
            string? reason;
            try { reason = RevocationCheck(e); } catch (Exception ex) { App.Log("插件: 撤销检查 " + ex.Message); continue; }
            var s = _state.TryGetValue(e.Id, out var st) ? st : null;
            if (reason != null)
            {
                s ??= StateOf(e.Id);
                if (s.Revoked != reason) { s.Revoked = reason; dirty = true; }
                if (s.Enabled) { s.Enabled = false; dirty = true; hits.Add((e, reason)); App.Log($"插件: {e.Id} 命中撤销名单，已停用：{reason}"); }
            }
            else if (s?.Revoked != null) { s.Revoked = null; dirty = true; }
        }
        if (dirty) SaveState();
        if (hits.Count > 0 && !deferred) { ListChanged?.Invoke(); Revoked?.Invoke(hits); }
        else if (hits.Count > 0) _ui.BeginInvoke(() => Revoked?.Invoke(hits));
        return hits;
    }

    /// <summary>
    /// 从市场安装 / 更新：bytes 已由调用方核过 SHA-256。L2 可以是裸 plugin.json，L3 必须是 zip。
    /// 包里的 id / 分级 / 版本 / 权限必须和索引声明一致；验过签的官方索引才能更新官方插件的 id。
    /// 返回 (成功?, 给用户看的话, 插件 id)。
    /// </summary>
    public (bool Ok, string Message) InstallFromMarket(MarketListing l, byte[] bytes)
    {
        var item = l.Item;
        var tmp = Path.Combine(Path.GetTempPath(), "qisland-market-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        try
        {
            if (MarketTrust.IsZip(bytes)) File.WriteAllBytes(tmp, bytes);
            else
            {
                if (item.Tier != PluginTier.Declarative) return (false, "高级插件要用 zip 包发布");
                using var fs = File.Create(tmp);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var en = zip.CreateEntry("plugin.json");
                using var w = en.Open();
                w.Write(bytes);
            }
            var receipt = new MarketReceipt(l.Source.Name, l.Source.Url, l.Verified, MarketTrust.PackageSha256(bytes), item.Version, DateTimeOffset.Now);
            var msg = Import(tmp, item, receipt);
            var ok = msg.StartsWith("已", StringComparison.Ordinal);
            if (ok && Find(item.Id) is { } now) StateOf(now.Id).Hidden = false;
            if (ok) SaveState();
            return (ok, msg);
        }
        catch (Exception ex) { App.Log("插件: 市场安装 " + ex); return (false, "安装失败：" + ex.Message); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ───────────── 求值 ─────────────

    public CardView? View(PluginEntry e)
    {
        if (!e.Manifest.IsValid) return null;
        if (e.Tier == PluginTier.Process)
            return e.ProcessView ?? new CardView(e.Manifest.Name, e.Manifest.Icon, e.Manifest.Accent,
                [new HeaderNode(e.Manifest.Icon, e.Manifest.Name, "高级插件", e.LastError != null ? "error" : "idle")], e.LastError ?? "等待插件输出…", null, true);
        return CardEvaluator.Evaluate(e.Manifest, ConfigOf(e), e.States, DateTime.Now);
    }

    private static string Sig(CardView? v) => v == null ? "" : string.Join("\u001f", v.Nodes.Select(n => n switch
    {
        BadgeRowNode b => string.Join("|", b.Badges),
        ActionRowNode a => string.Join("|", a.Actions),
        SparkNode s => string.Join(",", s.Points) + s.Caption,
        _ => n.ToString(),
    })) + v.Notice;

    private void Changed(PluginEntry e, bool force = false)
    {
        var sig = Sig(View(e));
        if (!force && sig == e.Signature) return;
        e.Signature = sig;
        ViewChanged?.Invoke(e.Manifest.WidgetKey);
    }

    // ───────────── 调度 ─────────────

    private bool CanFetch() => !_demo && !_locked && Active() && App.NetReady();

    private async Task Tick()
    {
        if (_demo) return;
        foreach (var e in _all.Where(Runnable).ToList())
        {
            if (e.Running) continue;
            var net = e.Tier == PluginTier.Process || e.Manifest.Sources.Any(s => s.Type is "https" or "local");
            if (!net) { Changed(e); continue; }   // 纯本地卡片：只重新求值（倒计时、日期）
            if (DateTime.Now < e.NextRun) continue;
            if (e.Tier == PluginTier.Declarative && !CanFetch()) continue;
            if (e.Tier == PluginTier.Process && (_locked || !Active())) continue;
            await RunNow(e);
        }
    }

    /// <summary>手动刷新（卡片上的「刷新」按钮、插件管理页）：10 秒内只认一次。</summary>
    public void Refresh(PluginEntry e)
    {
        if (DateTime.Now - _lastManual < TimeSpan.FromSeconds(10) && e.LastOk is { } t && DateTime.Now - t < TimeSpan.FromSeconds(10)) return;
        _lastManual = DateTime.Now;
        e.NextRun = DateTime.MinValue;
        e.Failures = 0;
        _ = RunNow(e);
    }

    private async Task RunNow(PluginEntry e)
    {
        if (_demo || e.Running || !Runnable(e)) { if (!_demo) Changed(e); return; }
        if (e.Tier == PluginTier.Declarative && !e.Manifest.Sources.Any(s => s.Type is "https" or "local")) { Changed(e, force: true); return; }
        e.Running = true;
        try
        {
            if (e.Tier == PluginTier.Process) await RunProcess(e);
            else await FetchAll(e);
        }
        catch (Exception ex) { e.LastError = ex.Message; App.Log($"插件 {e.Id}: " + ex.Message); }
        finally { e.Running = false; }
        Changed(e);
    }

    private int BaseInterval(PluginEntry e) => e.Tier == PluginTier.Process
        ? Math.Max(e.Manifest.IntervalSeconds, CardLimits.ProcessMinInterval)
        : e.Manifest.Sources.Where(s => s.Type is "https" or "local").Select(s => s.IntervalSeconds).DefaultIfEmpty(CardLimits.DefaultIntervalSeconds).Min();

    private void Schedule(PluginEntry e, bool ok, bool changed)
    {
        var baseSec = BaseInterval(e);
        if (ok)
        {
            e.Failures = 0;
            e.Unchanged = changed ? 0 : Math.Min(e.Unchanged + 1, 6);
            var factor = 1 + 0.5 * e.Unchanged;          // 数据一直没变：最多放慢到 4 倍
            e.NextRun = DateTime.Now.AddSeconds(Math.Min(baseSec * factor, CardLimits.MaxIntervalSeconds));
        }
        else
        {
            e.Failures++;
            var back = Math.Min(60 * Math.Pow(2, e.Failures - 1), 1800);   // 60s → 2min → 4min … 封顶 30 分钟
            e.NextRun = DateTime.Now.AddSeconds(Math.Max(back, CardLimits.MinIntervalSeconds));
        }
    }

    private async Task FetchAll(PluginEntry e)
    {
        var m = e.Manifest;
        var cfg = ConfigOf(e);
        var data = CardEvaluator.LocalData(m, cfg, DateTime.Now);
        foreach (var (k, v) in e.States) if (v.Data is { } d) data[k] = d;
        var allOk = true;
        var hash = new StringBuilder();
        foreach (var s in m.Sources.Where(s => s.Type is "https" or "local"))
        {
            if (s.Type == "local" && !LocalConsentOf(e).Contains(UrlAuthority(s.Url) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                e.States[s.Id] = new SourceState(null, "没有同意读取这个本机数据源", DateTime.Now);
                allOk = false;
                continue;
            }
            var uri = CardEvaluator.SourceUrl(m, s, cfg, data, out var why);
            if (uri == null)
            {
                e.States[s.Id] = new SourceState(e.States.TryGetValue(s.Id, out var old0) ? old0.Data : null, why ?? "网址无效", DateTime.Now);
                allOk = false;
                continue;
            }
            var (json, err, body) = await Fetch(m, s, uri);
            e.States.TryGetValue(s.Id, out var old);
            if (json is { } j)
            {
                e.States[s.Id] = new SourceState(j, null, DateTime.Now);
                data[s.Id] = j;
                hash.Append(body);
            }
            else
            {
                e.States[s.Id] = new SourceState(old?.Data, err, old?.UpdatedAt ?? DateTime.Now);
                allOk = false;
                App.Log($"插件 {e.Id}/{s.Id}: {err}");
            }
        }
        var h = hash.ToString();
        var changed = h != e.BodyHash;
        if (allOk) { e.BodyHash = h; e.LastOk = DateTime.Now; e.LastError = null; }
        else e.LastError = e.States.Values.FirstOrDefault(x => x.Error != null)?.Error;
        Schedule(e, allOk, changed);
    }

    private static string? UrlAuthority(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = url[(i + 3)..];
        var end = rest.IndexOfAny(['/', '?', '#']);
        return (end < 0 ? rest : rest[..end]).ToLowerInvariant();
    }

    // ───────────── 代发请求 ─────────────

    /// <summary>
    /// 公网请求：不跟随重定向（自己逐跳查），不带 Cookie / 凭据；建连回调里再查一次解析到的 IP —— 走代理时连的是代理，
    /// 这时目标 IP 由代理解析，我们只在直连时检查（发请求前另外做了一次 DNS 预检）。
    /// </summary>
    private static readonly HttpClient PublicHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = CardLimits.RequestTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 64,
        ConnectCallback = async (ctx, ct) =>
        {
            var target = ctx.InitialRequestMessage.RequestUri?.IdnHost;
            var direct = string.Equals(ctx.DnsEndPoint.Host, target, StringComparison.OrdinalIgnoreCase);
            var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var pick = direct ? addrs.FirstOrDefault(UrlPolicy.IsPublicAddress) : addrs.FirstOrDefault();
            if (pick == null || (direct && addrs.Any(a => !UrlPolicy.IsPublicAddress(a))))
                throw new HttpRequestException("目标域名解析到了内网 / 本机地址，已拒绝");
            var sock = new Socket(pick.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await sock.ConnectAsync(new IPEndPoint(pick, ctx.DnsEndPoint.Port), ct); return new NetworkStream(sock, ownsSocket: true); }
            catch { sock.Dispose(); throw; }
        },
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>本机数据源（用户逐项同意过的）：不走代理、不跟随重定向。</summary>
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, Credentials = null, ConnectTimeout = CardLimits.RequestTimeout }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<(JsonElement? json, string? error, string body)> Fetch(PluginManifest m, CardSource s, Uri uri)
    {
        using var cts = new CancellationTokenSource(CardLimits.RequestTimeout);
        try
        {
            if (s.Type == "https")
            {
                // DNS 预检：域名解析到内网就不发（走代理时建连回调看不到目标 IP，靠这一步兜住）
                var addrs = await Dns.GetHostAddressesAsync(uri.IdnHost, cts.Token);
                if (addrs.Length == 0 || addrs.Any(a => !UrlPolicy.IsPublicAddress(a))) return (null, "域名解析到了内网 / 本机地址，已拒绝", "");
            }
            var cur = uri;
            for (var hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, cur);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                foreach (var (k, v) in s.Headers)
                {
                    if (!UrlPolicy.IsHeaderAllowed(k, v, out _)) continue;
                    req.Headers.Remove(k);
                    req.Headers.TryAddWithoutValidation(k, v);
                }
                using var resp = await (s.Type == "local" ? LocalHttp : PublicHttp).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                var code = (int)resp.StatusCode;
                if (code is >= 300 and < 400)
                {
                    if (s.Type == "local") return (null, "本机数据源不允许重定向", "");
                    if (!UrlPolicy.CheckRedirect(cur, resp.Headers.Location?.OriginalString, m.Domains, hop + 1, out var next, out var why)) return (null, "重定向被拒绝：" + why, "");
                    cur = next!;
                    continue;
                }
                if (code is < 200 or >= 300) return (null, code == 403 || code == 429 ? $"HTTP {code}（可能被限流，稍后再试）" : $"HTTP {code}", "");
                if (resp.Content.Headers.ContentLength > CardLimits.MaxBodyBytes) return (null, "响应超过 256 KiB", "");
                await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[CardLimits.MaxBodyBytes + 1];
                var n = 0;
                while (n < buf.Length)
                {
                    var r = await stream.ReadAsync(buf.AsMemory(n, buf.Length - n), cts.Token);
                    if (r == 0) break;
                    n += r;
                }
                if (n > CardLimits.MaxBodyBytes) return (null, "响应超过 256 KiB", "");
                var bytes = buf.AsMemory(0, n);
                var parsed = await Task.Run(() =>
                {
                    using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = CardLimits.MaxJsonDepth });
                    return doc.RootElement.Clone();
                });
                return (parsed, null, Convert.ToHexString(SHA256.HashData(bytes.Span))[..16]);
            }
        }
        catch (OperationCanceledException) { return (null, "请求超时（8 秒）", ""); }
        catch (JsonException) { return (null, "返回的不是合法 JSON", ""); }
        catch (HttpRequestException ex) { return (null, "网络错误：" + PluginManifestShort(ex.Message), ""); }
        catch (Exception ex) { return (null, PluginManifestShort(ex.Message), ""); }
    }

    private static string PluginManifestShort(string s) => s.Length <= 80 ? s : s[..80] + "…";

    // ───────────── L3 进程 ─────────────

    private async Task RunProcess(PluginEntry e)
    {
        var m = e.Manifest;
        var entry = e.Dir == null ? null : ResolveEntry(e.Dir, m.Entry);
        if (entry == null || !File.Exists(entry)) { e.LastError = "找不到入口文件"; Schedule(e, false, false); return; }
        // 每次运行前重新算哈希：和确认时不一样就停下来，等用户重新确认
        string hash;
        await using (var fs = File.OpenRead(entry)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        var grant = _state.TryGetValue(e.Id, out var st) ? st.Grant : null;
        if (grant == null || !string.Equals(grant.EntryHash, hash, StringComparison.OrdinalIgnoreCase))
        {
            e.LastError = "入口文件在确认后被改过，已暂停，请在插件页重新确认";
            Reload();
            return;
        }
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = e.Dir!,
        };
        switch (Path.GetExtension(entry).ToLowerInvariant())
        {
            case ".ps1":
                psi.FileName = "powershell.exe";
                foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", entry }) psi.ArgumentList.Add(a);
                break;
            case ".cmd" or ".bat":
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                foreach (var a in new[] { "/d", "/s", "/c", entry }) psi.ArgumentList.Add(a);
                break;
            default:
                psi.FileName = entry;
                break;
        }
        foreach (var a in m.Arguments) psi.ArgumentList.Add(a.Replace("${pluginDir}", e.Dir, StringComparison.OrdinalIgnoreCase));
        // 不把 Qisland 自己的令牌、密钥类环境变量交给插件进程
        foreach (var k in psi.Environment.Keys.ToList())
            if (k.StartsWith("QISLAND", StringComparison.OrdinalIgnoreCase) || k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k.Contains("SECRET", StringComparison.OrdinalIgnoreCase) || k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase))
                psi.Environment.Remove(k);
        foreach (var (k, v) in ConfigOf(e))
        {
            var key = "QISLAND_PLUGIN_CONFIG_" + new string(k.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            psi.Environment[key] = v;
            psi.Environment["WINISLANDS_PLUGIN_CONFIG_" + key["QISLAND_PLUGIN_CONFIG_".Length..]] = v;   // 兼容旧版插件
        }
        using var p = new Process { StartInfo = psi };
        try { if (!p.Start()) { e.LastError = "启动失败"; Schedule(e, false, false); return; } }
        catch (Exception ex) { e.LastError = "启动失败：" + ex.Message; Schedule(e, false, false); return; }
        try { p.StandardInput.Close(); } catch { }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(m.TimeoutSeconds));
        var outTask = ReadCapped(p.StandardOutput, m.MaxOutputKb * 1024);
        var errTask = ReadCapped(p.StandardError, 8 * 1024);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            e.LastError = $"运行超时（{m.TimeoutSeconds} 秒），已结束进程树";
            Schedule(e, false, false);
            return;
        }
        var stdout = await outTask;
        var stderr = await errTask ?? "(stderr 超过 8 KB，已截断)";
        if (stderr.Length > 0) App.Log($"插件 {e.Id} stderr: " + PluginManifestShort(stderr));
        if (stdout == null) { e.LastError = $"输出超过 {m.MaxOutputKb} KB"; Schedule(e, false, false); return; }
        var view = ProcessOutput.Parse(m, stdout, DateTime.Now);
        if (p.ExitCode != 0 || view == null)
        {
            e.LastError = p.ExitCode != 0 ? $"退出码 {p.ExitCode}" : "输出不是可识别的 JSON";
            Schedule(e, false, false);
            return;
        }
        var changed = Sig(view) != Sig(e.ProcessView);
        e.ProcessView = view;
        e.LastError = null;
        e.LastOk = DateTime.Now;
        if (m.IntervalSeconds > 0) Schedule(e, true, changed); else e.NextRun = DateTime.MaxValue;
    }

    private static async Task<string?> ReadCapped(StreamReader r, int max)
    {
        var sb = new StringBuilder();
        var buf = new char[4096];
        int n;
        while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            if (sb.Length + n > max) { while (await r.ReadAsync(buf, 0, buf.Length) > 0) { } return null; }
            sb.Append(buf, 0, n);
        }
        return sb.ToString();
    }

    // ───────────── 素材 / 演示 ─────────────

    /// <summary>--snapshot：全部用离线样例数据，打开 6 个官方插件，另外放一个演示用 L3 插件条目（不运行）。不联网、不落盘。</summary>
    public void Demo(Dictionary<string, Dictionary<string, string>> mocks, PluginManifest? demoProcess)
    {
        _demo = true;
        _tick.Stop();
        _state = new(StringComparer.OrdinalIgnoreCase);
        Reload();
        if (demoProcess != null)
        {
            var e = new PluginEntry { Manifest = demoProcess, Dir = @"C:\demo\plugins\" + demoProcess.Id, EntryHash = "3F1C9A0D7E42B6A5C8D1E0F2A4B6C8D0E1F2A3B4C5D6E7F8091A2B3C4D5E6F70" };
            e.ProcessView = ProcessOutput.Parse(demoProcess, """{ "card": [ { "type": "header", "title": "显卡温度", "subtitle": "本机脚本", "status": "live" }, { "type": "metric", "value": "63", "unit": "℃", "label": "GPU 核心 · 风扇 42%" } ] }""", DateTime.Now);
            e.LastOk = DateTime.Now.AddMinutes(-1);
            _all.Add(e);
            _state[e.Id] = new PluginUserState { Enabled = false };
        }
        foreach (var e in _all.Where(x => x.Official))
        {
            _state[e.Id] = new PluginUserState { Enabled = true, Grant = PluginGrant.Of(e.Manifest) };
            if (mocks.TryGetValue(e.Id, out var mk))
                foreach (var (k, v) in mk) e.States[k] = new SourceState(JsonDocument.Parse(v).RootElement.Clone(), null, DateTime.Now.AddMinutes(-2));
            e.LastOk = DateTime.Now.AddMinutes(-2);
            e.NextRun = DateTime.Now.AddMinutes(13);
        }
        ListChanged?.Invoke();
    }

    /// <summary>演示：给某个插件塞一个错误态（看错误渲染）。</summary>
    public void DemoError(string id, string error)
    {
        if (Find(id) is not { } e) return;
        foreach (var s in e.Manifest.Sources.Where(s => s.Type == "https")) e.States[s.Id] = new SourceState(null, error, DateTime.Now);
        ViewChanged?.Invoke(e.Manifest.WidgetKey);
    }
}
