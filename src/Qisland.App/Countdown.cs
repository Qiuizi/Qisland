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
    private const string TaskName = "Qisland";
    private static string OffFlag => Path.Combine(App.DataDir, "autostart.off");
    private static string Exe => Environment.ProcessPath!;

    /// <summary>
    /// 两种方式：
    /// 1. 计划任务（登录触发，不排队）——Run 键里的程序是串行启动的，每个最多等 30 秒，
    ///    排在后面会晚好几分钟才出来；计划任务登录即并行启动。创建它要一次 UAC。
    /// 2. Run 键——不需要管理员，作为兜底（用户拒绝 UAC 时）。
    /// 两个都在也没关系，单实例互斥锁会让第二个直接退出。
    /// </summary>
    public static bool Enabled => TaskOk() || RunOk();

    public static bool TaskOk()
    {
        var xml = Schtasks($"/query /tn {TaskName} /xml", out var code);
        return code == 0 && xml.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RunOk()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetRun(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(Name, $"\"{Exe}\" --autostart"); else k.DeleteValue(Name, false);
        }
        catch (Exception ex) { App.Log("自启 Run 键: " + ex.Message); }
    }

    public static void Set(bool on)
    {
        if (on)
        {
            File.Delete(OffFlag);
            if (CreateTask()) { SetRun(false); App.Log("自启：已改为登录计划任务"); }
            else SetRun(true);
        }
        else
        {
            File.WriteAllText(OffFlag, "");
            SetRun(false);
            if (TaskExists()) Elevated($"/delete /tn {TaskName} /f");
        }
    }

    private static bool TaskExists() { Schtasks($"/query /tn {TaskName}", out var c); return c == 0; }

    /// <summary>默认打开；用户在托盘里关掉过就尊重他的选择。启动时从不弹 UAC，只在托盘里切换时弹。</summary>
    public static void EnsureDefault()
    {
        if (File.Exists(OffFlag)) return;
        if (TaskOk()) { if (RunOk()) SetRun(false); return; }
        if (!RunOk()) SetRun(true);
    }

    private static bool CreateTask()
    {
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var esc = System.Security.SecurityElement.Escape;
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Qisland 灵动岛：登录时启动</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{esc(user)}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{esc(Exe)}</Command><Arguments>--autostart</Arguments><WorkingDirectory>{esc(Path.GetDirectoryName(Exe)!)}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(App.DataDir, "autostart-task.xml");
        try
        {
            File.WriteAllText(file, xml, new System.Text.UnicodeEncoding(false, true));
            Elevated($"/create /tn {TaskName} /xml \"{file}\" /f");
        }
        catch (Exception ex) { App.Log("自启计划任务: " + ex.Message); }
        finally { try { File.Delete(file); } catch { } }
        return TaskOk();
    }

    /// <summary>以管理员身份跑 schtasks（弹 UAC）。用户点了取消会抛 Win32Exception 1223，当作失败。</summary>
    private static void Elevated(string args)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
            p?.WaitForExit(30000);
        }
        catch (Exception ex) { App.Log("自启 UAC: " + ex.Message); }
    }

    private static string Schtasks(string args, out int code)
    {
        code = -1;
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return "";
            var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); code = p.ExitCode; return o;
        }
        catch { return ""; }
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
