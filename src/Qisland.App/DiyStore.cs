using System.IO;
using System.Reflection;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 5.0 零代码 DIY 的三份配置：收起胶囊编排、主题包、快捷按钮。都放在数据目录里，读一次缓存在内存；
/// 渲染素材（--snapshot）时只改内存不落盘，不会碰到你本机的配置。
/// </summary>
internal static class DiyFiles
{
    public static bool Snapshot => Environment.GetCommandLineArgs().Contains("--snapshot");

    public static void Write(string path, string text)
    {
        if (Snapshot) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!AtomicFile.TryWrite(path, Encoding.UTF8.GetBytes(text), out var err)) App.Log("保存 " + Path.GetFileName(path) + ": " + err);
        }
        catch (Exception ex) { App.Log("保存 " + Path.GetFileName(path) + ": " + ex.Message); }
    }

    public static string? Read(string path, int max = 256 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > max) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) { App.Log("读取 " + Path.GetFileName(path) + ": " + ex.Message); return null; }
    }
}

/// <summary>收起胶囊编排（capsule-layout.txt）。</summary>
internal static class CapsuleStore
{
    private static string FilePath => Path.Combine(App.DataDir, "capsule-layout.txt");
    private static CapsuleLayout? _cur;
    public static event Action? Changed;

    public static CapsuleLayout Current => _cur ??= CapsuleLayout.Parse(DiyFiles.Read(FilePath));

    public static void Save(CapsuleLayout l)
    {
        _cur = l;
        DiyFiles.Write(FilePath, l.Serialize());
        Changed?.Invoke();
    }

    /// <summary>渲染素材用：只换内存里的编排，不写文件。</summary>
    public static void Demo(CapsuleLayout? l) { _cur = l; Changed?.Invoke(); }
}

/// <summary>快捷按钮（quick-actions.json）。</summary>
internal static class QuickStore
{
    private static string FilePath => Path.Combine(App.DataDir, "quick-actions.json");
    private static List<QuickAction>? _cur;
    public static event Action? Changed;

    public static IReadOnlyList<QuickAction> Current => _cur ??= QuickActions.Parse(DiyFiles.Read(FilePath));

    public static void Save(IEnumerable<QuickAction> list)
    {
        _cur = list.Take(QuickActions.Max).ToList();
        DiyFiles.Write(FilePath, QuickActions.Serialize(_cur));
        Changed?.Invoke();
    }

    public static void Demo(IEnumerable<QuickAction>? list) { _cur = list?.ToList(); Changed?.Invoke(); }
}

/// <summary>
/// 主题包：3 套官方示例内置在程序里（仓库 themes/ 目录），导入的放在数据目录 themes\ 下。
/// theme.txt 记当前用哪一套（builtin:文件名 或 user:文件名），空 = 不用主题包。
/// </summary>
internal static class ThemeStore
{
    public sealed record Entry(string Key, ThemePack Pack, bool Builtin);

    private static string ActiveFile => Path.Combine(App.DataDir, "theme.txt");
    public static string UserDir => Path.Combine(App.DataDir, "themes");
    private static string? _activeKey;
    private static bool _loaded;
    private static ThemePack? _active;

    public static readonly string[] BuiltinNames = ["obsidian-night", "mist-lime", "amber-gold"];

    public static IReadOnlyList<Entry> Builtins { get; } = LoadBuiltins();

