using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Qisland.Core;

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
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public string? lpVerb; public string lpFile; public string? lpParameters;
        public string? lpDirectory; public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string? lpClass; public IntPtr hkeyClass;
        public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO i);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    /// <summary>Hotkey：软件自带的「显示主窗口」快捷键（微信 Ctrl+Alt+W、QQ Ctrl+Alt+Z）。
    /// 这类软件在运行时绝不能再启动一次 exe：微信 4.x 支持多开，会弹出一个新的登录窗口。</summary>
    private sealed record Target(string[] Processes, string[] Exes, string? Lnk = null, string? Shell = null, byte Hotkey = 0, string? MainTitle = null);

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static Target? For(string source) => source switch
    {
        "wechat" => new(["Weixin", "WeChat"], [@"C:\Program Files\Tencent\Weixin\Weixin.exe"], "微信", null, (byte)'W', "微信"),
        "qq" => new(["QQ"], [@"D:\Program Files\Tencent\QQNT\QQ.exe"], "QQ", null, (byte)'Z', "QQ"),
        "deepseek-harness" or "deepseek" => new(["DeepSeek Harness"], [@"D:\Program Files (x86)\DSH\DeepSeek Harness.exe"], "DeepSeek Harness"),
        "doubao" => new(["Doubao", "doubao"], [Path.Combine(Local, "Doubao", "Application", "Doubao.exe")], "豆包"),
        "trae" => new(["Trae", "Trae CN"], [Path.Combine(Local, "Programs", "Trae", "Trae.exe"), Path.Combine(Local, "Programs", "Trae CN", "Trae CN.exe")], "Trae"),
        "workbuddy" => new(["WorkBuddy"], [Path.Combine(Local, "Programs", "WorkBuddy", "WorkBuddy.exe")], "WorkBuddy"),
        "info-station" or "info-deadline" => new(["info-station"], [Path.Combine(Local, "信息监控站", "info-station.exe")], "信息监控站"),
        "coursedesk" or "course" => new(["CourseDesk"], [], "CourseDesk"),
        "jarvis" => new(["CourseWorkspace"], [], "Course Workspace"),
        "cursor" => new(["Cursor"], [Path.Combine(Local, "Programs", "cursor", "Cursor.exe")], "Cursor"),
        "codex" => new(["ChatGPT", "Codex"], [], "Codex", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App"),
        "music" => new(["cloudmusic"], [@"D:\Program Files\NetEase\CloudMusic\cloudmusic.exe"], "网易云音乐"),
        "qqmusic" => new(["QQMusic"], [@"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe", @"D:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe"], "QQ音乐"),
        "spotify" => new(["Spotify"], [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe")], "Spotify", @"shell:AppsFolder\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
        "kugou" => new(["KuGou"], [], "酷狗音乐"),
        "kuwo" => new(["kwmusic", "KwMusic"], [], "酷我音乐"),
        "applemusic" => new(["AppleMusic"], [], "Apple Music", @"shell:AppsFolder\AppleInc.AppleMusicWin_nzyj5cx40ttqa!App"),
        "foobar" => new(["foobar2000"], [], "foobar2000"),
        _ when source.StartsWith("exe:") && source.Length > 4 => new([source[4..]], [], null),
        "potplayer" => new(["PotPlayerMini64", "PotPlayerMini", "vlc"], [@"D:\Program Files\DAUM\PotPlayer\PotPlayerMini64.exe"], "PotPlayer"),
        "chrome" => new(["chrome"], [], null),
        "edge" => new(["msedge"], [], null),
        "firefox" => new(["firefox"], [], null),
        "browser" or "bilibili" => new(["chrome", "msedge", "firefox", "360se", "QQBrowser"], [], null),
        "timer" or "todo" => null,
        _ => null,
    };

    public static bool CanOpen(string source) => For(source) != null;

    /// <summary>调试用：只解析跳转目标、写日志，不切前台、不启动、不按快捷键。</summary>
    [ThreadStatic] public static bool DryRun;

    /// <summary>这个来源对应软件的 exe：优先取正在运行的进程，其次登记的安装路径，最后开始菜单快捷方式指向的目标。</summary>
    public static string? ExePath(string source)
    {
        var t = For(source);
        if (t is null) return null;
        foreach (var name in t.Processes)
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    if (AppIcons.ProcessPath(p.Id) is { } path && File.Exists(path)) return path;
        foreach (var exe in t.Exes) if (File.Exists(exe)) return exe;
        if (t.Lnk is { } name2 && FindShortcut(name2) is { } lnk) return lnk; // 快捷方式本身也带着同一个图标
        return null;
    }

    /// <summary>
    /// 点岛上的一条：有直达链接（Codex 会话、作业网页…）就打开链接；微信走「已有窗口 → weixin:// → 快捷键」；
    /// 其它按来源切到对应软件。每一步的结果都记进 qisland.log。
    /// </summary>
    public static bool Open(LiveActivity a)
    {
        var plan = JumpPlan.For(a.Dispatch, a.OpenUri, a.AppId, CanOpen(a.Dispatch));
        App.Log($"跳转 {a.Id}: 计划 {string.Join(" → ", plan)}");
        foreach (var step in plan)
        {
            var ok = step switch
            {
                JumpStep.Uri => OpenUri(a.OpenUri!, a.Dispatch),
                JumpStep.WeChat => OpenWeChat(),
                JumpStep.App => Open(a.Dispatch),
                JumpStep.Aumid => AppJump.Aumid(a.AppId!, a.Dispatch),
                JumpStep.Terminal => AppJump.Terminal(a.AppId, a.Dispatch),
                JumpStep.Calendar => OpenCalendarApp(),
                _ => false,
            };
            if (ok) return true;
        }
        if (plan.Count > 0) App.Log($"跳转 {a.Id}: 都没成功");
        return false;
    }

    /// <summary>这一条点了能跳到某个软件 / 页面。</summary>
    public static bool CanJump(LiveActivity a) => JumpPlan.For(a.Dispatch, a.OpenUri, a.AppId, CanOpen(a.Dispatch)).Count > 0;

    /// <summary>正在放的歌 / 视频：浏览器先找标题含这个视频名的窗口，再按会话的 AUMID 找播放器窗口，最后按来源切 / 启动。</summary>
    public static bool OpenMedia(MediaState m)
    {
        App.Log($"跳转 媒体 {m.AppSource}");
        if (m.AppSource is "chrome" or "edge" or "firefox" or "browser" or "bilibili" && For(m.AppSource) is { } b && AppJump.Titled(b.Processes, m.Title, m.AppSource)) return true;
        if (Focus(For(m.AppSource) ?? new Target([], []), m.AppSource)) { App.Log($"跳转 {m.AppSource}: 切到播放器窗口"); return true; }
        if (m.Aumid.Length > 0 && AppJump.Aumid(m.Aumid, m.AppSource)) return true;
        return Open(m.AppSource);
    }

    /// <summary>日程没有会议链接：打开系统日历（Outlook 日历 / 日历应用）。</summary>
    private static bool OpenCalendarApp()
    {
        foreach (var scheme in new[] { "outlookcal", "ms-outlook" })
            if (ProtocolRegistered(scheme) && OpenUri(scheme + ":", "calendar")) return true;
        App.Log("跳转 calendar: 没有日历应用");
        return false;
    }

    /// <summary>打开一个链接，不弹系统的「找不到应用」对话框。协议没注册（比如没装 Codex）返回 false，交给按来源打开。</summary>
    public static bool OpenUri(string uri, string source = "link")
    {
        try
        {
            var scheme = uri.Split(':', 2)[0].ToLowerInvariant();
            if (scheme is not ("http" or "https" or "mailto" or "ms-actioncenter") && !ProtocolRegistered(scheme))
            {
                App.Log($"打开 {source}: {scheme}:// 没注册，改为切到软件");
                return false;
            }
            UnlockForeground();
            var ok = ShellOpen(uri);
            App.Log($"打开 {source}: 链接 {Redact(uri)} → {(ok ? "成功" : "失败 " + Marshal.GetLastWin32Error())}");
            return ok;
        }
        catch (Exception ex) { App.Log($"打开 {source} 链接失败: {ex.Message}"); return false; }
    }

    // 链接里可能带 token：日志里只留协议、主机和路径
    private static string Redact(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}{u.AbsolutePath}" : uri.Split('?')[0];

    private static bool ShellOpen(string file)
    {
        if (DryRun) { App.Log($"  （只解析）会打开 {Redact(file)}"); return true; }
        var i = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = 0x400 /*SEE_MASK_FLAG_NO_UI*/, lpVerb = "open", lpFile = file, nShow = 1 };
        return ShellExecuteEx(ref i);
    }

    public static bool ProtocolRegistered(string scheme)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
            return k?.GetValue("URL Protocol") != null || k?.OpenSubKey(@"shell\open\command") != null;
        }
        catch { return false; }
    }

    /// <summary>
    /// 微信：有可见（或最小化）的主窗口就直接切过去；缩在托盘里时交给微信自己的 weixin:// 入口把主窗口叫出来，
    /// 最多等 1.5 秒；协议没注册或没叫出来，再按微信的 Ctrl+Alt+W。
    /// 微信在运行时绝不再启动一次 Weixin.exe（会弹出新的登录窗口），也不强行 ShowWindow 它隐藏的 Qt 窗口（会白屏）。
    /// </summary>
    public static bool OpenWeChat()
    {
        var t = For("wechat")!;
        try
        {
            UnlockForeground();
            if (Focus(t, "wechat")) { App.Log("打开 wechat: 切到已有窗口"); return true; }
            if (DryRun) { App.Log("  （只解析）微信没有可切的窗口，会走 weixin:// / 快捷键"); return true; }
            if (!Running(t, out var pids)) return Open("wechat");
            if (ProtocolRegistered("weixin") && ShellOpen("weixin://"))
            {
                App.Log("打开 wechat: weixin:// 唤回，等主窗口…");
                _ = WaitThenSummon(t, pids);
                return true;
            }
            _ = Task.Run(() => Summon("wechat", t, pids));
            return true;
        }
        catch (Exception ex) { App.Log($"打开 wechat 失败: {ex.Message}"); return false; }
    }

    private static async Task WaitThenSummon(Target t, HashSet<uint> pids)
    {
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(100);
            if (Focus(t, "wechat")) { App.Log($"打开 wechat: weixin:// 唤回成功（{(i + 1) * 100} ms）"); return; }
        }
        App.Log("打开 wechat: weixin:// 1.5 秒内没出窗口，改按快捷键");
        await Task.Run(() => Summon("wechat", t, pids));
    }

    public static bool Open(string source)
    {
        var t = For(source);
        if (t is null) return false;
        try
        {
            UnlockForeground();
            if (Focus(t, source)) { App.Log($"打开 {source}: 切到已有窗口"); return true; }
            if (DryRun) { App.Log($"  （只解析）{source} 没有可切的窗口，会{(t.Hotkey != 0 && Running(t, out _) ? "按快捷键唤出" : "启动它")}"); return true; }
            if (t.Hotkey != 0 && Running(t, out var pids))
            {
                _ = Task.Run(() => Summon(source, t, pids));
                return true;
            }
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
    public static void UnlockForeground()
    {
        if (DryRun) return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        AllowSetForegroundWindow(-1);
    }

    /// <summary>只认「可见、有标题、有面积」的顶层窗口；缩在托盘里的隐藏窗口返回 false，交给重新启动处理。</summary>
    private static bool Focus(Target t, string source = "")
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

        if (!ForceForeground(best)) App.Log($"打开 {source}: 窗口已显示，但系统没让它到前台");
        return true;
    }

    /// <summary>把窗口切到前台：先走正常路径；被前台锁拦下时临时挂到前台线程的输入队列上再切一次。</summary>
    public static bool ForceForeground(IntPtr h)
    {
        if (DryRun)
        {
            GetWindowThreadProcessId(h, out var dp);
            string name; try { using var p = Process.GetProcessById((int)dp); name = p.ProcessName; } catch { name = "?"; }
            App.Log($"  （只解析）会切到 {name} 的窗口{(IsIconic(h) ? "（最小化，会先还原）" : "")}");
            return true;
        }
        ShowWindow(h, IsIconic(h) ? 9 /*SW_RESTORE*/ : 5);
        AllowSetForegroundWindow(-1);
        BringWindowToTop(h);
        SetForegroundWindow(h);
        if (GetForegroundWindow() == h) return true;
        // Win11 前台锁：再合成一次 Alt 解锁后重试
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(h);
        if (GetForegroundWindow() == h) return true;
        var fg = GetForegroundWindow();
        var fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true))
        {
            try { BringWindowToTop(h); SetForegroundWindow(h); }
            finally { AttachThreadInput(me, fgThread, false); }
        }
        if (GetForegroundWindow() == h) return true;
        SwitchToThisWindow(h, true); // 最后一招：和 Alt+Tab 同一条路径
        return GetForegroundWindow() == h;
    }
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr h, bool altTab);

    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    private static bool Running(Target t, out HashSet<uint> pids)
    {
        pids = [];
        foreach (var name in t.Processes)
            foreach (var p in Process.GetProcessesByName(name)) { using (p) pids.Add((uint)p.Id); }
        return pids.Count > 0;
    }

    // 托盘里的微信/QQ：先按软件自己的快捷键让它把主窗口叫出来；快捷键被改掉了，就直接把它隐藏的主窗口显示出来
    private static void Summon(string source, Target t, HashSet<uint> pids)
    {
        if (DryRun) return;
        keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(t.Hotkey, 0, 0, UIntPtr.Zero);
        keybd_event(t.Hotkey, 0, 2, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);
        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(100);
            if (Focus(t, source)) { App.Log($"打开 {source}: 快捷键唤出"); return; }
        }
        // 微信 4.x 是 Qt 窗口：强行 ShowWindow 隐藏的主窗口会白屏，这一步只给 QQ
        if (source == "wechat") { App.Log("打开 wechat: 没叫出主窗口，请从托盘打开微信"); return; }
        var h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, null, t.MainTitle)) != IntPtr.Zero)
        {
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) continue;
            ShowWindow(h, 9);
            ForceForeground(h);
            App.Log($"打开 {source}: 显示隐藏的主窗口");
            return;
        }
        App.Log($"打开 {source}: 没找到主窗口");
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
