using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>
/// 插件分级。
/// · Declarative（L2 · 安全卡片）：纯 JSON，不带任何代码；数据由 Qisland 按声明代发请求，只能访问声明过的 ≤3 个 HTTPS 域名。
/// · Process（L3 · 高级插件）：运行插件目录里的本机脚本 / 程序，没有沙箱，只适合信任来源。
/// </summary>
public enum PluginTier { Declarative, Process }

/// <summary>L2 声明式卡片的硬上限。改这里等于改安全边界，自检里逐条锁住。</summary>
public static class CardLimits
{
    public const int MaxDomains = 3;
    public const int MinIntervalSeconds = 60;
    public const int DefaultIntervalSeconds = 300;
    public const int MaxIntervalSeconds = 86400;
    public const int MaxBodyBytes = 256 * 1024;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    public const int MaxRedirects = 3;
    public const int MaxSources = 5;
    public const int MaxPrimitives = 8;
    public const int MaxActions = 4;
    public const int MaxBadges = 6;
    public const int MaxHeaders = 4;
    public const int MaxPathTokens = 24;
    public const int MaxJsonDepth = 32;
    public const int MaxText = 160;
    public const int MaxPoints = 60;
    public const int MaxPicked = 400;
    public const int MaxConfigFields = 8;
    public const int MaxConfigValue = 200;
    public const int MaxFilters = 8;
    public const int MaxManifestBytes = 64 * 1024;
    /// <summary>L3 进程插件：最短轮询间隔、最长运行时间、最大输出。</summary>
    public const int ProcessMinInterval = 30, ProcessMaxTimeout = 30, ProcessMaxOutputKb = 256;
}

/// <summary>config_schema 里的一个参数（插件管理页照着画输入框）。</summary>
public sealed record ConfigField(string Key, string Label, string Type, string Default, IReadOnlyList<string> Options, string? Pattern, string Placeholder)
{
    public static readonly string[] Types = ["text", "number", "date", "select"];

    /// <summary>校验用户填的值：长度、类型、选项、正则（带超时，防灾难性回溯）。</summary>
    public bool Validate(string? value, out string? error)
    {
        error = null;
        value ??= "";
        if (value.Length > CardLimits.MaxConfigValue) { error = $"{Label} 太长（最多 {CardLimits.MaxConfigValue} 字）"; return false; }
        if (value.Any(char.IsControl)) { error = $"{Label} 含控制字符"; return false; }
        switch (Type)
        {
            case "number":
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) { error = $"{Label} 要填数字"; return false; }
                break;
            case "date":
                if (!DateTime.TryParseExact(value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)) { error = $"{Label} 要填日期（如 2027-01-01）"; return false; }
                break;
            case "select":
                if (!Options.Contains(value)) { error = $"{Label} 只能选：{string.Join(" / ", Options)}"; return false; }
                break;
        }
        if (Pattern is { Length: > 0 } p)
        {
            try { if (!Regex.IsMatch(value, p, RegexOptions.None, TimeSpan.FromMilliseconds(50))) { error = $"{Label} 格式不对"; return false; } }
            catch (RegexMatchTimeoutException) { error = $"{Label} 格式校验超时"; return false; }
        }
        return true;
    }
}

/// <summary>L2 的一个数据源。</summary>
public sealed record CardSource(string Id, string Type, string Url, IReadOnlyDictionary<string, string> Headers, int IntervalSeconds,
    JsonElement? Data, string Target, string Start, string Label);

/// <summary>L2 卡片的一个原语（原始 JSON 属性，渲染时再按模板求值）。</summary>
public sealed record PrimitiveSpec(string Type, JsonElement Props);