    private static List<Entry> LoadBuiltins()
    {
        var list = new List<Entry>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var n in BuiltinNames)
        {
            try
            {
                using var s = asm.GetManifestResourceStream("theme." + n + ".json");
                if (s == null) continue;
                using var r = new StreamReader(s, Encoding.UTF8);
                if (ThemePack.Parse(r.ReadToEnd(), out _, out _) is { } p) list.Add(new Entry("builtin:" + n, p, true));
            }
            catch (Exception ex) { App.Log("内置主题 " + n + ": " + ex.Message); }
        }
        return list;
    }

    public static List<Entry> All()
    {
        var list = new List<Entry>(Builtins);
        try
        {
            if (Directory.Exists(UserDir))
                foreach (var f in Directory.EnumerateFiles(UserDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(40))
                    if (ThemePack.Parse(DiyFiles.Read(f, ThemePack.MaxBytes), out _, out _) is { } p)
                        list.Add(new Entry("user:" + Path.GetFileName(f), p, false));
        }
        catch (Exception ex) { App.Log("读取主题目录: " + ex.Message); }
        return list;
    }

    public static string? ActiveKey { get { Load(); return _activeKey; } }

    /// <summary>当前主题包（原始的，未按外观钳位；Theme.ApplyPack 会再钳一次）。</summary>
    public static ThemePack? Active { get { Load(); return _active; } }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        var key = DiyFiles.Read(ActiveFile, 1024)?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        _active = Resolve(key);
        _activeKey = _active != null ? key : null;
    }

    private static ThemePack? Resolve(string key)
    {
        if (key.StartsWith("builtin:", StringComparison.Ordinal)) return Builtins.FirstOrDefault(b => b.Key == key)?.Pack;
        if (key.StartsWith("user:", StringComparison.Ordinal))
        {
            var name = Path.GetFileName(key[5..]);
            if (name.Length == 0 || name != key[5..]) return null;
            return ThemePack.Parse(DiyFiles.Read(Path.Combine(UserDir, name), ThemePack.MaxBytes), out _, out _);
        }
        return null;
    }

    /// <summary>切换主题包（null = 恢复默认）。返回主题包想要的外观（材质），调用方据此切外观。</summary>
    public static void SetActive(string? key)
    {
        _loaded = true;
        _active = key == null ? null : Resolve(key);
        _activeKey = _active != null ? key : null;
        DiyFiles.Write(ActiveFile, _activeKey ?? "");
    }

    /// <summary>渲染素材用：只在内存里换主题包。</summary>
    public static void Demo(ThemePack? p) { _loaded = true; _active = p; _activeKey = null; }

    /// <summary>主题包对应的外观：纯色按底色深浅走深色 / 白色，磨砂走磨砂玻璃，液态走液态玻璃；没写材质就保持当前外观。</summary>
    public static Theme.Look LookFor(ThemePack p, Theme.Look current) => p.Material switch
    {
        ThemeMaterial.Solid => p.DarkBase ? Theme.Look.Dark : Theme.Look.Light,
        ThemeMaterial.Frosted => Theme.Look.Glass,
        ThemeMaterial.Liquid => Theme.Look.Liquid,
        _ => current,
    };

    /// <summary>导入：校验通过后把规整过的 JSON 存进数据目录 themes\（同名覆盖），返回它的键。</summary>
    public static string? Import(string path, out ThemePack? pack, out string? error, out List<string> warnings)
    {
        pack = null; warnings = [];
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { error = "文件不存在"; return null; }
            if (fi.Length > ThemePack.MaxBytes) { error = "文件超过 32 KB，不像主题包"; return null; }
            pack = ThemePack.Parse(File.ReadAllText(path), out error, out warnings);
            if (pack == null) return null;
            var name = ThemePack.SafeFileName(pack.Name);
            Directory.CreateDirectory(UserDir);
            DiyFiles.Write(Path.Combine(UserDir, name), pack.ToJson());
            return "user:" + name;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    // ───────────── 5.0 插件市场 ─────────────

    /// <summary>市场装的主题存成 themes\&lt;id&gt;.json（原样字节，SHA-256 可复核）；返回已装版本，没装返回 null。</summary>
    public static string? InstalledVersion(string id)
    {
        var f = MarketFile(id);
        if (f == null || !File.Exists(f)) return null;
        return ThemePack.Parse(DiyFiles.Read(f, ThemePack.MaxBytes), out _, out _) is { } p ? (p.Version.Length > 0 ? p.Version : "0") : null;
    }

    private static string? MarketFile(string id) => PluginManifest.IdRx.IsMatch(id) ? Path.Combine(UserDir, id + ".json") : null;

    /// <summary>校验通过才落盘；返回错误，成功返回 null。</summary>
    public static string? InstallFromMarket(string id, byte[] bytes)
    {
        if (MarketFile(id) is not { } f) return "主题 id 不合法";
        if (BuiltinNames.Contains(id, StringComparer.OrdinalIgnoreCase)) return "不能覆盖内置主题";
        if (bytes.Length > ThemePack.MaxBytes) return "主题包超过 32 KB";
        var text = Encoding.UTF8.GetString(MarketTrust.Canonical(bytes));
        if (ThemePack.Parse(text, out var error, out _) == null) return "主题包不合格：" + error;
        try
        {
            Directory.CreateDirectory(UserDir);
            DiyFiles.Write(f, text);
            return null;
        }
        catch (Exception ex) { return "保存失败：" + ex.Message; }
    }

    public static bool Uninstall(string id)
    {
        if (MarketFile(id) is not { } f) return false;
        try { if (File.Exists(f)) File.Delete(f); return true; }
        catch (Exception ex) { App.Log("卸载主题: " + ex.Message); return false; }
    }

    /// <summary>主题文件的规整 SHA-256（撤销名单按哈希命中用）；内置主题按编进程序的字节算。</summary>
    public static string? FileSha256(string key)
    {
        try
        {
            if (key.StartsWith("user:", StringComparison.Ordinal) && Path.GetFileName(key[5..]) is { Length: > 0 } n && File.Exists(Path.Combine(UserDir, n)))
                return MarketTrust.PackageSha256(File.ReadAllBytes(Path.Combine(UserDir, n)));
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 导出「当前配置」：用着主题包就导出它；没用就把当前外观的颜色整理成一份起步模板，改几个字段就是自己的主题。
    /// </summary>
    public static ThemePack ExportPack(Theme.Look look)
    {
        if (Active is { } a) return a;
        static Argb? Of(System.Windows.Media.Brush? b)
        {
            System.Windows.Media.Color? c = b switch
            {
                System.Windows.Media.SolidColorBrush s => s.Color,
                System.Windows.Media.GradientBrush g when g.GradientStops.Count > 0 => g.GradientStops[0].Color,
                _ => null,
            };
            return c is { } v ? new Argb(v.A, v.R, v.G, v.B) : null;
        }
        Argb? End(System.Windows.Media.Brush? b) => b is System.Windows.Media.GradientBrush g && g.GradientStops.Count > 1
            ? new Argb(g.GradientStops[^1].Color.A, g.GradientStops[^1].Color.R, g.GradientStops[^1].Color.G, g.GradientStops[^1].Color.B) : null;
        return new ThemePack
        {
            Name = "我的主题",
            Version = "1.0",
            Description = "从「" + Theme.LookName(look) + "」外观导出",
            Material = look switch { Theme.Look.Glass => ThemeMaterial.Frosted, Theme.Look.Liquid => ThemeMaterial.Liquid, _ => ThemeMaterial.Solid },
            Background = Of(Theme.Pill), BackgroundEnd = End(Theme.Pill),
            Card = Of(Theme.Card), Accent = Of(Theme.Blue), Text = Of(Theme.Primary), Subtext = Of(Theme.Secondary),
            Radius = Theme.ExpandedRadius, Opacity = Theme.IslandOpacity,
        }.Sanitized();
    }
}
