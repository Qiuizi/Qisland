using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Qisland.App;

/// <summary>点岛上的一行，把对应的软件带到前台；没开或者缩在托盘里就重新启动它（单实例软件会自己把窗口叫出来）。</summary>
public static class Launcher
{
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private sealed record Target(string[] Processes, string[] Exes, string? Lnk = null, string? Shell = null);

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static Target? For(string source) => source switch
    {
        "wechat" => new(["Weixin", "WeChat"], [@"C:\Program Files\Tencent\Weixin\Weixin.exe"], "微信"),
        "qq" => new(["QQ"], [@"D:\Program Files\Tencent\QQNT\QQ.exe"], "QQ"),
        "deepseek-harness" or "deepseek" => new(["DeepSeek Harness"], [@"D:\Program Files (x86)\DSH\DeepSeek Harness.exe"], "DeepSeek Harness"),
        "info-station" or "info-deadline" => new(["info-station"], [Path.Combine(Local, "信息监控站", "info-station.exe")], "信息监控站"),
        "coursedesk" or "course" => new(["CourseDesk"], [], "CourseDesk"),
        "jarvis" => new(["CourseWorkspace"], [@"D:\Qiuizi\project\CourseWorkspace\dist\CourseWorkspace\CourseWorkspace.exe"], "Course Workspace"),
        "codex" => new(["ChatGPT", "Codex"], [], "Codex", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App"),
        "music" => new(["cloudmusic"], [@"D:\Program Files\NetEase\CloudMusic\cloudmusic.exe"], "网易云音乐"),
        "timer" or "todo" => null,
        _ => null,
    };

    public static bool CanOpen(string source) => For(source) != null;

    public static bool Open(LiveActivity a) => Open(a.Source);

    public static bool Open(string source)
    {
        var t = For(source);
        if (t is null) return false;
        try
        {
            UnlockForeground();
            if (Focus(t)) { App.Log($"打开 {source}: 切到已有窗口"); return true; }
            foreach (var exe in t.Exes)
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                    App.Log($"打开 {source}: 启动 {exe}");
                    return true;
                }
            if (t.Shell is { } sh)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", sh) { UseShellExecute = true });
                App.Log($"打开 {source}: {sh}");
                return true;
            }
            if (t.Lnk is { } name && FindShortcut(name) is { } lnk)
            {
                Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true });
                App.Log($"打开 {source}: {lnk}");
                return true;
            }
            App.Log($"打开 {source}: 找不到程序");
        }
        catch (Exception ex) { App.Log($"打开 {source} 失败: {ex.Message}"); }
        return false;
    }

    // 岛本身不抢焦点，系统会拦后台进程改前台：合成一次 Alt 让前台锁放开，再允许任何进程（包括刚启动的）拿前台
    private static void UnlockForeground()
    {
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        AllowSetForegroundWindow(-1);
    }

    /// <summary>只认「可见、有标题、有面积」的顶层窗口；缩在托盘里的隐藏窗口返回 false，交给重新启动处理。</summary>
    private static bool Focus(Target t)
    {
        var pids = new HashSet<uint>();
        foreach (var name in t.Processes)
            foreach (var p in Process.GetProcessesByName(name)) { using (p) pids.Add((uint)p.Id); }
        if (pids.Count == 0) return false;

        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h) || GetWindow(h, 4) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            GetWindowRect(h, out var r);
            long area = (long)(r.R - r.L) * (r.B - r.T);
            if (IsIconic(h)) area = Math.Max(area, 1);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero || bestArea < 40_000 && !IsIconic(best)) return false;

        ShowWindow(best, IsIconic(best) ? 9 : 5);
        BringWindowToTop(best);
        SetForegroundWindow(best);
        return true;
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
