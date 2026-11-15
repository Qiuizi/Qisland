using System.Globalization;
using System.IO;
using System.Windows.Threading;

namespace Qisland.App;

public sealed record CountdownItem(DateTime At, string Name, string Short)
{
    public int Days => (At.Date - DateTime.Today).Days;
    public string Left => Days switch { <= 0 => "今天", 1 => "明天", 2 => "后天", _ => $"{Days} 天" };
}

/// <summary>
/// 倒计时存在 %APPDATA%\Qisland\countdown.txt，一行一个：
///   2026-11-21 六级口语 | 口语
/// 日期后面可以带时间（2026-12-12 09:00），竖线后面是收起时显示的简称（可省略）。过期的自动不显示。
/// </summary>
public sealed class Countdowns : IDisposable
{
    public static string PathName => Path.Combine(App.DataDir, "countdown.txt");
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public IReadOnlyList<CountdownItem> Items { get; private set; } = [];
    public event Action? Changed;

    public Countdowns()
    {
        if (!File.Exists(PathName))
            File.WriteAllText(PathName,
                "# 倒计时：一行一个「日期 [时间] 名称 | 简称」，# 开头是注释，过期的自动隐藏\n" +
                "2026-11-21 六级口语 | 口语\n" +
                "2026-12-12 09:00 六级笔试 | 六级\n");
        _watcher = new FileSystemWatcher(App.DataDir, "countdown.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Created += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(); };
        Load();
    }

    private void Kick() { _debounce.Stop(); _debounce.Start(); }

    /// <summary>每天零点过后调用一次，让过期的条目消失。</summary>
    public void Refresh() => Load();

    private void Load()
    {
        try
        {
            var list = new List<CountdownItem>();
            foreach (var raw in File.ReadAllLines(PathName))
            {
                var l = raw.Trim();
                if (l.Length < 10 || l.StartsWith('#')) continue;
                var parts = l.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (!DateTime.TryParseExact(parts[0], ["yyyy-MM-dd", "yyyy-M-d", "yyyy/M/d"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
                var rest = l[parts[0].Length..].Trim();
                if (parts.Length > 1 && TimeSpan.TryParseExact(parts[1], ["h\\:mm", "hh\\:mm"], CultureInfo.InvariantCulture, out var t))
                {
                    at += t;
                    rest = rest[parts[1].Length..].Trim();
                }
                if (rest.Length == 0) continue;
                var bar = rest.IndexOf('|');
                var name = (bar >= 0 ? rest[..bar] : rest).Trim();
                var shortName = bar >= 0 ? rest[(bar + 1)..].Trim() : (name.Length > 4 ? name[^4..] : name);
                if (at.Date < DateTime.Today) continue;
                list.Add(new CountdownItem(at, name, shortName));
            }
            Items = list.OrderBy(i => i.At).ToList();
            Changed?.Invoke();
        }
        catch (IOException) { Kick(); }
        catch (Exception ex) { App.Log("倒计时: " + ex.Message); }
    }

    public void Dispose() => _watcher.Dispose();
}

/// <summary>开机自启（HKCU\...\Run）+ 崩溃后自动拉起。</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Qisland";
    private static string OffFlag => Path.Combine(App.DataDir, "autostart.off");

    public static bool Enabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string s && s.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) { k.SetValue(Name, $"\"{Environment.ProcessPath}\" --autostart"); File.Delete(OffFlag); }
            else { k.DeleteValue(Name, false); File.WriteAllText(OffFlag, ""); }
        }
        catch (Exception ex) { App.Log("自启设置: " + ex.Message); }
    }

    /// <summary>默认打开；用户在托盘里关掉过就尊重他的选择。exe 路径变了也会自动更新。</summary>
    public static void EnsureDefault()
    {
        if (File.Exists(OffFlag)) return;
        if (!Enabled) Set(true);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string args, int flags);

    private static int _restarting;

    /// <summary>
    /// 两层兜底：未处理异常时自己拉起一个新进程再退出；
    /// 真的卡死或被系统判定崩溃时，Windows 错误报告会按 RegisterApplicationRestart 重启（需运行满 60 秒）。
    /// </summary>
    public static void InstallCrashRestart()
    {
        try { RegisterApplicationRestart("--restarted", 0); } catch { }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            App.Log("崩溃，自动重启: " + e.ExceptionObject);
            RestartSelf();
        };
    }

    public static void RestartSelf()
    {
        if (Interlocked.Exchange(ref _restarting, 1) == 1) return;
        // 防止启动即崩溃的死循环：60 秒内已经重启过一次就不再拉起
        var stamp = Path.Combine(App.DataDir, "restart.stamp");
        try
        {
            if (File.Exists(stamp) && DateTime.Now - File.GetLastWriteTime(stamp) < TimeSpan.FromSeconds(60)) { App.Log("60 秒内重复崩溃，不再自动重启"); return; }
            File.WriteAllText(stamp, DateTime.Now.ToString("O"));
        }
        catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted") { UseShellExecute = false }); }
        catch { }
    }
}
