using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 点击跳转的通用部分：按 AppUserModelId 找窗口 / 激活应用，按工作目录找终端，按标题找窗口。
/// 只读窗口标题、进程名和窗口的 AUMID 属性，不碰别的进程的内存；切前台走 <see cref="Launcher.ForceForeground"/>。
/// </summary>
internal static class AppJump
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int a, out int v, int size);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint len, StringBuilder? id);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit)] private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(16)] public IntPtr p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY k);
        [PreserveSig] int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        [PreserveSig] int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile(string appUserModelId, IntPtr itemArray, string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol(string appUserModelId, IntPtr itemArray, out uint processId);
    }
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] private class ApplicationActivationManager;

    /// <summary>一个可切换的顶层窗口（可见或最小化、有标题、不是工具窗口、没被系统隐藏）。</summary>
    private sealed record Win(IntPtr H, uint Pid, string Title, string Process);

    /// <summary>按 z 序从上到下列出可以切过去的顶层窗口。</summary>
    private static List<Win> Windows()
    {
        var list = new List<Win>();
        var names = new Dictionary<uint, string>();
        var sb = new StringBuilder(512);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            if (DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out var cloaked, 4) == 0 && cloaked != 0) return true;
            sb.Clear();
            if (GetWindowText(h, sb, sb.Capacity) == 0) return true;
            GetWindowRect(h, out var r);
            if (!IsIconic(h) && ((r.R - r.L) < 120 || (r.B - r.T) < 60)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            if (!names.TryGetValue(pid, out var name))
            {
                try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { name = ""; }
                names[pid] = name;
            }
            list.Add(new Win(h, pid, sb.ToString(), name));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string WindowAumid(IntPtr h)
    {
        try
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(h, ref iid, out var store) != 0 || store == null) return "";
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
                if (store.GetValue(ref key, out var v) != 0) return "";
                try { return v.vt == 31 /*VT_LPWSTR*/ && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) ?? "" : ""; }
                finally { PropVariantClear(ref v); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return ""; }
    }

    private static string ProcessAumid(uint pid)
    {
        var h = OpenProcess(0x1000 /*PROCESS_QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            uint len = 0;
            if (GetApplicationUserModelId(h, ref len, null) != 122 /*ERROR_INSUFFICIENT_BUFFER*/ || len == 0) return "";
            var sb = new StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    /// <summary>AUMID 像 exe 名（「cloudmusic.exe」「Chrome」「MSEdge」）时对应的进程名。</summary>
    private static string ExeName(string aumid)
    {
        var a = aumid.Trim();
        if (a.Contains('!') || a.Contains('\\') && !a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "";
        var name = System.IO.Path.GetFileNameWithoutExtension(a);
        return name.Contains('.') ? "" : name;
    }

    /// <summary>按 AUMID 找已开着的窗口切过去；没有就让系统激活这个应用（商店应用走激活管理器，其它走 shell:AppsFolder）。</summary>
    public static bool Aumid(string aumid, string label)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return false;
        var wins = Windows();
        var exe = ExeName(aumid);
        var hit = wins.FirstOrDefault(w => string.Equals(WindowAumid(w.H), aumid, StringComparison.OrdinalIgnoreCase))
                  ?? wins.FirstOrDefault(w => string.Equals(ProcessAumid(w.Pid), aumid, StringComparison.OrdinalIgnoreCase))
                  ?? (exe.Length > 0 ? wins.FirstOrDefault(w => string.Equals(w.Process, exe, StringComparison.OrdinalIgnoreCase)) : null);
        if (hit != null)
        {
            Launcher.UnlockForeground();
            var ok = Launcher.ForceForeground(hit.H);
            App.Log($"跳转 {label}: AUMID 窗口（{hit.Process}）→ {(ok ? "到前台" : "已显示，前台被系统拦下")}");
            return true;
        }
        if (Launcher.DryRun) { App.Log($"  （只解析）{label} 没有开着的窗口，会让系统激活 AUMID"); return true; }
        try
        {
            Launcher.UnlockForeground();
            if (aumid.Contains('!'))
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                var hr = mgr.ActivateApplication(aumid, null, 0, out var pid);
                App.Log($"跳转 {label}: 激活商店应用 → {(hr == 0 ? "成功 pid " + pid : $"失败 0x{hr:X8}")}");
                if (hr == 0) return true;
            }
            if (exe.Length > 0) return false; // 按 exe 名报的会话：交给按来源启动
            Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + aumid) { UseShellExecute = true });
            App.Log($"跳转 {label}: shell:AppsFolder 激活");
            return true;
        }
        catch (Exception ex) { App.Log($"跳转 {label}: AUMID 激活失败 {ex.Message}"); return false; }
    }

    /// <summary>命令行 Agent：切到跑着它的终端 / 编辑器（标题带工作目录名的优先）。</summary>
    public static bool Terminal(string? appId, string label)
    {
        var folder = JumpPlan.Folder(appId);
        var hosts = new HashSet<string>(JumpPlan.TerminalHosts, StringComparer.OrdinalIgnoreCase);
        var wins = Windows().Where(w => hosts.Contains(w.Process)).ToList();
        var i = JumpPlan.PickTerminal(wins.Select(w => (w.Title, w.Process)).ToList(), folder);
        if (i < 0) { App.Log($"跳转 {label}: 没找到终端 / 编辑器窗口"); return false; }
        Launcher.UnlockForeground();
        var ok = Launcher.ForceForeground(wins[i].H);
        App.Log($"跳转 {label}: 终端 {wins[i].Process}{(folder.Length > 0 && wins[i].Title.Contains(folder, StringComparison.OrdinalIgnoreCase) ? "（标题含工作目录）" : "")} → {(ok ? "到前台" : "前台被系统拦下")}");
        return true;
    }

    /// <summary>在这些进程里找标题含 <paramref name="text"/> 的窗口（浏览器里播视频的那个标签页所在窗口）。</summary>
    public static bool Titled(IEnumerable<string> processes, string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
        var set = new HashSet<string>(processes, StringComparer.OrdinalIgnoreCase);
        var hit = Windows().FirstOrDefault(w => set.Contains(w.Process) && w.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (hit == null) return false;
        Launcher.UnlockForeground();
        var ok = Launcher.ForceForeground(hit.H);
        App.Log($"跳转 {label}: 标题匹配的 {hit.Process} 窗口 → {(ok ? "到前台" : "前台被系统拦下")}");
        return true;
    }
}
