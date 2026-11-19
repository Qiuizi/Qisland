using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace Qisland.App;

/// <summary>
/// 取「当前选中的文字」：
/// 1. 先问 UI Automation（不碰剪贴板）；焦点在密码框就直接拒绝。
/// 2. 取不到时模拟一次 Ctrl+C：先备份剪贴板，等剪贴板变了读出文字，再把原来的内容放回去。
///    这次临时复制不会进 Qisland 的剪贴板历史。
/// 读到的文字只在内存里。
/// </summary>
public static class SelectionGrabber
{
    public sealed record Grab(string? Text, IntPtr Window, string? Refused);

    /// <summary>剪贴板历史在这个时间之前的变化都忽略（临时复制 / 粘贴 / 还原）。</summary>
    public static DateTime SuppressClipUntil { get; private set; } = DateTime.MinValue;

    public static async Task<Grab> Get()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return new(null, fg, "没有前台窗口");
        if (Win32Password(fg)) return new(null, fg, "焦点在密码框里，不读取");

        var (isPassword, uiaText) = await Task.Run(UiaSelection).WaitAsync(TimeSpan.FromMilliseconds(700)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (false, null));
        if (isPassword) return new(null, fg, "焦点在密码框里，不读取");
        if (!string.IsNullOrWhiteSpace(uiaText)) return new(uiaText, fg, null);

        // 终端里没选中时 Ctrl+C 是「中断」：绝不替你按
        if (IsTerminal(fg)) return new(null, fg, "终端里请先选中文字（不会替你按 Ctrl+C）");

        // 等你松开快捷键的修饰键，不然 Ctrl+C 会变成 Ctrl+Alt+C
        for (var i = 0; i < 40 && ModifiersDown(); i++) await Task.Delay(25);
        if (GetForegroundWindow() != fg) return new(null, fg, "窗口切走了");

        var backup = Backup();
        var seq = GetClipboardSequenceNumber();
        SuppressClipUntil = DateTime.Now.AddSeconds(3);
        SendChord(0x43); // C
        string? text = null;
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(25);
            if (GetClipboardSequenceNumber() == seq) continue;
            await Task.Delay(30);
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); } catch { }
            break;
        }
        if (GetClipboardSequenceNumber() != seq) Restore(backup);
        SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
        return string.IsNullOrWhiteSpace(text) ? new(null, fg, "没有选中文字") : new(text, fg, null);
    }

    /// <summary>把文字粘贴到 target 窗口（岛不抢焦点，所以一般还是原来那个窗口），然后还原剪贴板。不按回车。</summary>
    public static async Task<bool> PasteInto(IntPtr target, string text)
    {
        if (target == IntPtr.Zero) return false;
        if (GetForegroundWindow() != target && !Launcher.ForceForeground(target)) return false;
        if (Win32Password(target)) return false;
        var backup = Backup();
        SuppressClipUntil = DateTime.Now.AddSeconds(3);
        if (!SetPrivateText(text)) return false;
        for (var i = 0; i < 40 && ModifiersDown(); i++) await Task.Delay(25);
        SendChord(0x56); // V
        await Task.Delay(350);   // 让目标程序读完剪贴板再还原
        Restore(backup);
        SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
        return true;
    }

    /// <summary>复制到剪贴板，标记为「不进剪贴板历史 / 不上云」。</summary>
    public static bool SetPrivateText(string text)
    {
        try
        {
            var d = new DataObject();
            d.SetText(text, TextDataFormat.UnicodeText);
            d.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            d.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(d, true);
            return true;
        }
        catch (Exception ex) { App.Log("写剪贴板失败: " + ex.Message); return false; }
    }

    // ───────────── 剪贴板备份 ─────────────

    private static readonly string[] Formats = [DataFormats.UnicodeText, DataFormats.Rtf, DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap];

    private static Dictionary<string, object>? Backup()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return null;
            var keep = new Dictionary<string, object>();
            foreach (var f in Formats)
                if (data.GetDataPresent(f) && data.GetData(f) is { } v)
                    keep[f] = v is StringCollection sc ? sc.Cast<string>().ToArray() : v;
            return keep;
        }
        catch { return null; }
    }

    private static void Restore(Dictionary<string, object>? keep)
    {
        try
        {
            if (keep == null || keep.Count == 0) { Clipboard.Clear(); return; }
            var d = new DataObject();
            foreach (var (f, v) in keep) d.SetData(f, v);
            Clipboard.SetDataObject(d, true);
        }
        catch (Exception ex) { App.Log("还原剪贴板失败: " + ex.Message); }
    }

    // ───────────── UI Automation ─────────────

    private static (bool password, string? text) UiaSelection()
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el == null) return (false, null);
            if (el.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty) is true) return (true, null);
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var p) && p is TextPattern tp)
            {
                var sel = tp.GetSelection();
                var s = string.Concat(sel.Select(r => r.GetText(AiClient.MaxChars)));
                return (false, s.Trim().Length > 0 ? s : null);
            }
        }
        catch { }
        return (false, null);
    }

    // ───────────── Win32 ─────────────

    private static bool Win32Password(IntPtr fg)
    {
        try
        {
            var tid = GetWindowThreadProcessId(fg, out _);
            var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
            if (!GetGUIThreadInfo(tid, ref info) || info.hwndFocus == IntPtr.Zero) return false;
            return (GetWindowLongPtr(info.hwndFocus, -16).ToInt64() & 0x20 /*ES_PASSWORD*/) != 0 && ClassName(info.hwndFocus).Contains("Edit", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static readonly HashSet<string> TerminalProcs = new(StringComparer.OrdinalIgnoreCase)
        { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty", "wezterm-gui", "alacritty", "Tabby", "Hyper", "kitty", "WindTerm", "MobaXterm", "putty", "Xshell", "FinalShell", "termius", "warp" };

    private static bool IsTerminal(IntPtr fg)
    {
        var cls = ClassName(fg);
        if (cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "PuTTY") return true;
        try
        {
            GetWindowThreadProcessId(fg, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return TerminalProcs.Contains(p.ProcessName);
        }
        catch { return false; }
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool ModifiersDown() =>
        (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0
        || (GetAsyncKeyState(0x11) & 0x8000) != 0;

    private static void SendChord(ushort vk)
    {
        var inputs = new[]
        {
            Key(0x11, false), Key(vk, false), Key(vk, true), Key(0x11, true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private static Input Key(ushort vk, bool up) => new() { type = 1, ki = new KeybdInput { wVk = vk, dwFlags = up ? 2u : 0u } };

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // INPUT 联合体里最大的是 MOUSEINPUT，补齐尺寸
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint type; public KeybdInput ki; private readonly long pad1; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public Rect rcCaret; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint tid, ref GuiThreadInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);
}

public static class ForegroundWait
{
    /// <summary>等某个进程的窗口到前台（豆包刚被叫出来时），超时返回 0。</summary>
    public static async Task<IntPtr> For(string processName, int ms)
    {
        for (var waited = 0; waited < ms; waited += 100)
        {
            var h = GetForegroundWindow();
            if (h != IntPtr.Zero)
            {
                GetWindowThreadProcessId(h, out var pid);
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); if (p.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)) return h; } catch { }
            }
            await Task.Delay(100);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
