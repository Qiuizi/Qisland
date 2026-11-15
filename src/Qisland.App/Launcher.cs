using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Qisland.App;

/// <summary>点岛上的一行，把对应的软件带到前台；没开就启动它。</summary>
public static class Launcher
{
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private sealed record Target(string[] Processes, string[] Exes, string? Lnk = null, string? Url = null);

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static Target? For(string source) => source switch
    {
        "wechat" => new(["Weixin", "WeChat"], [@"C:\Program Files\Tencent\Weixin\Weixin.exe"], "微信"),
        "qq" => new(["QQ"], [@"D:\Program Files\Tencent\QQNT\QQ.exe"], "QQ"),
        "deepseek-harness" or "deepseek" => new(["DeepSeek Harness"], [@"D:\Program Files (x86)\DSH\DeepSeek Harness.exe"], "DeepSeek Harness"),
        "info-station" or "info-deadline" => new(["info-station"], [Path.Combine(Local, "信息监控站", "info-station.exe")], "信息监控站"),
        "coursedesk" or "course" => new(["CourseDesk"], [], "CourseDesk"),
        "jarvis" => new(["CourseWorkspace"], [@"D:\Qiuizi\project\CourseWorkspace\dist\CourseWorkspace\CourseWorkspace.exe"], "Course Workspace"),
        "codex" => new(["Codex"], [], "Codex"),
        _ => null,
    };

    public static bool Open(LiveActivity a)
    {
        var t = For(a.Source);
        if (t is null) return false;
        try
        {
            if (Focus(t)) return true;
            foreach (var exe in t.Exes)
                if (File.Exists(exe)) { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! }); return true; }
            if (t.Lnk is { } name && FindShortcut(name) is { } lnk) { Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true }); return true; }
        }
        catch (Exception ex) { App.Log($"打开 {a.Source} 失败: {ex.Message}"); }
        return false;
    }

    private static bool Focus(Target t)
    {
        foreach (var name in t.Processes)
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (IsIconic(h)) ShowWindowAsync(h, 9);
                    // 后台进程抢前台会被系统拦：先合成一次 Alt 键让系统放行
                    keybd_event(0x12, 0, 0, UIntPtr.Zero);
                    keybd_event(0x12, 0, 2, UIntPtr.Zero);
                    SetForegroundWindow(h);
                    return true;
                }
            }
        return false; // 进程在但只剩托盘图标时，再启动一次 exe，单实例软件会自己把窗口叫出来
    }

    private static string? FindShortcut(string name)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            var hit = Directory.EnumerateFiles(r, name + ".lnk", SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }
}