/// <summary>
/// plugin.json 解析结果（L2 / L3 共用）。解析不抛异常：致命问题进 <see cref="Errors"/>，
/// <see cref="IsValid"/> 为 false 的插件在管理页显示为「无效」，不会被启用。
/// </summary>
public sealed class PluginManifest
{
    public string Id { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Author { get; private set; } = "";
    public string Description { get; private set; } = "";
    public string Icon { get; private set; } = "◆";
    public string Accent { get; private set; } = "";
    public PluginTier Tier { get; private set; }
    public List<string> Domains { get; } = [];
    public List<string> LocalSources { get; } = [];
    /// <summary>旧版 WinIslands 进程插件的权限说明（字符串数组），只展示不生效。</summary>
    public List<string> Notes { get; } = [];
    public List<ConfigField> ConfigFields { get; } = [];
    public List<CardSource> Sources { get; } = [];
    public List<PrimitiveSpec> Card { get; } = [];
    public string ErrorText { get; private set; } = "";

    // L3
    public string Entry { get; private set; } = "";
    public List<string> Arguments { get; } = [];
    public string Sha256 { get; private set; } = "";
    public int IntervalSeconds { get; private set; }
    public int TimeoutSeconds { get; private set; } = 10;
    public int MaxOutputKb { get; private set; } = 64;
    public Dictionary<string, string> ConfigDefaults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
    /// <summary>组件页里的键：p:插件 id（小写）。</summary>
    public string WidgetKey => "p:" + Id.ToLowerInvariant();

    public static readonly Regex IdRx = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyRx = new("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant);
    private static readonly Regex HexColor = new("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
    private static readonly string[] Reserved = ["config", "now"];
    public static readonly string[] PrimitiveTypes = ["header", "metric", "progress", "sparkline", "badge_row", "action_row"];
    public static readonly string[] Tones = ["neutral", "green", "amber", "red", "blue", "purple", "teal", "auto"];
    public static readonly string[] Statuses = ["ok", "warn", "error", "idle", "live", "auto"];

    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = CardLimits.MaxJsonDepth };

    public static PluginManifest Parse(string? json)
    {
        var m = new PluginManifest();
        if (string.IsNullOrWhiteSpace(json)) { m.Errors.Add("plugin.json 是空的"); return m; }
        if (json.Length > CardLimits.MaxManifestBytes) { m.Errors.Add("plugin.json 超过 64 KiB"); return m; }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, DocOptions); }
        catch (Exception ex) { m.Errors.Add("JSON 格式错误：" + Short(ex.Message, 120)); return m; }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) { m.Errors.Add("plugin.json 顶层要是对象"); return m; }
            m.Load(r.Clone());
        }
        return m;
    }

    private void Load(JsonElement r)
    {
        Id = Str(r, "id").Trim();
        if (!IdRx.IsMatch(Id)) Errors.Add("id 只能用字母、数字、点、横线、下划线（1–64 字）");
        Name = Clean(Str(r, "name"), 40);
        if (Name.Length == 0) Name = Id;
        Version = Clean(Str(r, "version"), 24);
        Author = Clean(Str(r, "author"), 40);
        Description = Clean(Str(r, "description"), 200);
        var icon = Clean(Str(r, "icon"), 8);
        if (icon.Length > 0) Icon = icon;
        var accent = Str(r, "accent");
        if (accent.Length > 0) { if (HexColor.IsMatch(accent)) Accent = accent; else Errors.Add("accent 要写成 #RRGGBB"); }

        var tier = Str(r, "tier").ToLowerInvariant();
        var type = Str(r, "type").ToLowerInvariant();
        Entry = Str(r, "entry").Trim();
        Tier = tier switch
        {
            "declarative" or "l2" or "card" => PluginTier.Declarative,
            "process" or "advanced" or "l3" => PluginTier.Process,
            "" when type == "declarative-card" => PluginTier.Declarative,
            "" when Entry.Length > 0 => PluginTier.Process,
            "" when r.TryGetProperty("card", out _) => PluginTier.Declarative,
            _ => PluginTier.Declarative,
        };
        if (tier.Length > 0 && tier is not ("declarative" or "l2" or "card" or "process" or "advanced" or "l3")) Errors.Add("tier 只能是 declarative 或 process");

        LoadPermissions(r);
        LoadConfigSchema(r);
        if (r.TryGetProperty("config", out var cfg) && cfg.ValueKind == JsonValueKind.Object)
            foreach (var p in cfg.EnumerateObject())
                if (ConfigDefaults.Count < 32) ConfigDefaults[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.GetRawText();

        if (Tier == PluginTier.Declarative) LoadDeclarative(r);
        else LoadProcess(r);
    }

    private void LoadPermissions(JsonElement r)
    {
        if (!r.TryGetProperty("permissions", out var p)) return;
        if (p.ValueKind == JsonValueKind.Array)
        {
            // 旧版 WinIslands：["network", "clipboard"] 这类说明性字符串
            foreach (var e in p.EnumerateArray()) if (e.ValueKind == JsonValueKind.String && Notes.Count < 16) Notes.Add(Clean(e.GetString(), 40));
            return;
        }
        if (p.ValueKind != JsonValueKind.Object) { Errors.Add("permissions 要是对象：{ \"domains\": [...] }"); return; }
        if (p.TryGetProperty("domains", out var d))
        {
            if (d.ValueKind != JsonValueKind.Array) Errors.Add("permissions.domains 要是数组");
            else foreach (var e in d.EnumerateArray())
                {
                    var host = (e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").Trim().TrimEnd('.').ToLowerInvariant();
                    if (!UrlPolicy.IsValidDomain(host, out var why)) { Errors.Add($"域名 {Short(host, 60)} 不允许：{why}"); continue; }
                    if (!Domains.Contains(host)) Domains.Add(host);
                }
            if (Domains.Count > CardLimits.MaxDomains) Errors.Add($"最多声明 {CardLimits.MaxDomains} 个域名（现在 {Domains.Count} 个）");
        }
        if (p.TryGetProperty("local", out var l))
        {
            if (l.ValueKind != JsonValueKind.Array) Errors.Add("permissions.local 要是数组");
            else foreach (var e in l.EnumerateArray())
                {
                    var hp = (e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").Trim().ToLowerInvariant();
                    if (!UrlPolicy.IsValidLocalHostPort(hp)) { Errors.Add($"本机数据源 {Short(hp, 60)} 要写成 127.0.0.1:端口 或 内网地址:端口"); continue; }
                    if (!LocalSources.Contains(hp)) LocalSources.Add(hp);
                }
            if (LocalSources.Count > CardLimits.MaxDomains) Errors.Add($"最多声明 {CardLimits.MaxDomains} 个本机数据源");
        }
    }

    private void LoadConfigSchema(JsonElement r)
    {
        if (!r.TryGetProperty("config_schema", out var s)) return;
        IEnumerable<(string key, JsonElement f)> items = s.ValueKind switch
        {
            JsonValueKind.Array => s.EnumerateArray().Select(f => (f.ValueKind == JsonValueKind.Object ? Str(f, "key") : "", f)),
            // 旧版：{ "properties": { "key": { "title": ..., "default": ... } } } 或直接 { "key": {...} }
            JsonValueKind.Object => (s.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object ? props : s)
                .EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => (x.Name, x.Value)),
            _ => [],
        };
        foreach (var (key, f) in items)
        {
            if (ConfigFields.Count >= CardLimits.MaxConfigFields) { Errors.Add($"config_schema 最多 {CardLimits.MaxConfigFields} 项"); break; }
            if (!KeyRx.IsMatch(key)) { Errors.Add($"参数名 {Short(key, 40)} 只能用小写字母、数字、下划线，且字母开头"); continue; }
            if (ConfigFields.Any(c => c.Key == key)) { Errors.Add($"参数 {key} 重复"); continue; }
            var type = Str(f, "type").ToLowerInvariant();
            if (type is "string" or "") type = "text";
            if (type == "integer") type = "number";
            if (!ConfigField.Types.Contains(type)) { Errors.Add($"参数 {key} 的类型 {type} 不支持（text / number / date / select）"); continue; }
            var label = Clean(Str(f, "label") is { Length: > 0 } lb ? lb : Str(f, "title"), 24);
            var options = f.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString(), 40)).Where(x => x.Length > 0).Take(16).ToList()
                : f.TryGetProperty("enum", out var en) && en.ValueKind == JsonValueKind.Array
                    ? en.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clean(x.GetString(), 40)).Take(16).ToList() : [];
            if (type == "select" && options.Count == 0) { Errors.Add($"参数 {key} 是下拉选择，但没有 options"); continue; }
            var pattern = Str(f, "pattern");
            if (pattern.Length > 200) { Errors.Add($"参数 {key} 的 pattern 太长"); continue; }
            if (pattern.Length > 0) try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(50)); } catch { Errors.Add($"参数 {key} 的 pattern 不是合法正则"); continue; }
            var def = f.TryGetProperty("default", out var dv) ? (dv.ValueKind == JsonValueKind.String ? dv.GetString() ?? "" : dv.ValueKind is JsonValueKind.Number ? dv.GetRawText() : "") : "";
            var field = new ConfigField(key, label.Length > 0 ? label : key, type, def, options, pattern.Length > 0 ? pattern : null, Clean(Str(f, "placeholder"), 60));
            if (def.Length > 0 && !field.Validate(def, out var why)) Errors.Add($"参数 {key} 的默认值不合法：{why}");
            ConfigFields.Add(field);
        }
    }

    private void LoadDeclarative(JsonElement r)
    {
        if (Entry.Length > 0) Errors.Add("声明式卡片不能有 entry（L2 不运行任何程序）");
        foreach (var banned in new[] { "script", "scripts", "exec", "command", "js", "javascript", "html" })
            if (r.TryGetProperty(banned, out _)) Errors.Add($"声明式卡片不支持 {banned}（L2 不执行任何代码）");
        ErrorText = Clean(Str(r, "error_text"), 60);

        if (r.TryGetProperty("sources", out var src))
        {
            IEnumerable<(string id, JsonElement s)> list = src.ValueKind switch
            {
                JsonValueKind.Object => src.EnumerateObject().Select(p => (p.Name, p.Value)),
                JsonValueKind.Array => src.EnumerateArray().Select(s => (s.ValueKind == JsonValueKind.Object ? Str(s, "id") : "", s)),
                _ => [],
            };
            foreach (var (id, s) in list)
            {
                if (Sources.Count >= CardLimits.MaxSources) { Errors.Add($"最多 {CardLimits.MaxSources} 个数据源"); break; }
                LoadSource(id, s);
            }
        }

        if (!r.TryGetProperty("card", out var card) || card.ValueKind != JsonValueKind.Array) { Errors.Add("声明式卡片缺少 card 数组"); return; }
        foreach (var p in card.EnumerateArray())
        {
            if (Card.Count >= CardLimits.MaxPrimitives) { Errors.Add($"一张卡片最多 {CardLimits.MaxPrimitives} 个原语"); break; }
            var errs = ValidatePrimitive(p, Sources.Select(s => s.Id).ToList(), ConfigFields.Select(c => c.Key).ToList());
            if (errs.Count > 0) { Errors.AddRange(errs); continue; }
            Card.Add(new PrimitiveSpec(Str(p, "type"), p.Clone()));
        }
        if (Card.Count == 0 && Errors.Count == 0) Errors.Add("card 里至少要有一个原语");
    }

    private void LoadSource(string id, JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) { Errors.Add("数据源要是对象"); return; }
        id = id.Trim();
        if (!KeyRx.IsMatch(id) || Reserved.Contains(id)) { Errors.Add($"数据源名 {Short(id, 30)} 不合法（小写字母开头，不能叫 config / now）"); return; }
        if (Sources.Any(x => x.Id == id)) { Errors.Add($"数据源 {id} 重复"); return; }
        var type = Str(s, "type").ToLowerInvariant();
        var label = Clean(Str(s, "label"), 40);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (type)
        {
            case "https":
            case "local":
            {
                var url = Str(s, "url").Trim();
                var earlier = Sources.Select(x => x.Id).ToList();
                if (type == "https")
                {
                    if (!UrlPolicy.CheckTemplate(url, Domains, out var why)) { Errors.Add($"数据源 {id}：{why}"); return; }
                }
                else if (!UrlPolicy.CheckLocalTemplate(url, LocalSources, out var why2)) { Errors.Add($"数据源 {id}：{why2}"); return; }
                if (!CardTemplate.CheckUrlBindings(url, ConfigFields.Select(c => c.Key).ToList(), earlier, out var bad)) { Errors.Add($"数据源 {id} 的网址模板：{bad}"); return; }
                if (s.TryGetProperty("headers", out var h))
                {
                    if (h.ValueKind != JsonValueKind.Object) { Errors.Add($"数据源 {id} 的 headers 要是对象"); return; }
                    foreach (var p in h.EnumerateObject())
                    {
                        var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
                        if (!UrlPolicy.IsHeaderAllowed(p.Name, v, out var hw)) { Errors.Add($"数据源 {id} 的请求头 {Short(p.Name, 30)}：{hw}"); continue; }
                        headers[p.Name] = v;
                    }
                    if (headers.Count > CardLimits.MaxHeaders) Errors.Add($"数据源 {id} 最多 {CardLimits.MaxHeaders} 个请求头");
                }
                var interval = s.TryGetProperty("interval_seconds", out var iv) && iv.TryGetInt32(out var n) ? n : CardLimits.DefaultIntervalSeconds;
                Sources.Add(new CardSource(id, type, url, headers, UrlPolicy.ClampInterval(interval), null, "", "", label));
                break;
            }
            case "static":
            {
                if (!s.TryGetProperty("data", out var data)) { Errors.Add($"静态数据源 {id} 缺少 data"); return; }
                Sources.Add(new CardSource(id, type, "", headers, 0, data.Clone(), "", "", label));
                break;
            }
            case "countdown":
            {
                var target = Str(s, "target").Trim();
                if (target.Length == 0) { Errors.Add($"倒计时数据源 {id} 缺少 target"); return; }
                var keys = ConfigFields.Select(c => c.Key).ToList();
                foreach (var t in new[] { target, Str(s, "start") })
                    if (!CardTemplate.CheckUrlBindings(t, keys, [], out var bad)) { Errors.Add($"倒计时数据源 {id}：{bad}"); return; }
                Sources.Add(new CardSource(id, type, "", headers, 0, null, target, Str(s, "start").Trim(), label));
                break;
            }
            default:
                Errors.Add($"数据源 {id} 的类型 {Short(type, 20)} 不支持（https / static / countdown / local）");
                break;
        }
    }

    /// <summary>校验一个原语：类型、必填字段、数量上限、按钮只允许 https 链接或刷新。</summary>
    public static List<string> ValidatePrimitive(JsonElement p, IReadOnlyList<string> sources, IReadOnlyList<string> config)
    {
        var e = new List<string>();
        if (p.ValueKind != JsonValueKind.Object) { e.Add("card 里的每一项要是对象"); return e; }
        var type = Str(p, "type");
        if (!PrimitiveTypes.Contains(type)) { e.Add($"不认识的原语 {Short(type, 20)}（可用：{string.Join(" / ", PrimitiveTypes)}）"); return e; }
        void Need(string key) { if (!p.TryGetProperty(key, out var v) || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) e.Add($"{type} 缺少 {key}"); }
        void Text(string key)
        {
            if (!p.TryGetProperty(key, out var v)) return;
            if (v.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) { e.Add($"{type}.{key} 要是文字或数字"); return; }
            if (v.ValueKind == JsonValueKind.String && !CardTemplate.CheckBindings(v.GetString()!, sources, config, out var bad)) e.Add($"{type}.{key}：{bad}");
        }
        void Tone(JsonElement o, string key, string[] allowed)
        {
            if (o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()!;
                if (!s.Contains("{{") && !allowed.Contains(s)) e.Add($"{type}.{key} 只能是 {string.Join(" / ", allowed)}");
            }
        }
        switch (type)
        {
            case "header":
                Need("title"); Text("title"); Text("subtitle"); Text("icon"); Tone(p, "status", Statuses);
                break;
            case "metric":
                Need("value"); Text("value"); Text("unit"); Text("label"); Text("delta"); Tone(p, "tone", Tones);
                break;
            case "progress":
                Need("value"); Text("value"); Text("max"); Text("label"); Text("text"); Tone(p, "tone", Tones);
                break;
            case "sparkline":
                Need("points"); Text("label"); Text("caption"); Tone(p, "tone", Tones);
                if (p.TryGetProperty("points", out var pts))
                {
                    if (pts.ValueKind == JsonValueKind.Array) { if (pts.GetArrayLength() > CardLimits.MaxPoints || pts.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number)) e.Add($"sparkline.points 最多 {CardLimits.MaxPoints} 个数字"); }
                    else if (pts.ValueKind == JsonValueKind.String) { if (!CardTemplate.CheckBindings("{{" + pts.GetString()!.Trim().Trim('{', '}') + "}}", sources, config, out var bad)) e.Add("sparkline.points：" + bad); }
                    else e.Add("sparkline.points 要是数字数组或数据路径");
                }
                if (p.TryGetProperty("last", out var last) && (!last.TryGetInt32(out var n) || n < 2 || n > CardLimits.MaxPoints)) e.Add($"sparkline.last 要在 2–{CardLimits.MaxPoints} 之间");
                break;
            case "badge_row":
                if (!p.TryGetProperty("badges", out var bs) || bs.ValueKind != JsonValueKind.Array || bs.GetArrayLength() == 0) { e.Add("badge_row 缺少 badges 数组"); break; }
                if (bs.GetArrayLength() > CardLimits.MaxBadges) e.Add($"badge_row 最多 {CardLimits.MaxBadges} 个徽章");
                foreach (var b in bs.EnumerateArray())
                {
                    if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("text", out var bt) || bt.ValueKind != JsonValueKind.String) { e.Add("徽章要写成 { \"text\": ... }"); continue; }
                    if (!CardTemplate.CheckBindings(bt.GetString()!, sources, config, out var bad)) e.Add("徽章：" + bad);
                    Tone(b, "tone", Tones);
                }
                break;
            case "action_row":
                if (!p.TryGetProperty("actions", out var acts) || acts.ValueKind != JsonValueKind.Array || acts.GetArrayLength() == 0) { e.Add("action_row 缺少 actions 数组"); break; }
                if (acts.GetArrayLength() > CardLimits.MaxActions) e.Add($"action_row 最多 {CardLimits.MaxActions} 个按钮");
                foreach (var a in acts.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object) { e.Add("按钮要是对象"); continue; }
                    var label = Str(a, "label");
                    if (label.Length == 0) e.Add("按钮缺少 label");
                    else if (!CardTemplate.CheckBindings(label, sources, config, out var bad)) e.Add("按钮文字：" + bad);
                    var kind = Str(a, "kind");
                    if (kind == "refresh") continue;
                    if (kind != "open_url") { e.Add($"按钮只支持 open_url / refresh（不支持 {Short(kind, 20)}）"); continue; }
                    var url = Str(a, "url");
                    if (!UrlPolicy.CheckOpenTemplate(url, out var why)) e.Add("按钮链接：" + why);
                    else if (!CardTemplate.CheckUrlBindings(url, config, sources, out var bad2)) e.Add("按钮链接：" + bad2);
                }
                break;
        }
        return e;
    }

    private void LoadProcess(JsonElement r)
    {
        if (Entry.Length == 0) { Errors.Add("高级插件缺少 entry"); return; }
        if (Entry.Contains("..") || Path.IsPathRooted(Entry) || Entry.Contains(':')) Errors.Add("entry 只能是插件目录里的相对路径");
        if (!IsSupportedEntry(Entry)) Errors.Add("entry 只支持 .exe / .cmd / .bat / .ps1 / .com");
        if (r.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && Arguments.Count < 16) Arguments.Add(Short(x.GetString() ?? "", 400));
        Sha256 = Str(r, "sha256").Replace("-", "").Trim();
        if (Sha256.Length > 0 && !Regex.IsMatch(Sha256, "^[0-9A-Fa-f]{64}$")) Errors.Add("sha256 要是 64 位十六进制");
        var iv = r.TryGetProperty("interval_seconds", out var i) && i.TryGetInt32(out var n) ? n : 0;
        IntervalSeconds = iv <= 0 ? 0 : Math.Clamp(iv, CardLimits.ProcessMinInterval, CardLimits.MaxIntervalSeconds);
        TimeoutSeconds = Math.Clamp(r.TryGetProperty("timeout_seconds", out var t) && t.TryGetInt32(out var tn) ? tn : 10, 1, CardLimits.ProcessMaxTimeout);
        MaxOutputKb = Math.Clamp(r.TryGetProperty("max_output_kb", out var mo) && mo.TryGetInt32(out var mn) ? mn : 64, 1, CardLimits.ProcessMaxOutputKb);
    }

    public static bool IsSupportedEntry(string path) => Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".cmd" or ".bat" or ".ps1" or ".com";

    /// <summary>用户配置 + 默认值合并，逐项校验；不合法的值退回默认值。</summary>
    public Dictionary<string, string> EffectiveConfig(IReadOnlyDictionary<string, string>? user)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in ConfigFields)
        {
            var v = user != null && user.TryGetValue(f.Key, out var u) ? u : ConfigDefaults.TryGetValue(f.Key, out var cd) ? cd : f.Default;
            d[f.Key] = f.Validate(v, out _) ? v : f.Default;
        }
        // 旧版 L3：没有 schema 的 config 原样透传（只做长度限制）
        if (Tier == PluginTier.Process)
            foreach (var (k, v) in ConfigDefaults)
                if (!d.ContainsKey(k)) d[k] = user != null && user.TryGetValue(k, out var u2) ? Short(u2, CardLimits.MaxConfigValue) : Short(v, CardLimits.MaxConfigValue);
        return d;
    }

    internal static string Str(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string Short(string? s, int n) => s == null ? "" : s.Length <= n ? s : s[..n] + "…";

    /// <summary>展示用文字：去掉控制字符、换行，截断。</summary>
    public static string Clean(string? s, int n)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(Math.Min(s.Length, n + 1));
        foreach (var c in s)
        {
            if (sb.Length >= n) { sb.Append('…'); break; }
            if (char.IsControl(c) || c is '\u200B' or '\u202E' or '\u202D' or '\u2066' or '\u2067' or '\u2068' or '\u2069') { if (c is '\n' or '\t') sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}

/// <summary>一次授权记录：用户确认时插件声明了哪些能力。更新插件后能力变多就要重新确认。</summary>
public sealed record PluginGrant(PluginTier Tier, IReadOnlyList<string> Domains, IReadOnlyList<string> Local, string Entry, string EntryHash)
{
    public static PluginGrant Of(PluginManifest m, string entryHash = "") =>
        new(m.Tier, m.Domains.ToList(), m.LocalSources.ToList(), m.Tier == PluginTier.Process ? m.Entry : "", m.Tier == PluginTier.Process ? entryHash.ToUpperInvariant() : "");
}

public static class PluginPermissions
{
    /// <summary>
    /// 和上次确认的授权比，列出「能力变多」的地方；空列表 = 不用重新确认。
    /// 域名变少、配置变化不算升级；L2 → L3、新域名、新本机数据源、换入口文件或入口内容变了都算。
    /// </summary>
    public static List<string> Escalations(PluginGrant? approved, PluginGrant requested)
    {
        var r = new List<string>();
        if (approved == null)
        {
            r.Add(requested.Tier == PluginTier.Process ? "首次启用高级插件（会运行本机程序）" : "首次启用");
            return r;
        }
        if (approved.Tier == PluginTier.Declarative && requested.Tier == PluginTier.Process) r.Add("从安全卡片（L2）升级为高级插件（L3），会运行本机程序");
        foreach (var d in requested.Domains) if (!approved.Domains.Contains(d, StringComparer.OrdinalIgnoreCase)) r.Add("新增访问域名 " + d);
        foreach (var l in requested.Local) if (!approved.Local.Contains(l, StringComparer.OrdinalIgnoreCase)) r.Add("新增本机数据源 " + l);
        if (requested.Tier == PluginTier.Process && approved.Tier == PluginTier.Process)
        {
            if (!string.Equals(approved.Entry, requested.Entry, StringComparison.OrdinalIgnoreCase)) r.Add($"入口文件变了：{approved.Entry} → {requested.Entry}");
            else if (!string.Equals(approved.EntryHash, requested.EntryHash, StringComparison.OrdinalIgnoreCase)) r.Add("入口文件内容变了（SHA-256 不同）");
        }
        return r;
    }
}
