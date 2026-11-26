using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Qisland.App;

/// <summary>
/// fullscreen.txt 的读写（设置页「自动隐藏」用）：
/// · hide 名单：这些程序在前台时岛藏起来，哪怕不是全屏（类似 pyisland 的进程黑名单）；
/// · show 名单：这些程序全屏时岛照常显示。
/// 一行一个「hide 进程名」/「show 进程名」，# 开头是注释。写回时原样保留注释和认不出的行，只重排名单本身，
/// 所以手改过的文件不会被设置页冲掉。岛每秒按修改时间重读，不用重启。
/// </summary>
public static class HideRules
{
    public static string PathName => IslandWindow.FullscreenListPath;

    public static string Clean(string name)
    {
        var n = name.Trim().Trim('"');
        n = Path.GetFileName(n);
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    public static (List<string> Show, List<string> Hide) Read()
    {
        var show = new List<string>(); var hide = new List<string>();
        try
        {
            if (!File.Exists(PathName)) return (show, hide);
            foreach (var raw in File.ReadAllLines(PathName))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith('#')) continue;
                var parts = l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 2) continue;
                var name = Clean(parts[1]);
                var list = parts[0] is "show" or "显示" ? show : parts[0] is "hide" or "隐藏" ? hide : null;
                if (list != null && name.Length > 0 && !list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
            }
        }
        catch (Exception ex) { App.Log("读 fullscreen.txt: " + ex.Message); }
        return (show, hide);
    }

    public static bool Write(IEnumerable<string> show, IEnumerable<string> hide)
    {
        try
        {
            var keep = new List<string>();
            if (File.Exists(PathName))
                foreach (var raw in File.ReadAllLines(PathName))
                {
                    var l = raw.Trim();
                    var head = l.Split(' ', 2)[0];
                    if (l.Length > 0 && !l.StartsWith('#') && head is "show" or "显示" or "hide" or "隐藏") continue;
                    keep.Add(raw);
                }
            while (keep.Count > 0 && keep[^1].Trim().Length == 0) keep.RemoveAt(keep.Count - 1);
            var sb = new StringBuilder();
            foreach (var l in keep) sb.Append(l).Append('\n');
            foreach (var n in show.Select(Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) sb.Append("show ").Append(n).Append('\n');
            foreach (var n in hide.Select(Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) sb.Append("hide ").Append(n).Append('\n');
            File.WriteAllText(PathName, sb.ToString());
            return true;
        }
        catch (Exception ex) { App.Log("写 fullscreen.txt: " + ex.Message); return false; }
    }

    // 系统外壳、输入法这类常驻窗口：不放进「正在运行」建议里
    private static readonly HashSet<string> Shell = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "ApplicationFrameHost",
        "SystemSettings", "LockApp", "Widgets", "WidgetBoard", "ShellHost", "dwm", "Qisland", "ctfmon", "SecurityHealthSystray",
    };

    /// <summary>当前有可见主窗口的程序（去重、按名字排序），给设置页做「点一下加入」的建议。</summary>
    public static List<string> RunningApps(int max = 10)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var self = Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            try { var n = Process.GetProcessById((int)pid).ProcessName; if (!Shell.Contains(n)) names.Add(n); } catch { }
            return true;
        }, IntPtr.Zero);
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
