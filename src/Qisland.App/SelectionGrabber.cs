using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Qisland.Core;

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
    /// <param name="Focus">划词时前台窗口里有焦点的控件句柄（「替换原文」前核对用）。</param>
    public sealed record Grab(string? Text, IntPtr Window, string? Refused, IntPtr Focus = default, int ProcessId = 0, DateTime CapturedAt = default)
    {
        public PasteTargetSnapshot Target => new(
            Window.ToInt64(),
            Focus.ToInt64(),
            ProcessId,
            CapturedAt == default ? DateTime.Now : CapturedAt);
    }

    public enum PasteOutcome { Pasted, Copied, TargetChanged, Failed }

    public const string TargetChangedMessage = "目标窗口已变化，已复制，请手动粘贴";

    /// <summary>剪贴板备份的结果：Saved 备份到了内容；Empty 剪贴板本来就是空的；Unavailable 没法可靠备份（被占用 / 读失败 / 有存不下来的格式）。</summary>
    internal enum BackupState { Saved, Empty, Unavailable }

    internal sealed record ClipboardBackup(BackupState State, Dictionary<string, object>? Data = null);

    internal interface ISelectionPasteHost
    {
        PasteTargetSnapshot ForegroundTarget();
        uint ClipboardSequenceNumber { get; }
        bool ModifiersAreDown();
        Task DelayAsync(int milliseconds);
        bool SetClipboardText(string text);
        /// <summary>发 Ctrl+V；返回 SendInput 是否把按键全部送进了输入队列。</summary>
        bool SendPaste();
        ClipboardBackup Backup();
        void Restore(ClipboardBackup backup);
    }

    /// <summary>划词时模拟 Ctrl+C 用到的剪贴板 / 按键操作（测试里换成假的）。</summary>
    internal interface ISelectionCopyHost
    {
        uint ClipboardSequenceNumber { get; }
        Task DelayAsync(int milliseconds);
        /// <summary>发 Ctrl+C；返回 SendInput 是否把按键全部送进了输入队列。</summary>
        bool SendCopy();
        string? ReadText();
        ClipboardBackup Backup();
        void Restore(ClipboardBackup backup);
    }

    internal readonly record struct PasteAttemptResult(PasteOutcome Outcome, uint Sequence);

    /// <summary>剪贴板历史在这个时间之前的变化都忽略（临时复制 / 粘贴 / 还原）。</summary>
    public static DateTime SuppressClipUntil { get; private set; } = DateTime.MinValue;

    public static async Task<Grab> Get()
    {
        var fg = GetForegroundWindow();
        var capturedAt = DateTime.Now;
        var pid = ProcessIdOf(fg);
        if (fg == IntPtr.Zero) return new(null, fg, "没有前台窗口", CapturedAt: capturedAt);
        if (Win32Password(fg)) return new(null, fg, "焦点在密码框里，不读取", ProcessId: pid, CapturedAt: capturedAt);

        var (isPassword, uiaText) = await Task.Run(UiaSelection).WaitAsync(TimeSpan.FromMilliseconds(700)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : (false, null));
        if (isPassword) return new(null, fg, "焦点在密码框里，不读取", ProcessId: pid, CapturedAt: capturedAt);
        var focus = FocusOf(fg);
        if (!string.IsNullOrWhiteSpace(uiaText)) return new(uiaText, fg, null, focus, pid, capturedAt);

        // 终端里没选中时 Ctrl+C 是「中断」：绝不替你按
        if (IsTerminal(fg)) return new(null, fg, "终端里请先选中文字（不会替你按 Ctrl+C）", focus, pid, capturedAt);

        // 等你松开快捷键的修饰键，不然 Ctrl+C 会变成 Ctrl+Alt+C
        for (var i = 0; i < 40 && ModifiersDown(); i++) await Task.Delay(25);
        if (GetForegroundWindow() != fg) return new(null, fg, "窗口切走了", focus, pid, capturedAt);

        var (text, refused) = await CopySelectionAsync(WindowsHost);
        return refused != null || string.IsNullOrWhiteSpace(text)
            ? new(null, fg, refused ?? "没有选中文字", focus, pid, capturedAt)
            : new(text, fg, null, focus, pid, capturedAt);
    }

    internal static Task<(string? Text, string? Refused)> CopySelectionForTestAsync(ISelectionCopyHost host) => CopySelectionAsync(host);

    /// <summary>临时 Ctrl+C 取选中文字，再把剪贴板还原。</summary>
    private static async Task<(string? Text, string? Refused)> CopySelectionAsync(ISelectionCopyHost host)
    {
        var backup = host.Backup();
        // 备份不可靠（剪贴板被占用 / 读失败 / 有存不下来的格式）：不做临时复制，免得还原不回去把用户的剪贴板弄丢
        if (backup.State == BackupState.Unavailable)
            return (null, "剪贴板暂时没法可靠备份，没有模拟复制（可以先手动复制再试）");
        var seq = host.ClipboardSequenceNumber;
        SuppressClipUntil = DateTime.Now.AddSeconds(3);
        if (!host.SendCopy())
        {
            SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
            return (null, "模拟 Ctrl+C 没有发出去（按键被系统拒绝），剪贴板没动");
        }
        string? text = null;
        uint copied = 0;
        for (var i = 0; i < 24; i++)
        {
            await host.DelayAsync(25);
            if (host.ClipboardSequenceNumber == seq) continue;
            await host.DelayAsync(30);
            copied = host.ClipboardSequenceNumber;
            text = host.ReadText();
            break;
        }
        // 只在剪贴板还是这次 Ctrl+C 的结果时还原；期间别的程序又复制了东西就不覆盖它
        if (PasteGuard.ShouldRestoreClipboard(copied, host.ClipboardSequenceNumber)) host.Restore(backup);
        SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
        return (text, null);
    }

    /// <summary>把文字粘贴到 target 窗口（岛不抢焦点，所以一般还是原来那个窗口），然后还原剪贴板。不按回车。</summary>
    public static async Task<PasteOutcome> PasteInto(PasteTargetSnapshot target, string text)
    {
        if (target.Window == 0) return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        var window = new IntPtr(target.Window);
        if (GetForegroundWindow() != window && !Launcher.ForceForeground(window))
            return SetPrivateText(text) ? PasteOutcome.TargetChanged : PasteOutcome.Failed;
        if (Win32Password(window)) return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        return await PasteNow(text, target);
    }

    /// <summary>
    /// 「替换原文」：先核对窗口、焦点控件和选中的文字都还是划词时那样才粘贴；
    /// 任何一项变了（换了输入框、改了选区、窗口回不来）就只把译文复制到剪贴板，返回 Copied 让界面提示。
    /// </summary>
    public static async Task<PasteOutcome> ReplaceSelection(Grab origin, string text)
    {
        if (origin.Window == IntPtr.Zero) return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        if (GetForegroundWindow() != origin.Window && !Launcher.ForceForeground(origin.Window))
            return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        if (Win32Password(origin.Window)) return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        await Task.Delay(60);   // 刚切回前台，等焦点落定
        var now = await Get();
        var decision = PasteGuard.Decide(
            new SelectionSnapshot(origin.Window.ToInt64(), origin.Focus.ToInt64(), origin.Text),
            new SelectionSnapshot(now.Window.ToInt64(), now.Focus.ToInt64(), now.Text));
        if (decision == PasteDecision.CopyInstead)
        {
            App.Log("替换原文: 窗口 / 焦点 / 选区变了，改为复制");
            return SetPrivateText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        }
        return await PasteNow(text, origin.Target);
    }

    private static async Task<PasteOutcome> PasteNow(string text, PasteTargetSnapshot target, ISelectionPasteHost? host = null)
    {
        host ??= WindowsHost;
        var backup = host.Backup();
        if (backup.State == BackupState.Unavailable)
        {
            // 没法保证粘贴后还原原来的剪贴板：不走「临时放进剪贴板 → Ctrl+V → 还原」，只把译文复制好让你手动粘贴
            App.Log("粘贴: 剪贴板没法可靠备份，改为只复制");
            return host.SetClipboardText(text) ? PasteOutcome.Copied : PasteOutcome.Failed;
        }
        SuppressClipUntil = DateTime.Now.AddSeconds(3);
        var result = await RunPasteAsync(text, target, host);
        if (result.Outcome != PasteOutcome.Pasted)
        {
            SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
            return result.Outcome;
        }
        await host.DelayAsync(350);   // 让目标程序读完剪贴板再还原
        // 期间剪贴板被别人改过（序号不是我们放进去的那个）就不还原，免得把用户新复制的内容冲掉
        if (PasteGuard.ShouldRestoreClipboard(result.Sequence, host.ClipboardSequenceNumber)) host.Restore(backup);
        else App.Log("粘贴后剪贴板已被其他程序改动，不还原");
        SuppressClipUntil = DateTime.Now.AddMilliseconds(800);
        return PasteOutcome.Pasted;
    }

    internal static Task<PasteOutcome> PasteNowForTestAsync(string text, PasteTargetSnapshot target, ISelectionPasteHost host) =>
        PasteNow(text, target, host);

    internal static Task<PasteAttemptResult> PasteForTestAsync(string text, PasteTargetSnapshot target, ISelectionPasteHost host) =>
        RunPasteAsync(text, target, host);

    private static async Task<PasteAttemptResult> RunPasteAsync(string text, PasteTargetSnapshot target, ISelectionPasteHost host)
    {
        if (!host.SetClipboardText(text)) return new(PasteOutcome.Failed, 0);
        var ours = host.ClipboardSequenceNumber;
        for (var i = 0; i < 40 && host.ModifiersAreDown(); i++) await host.DelayAsync(25);
        if (host.ModifiersAreDown()) return new(PasteOutcome.TargetChanged, ours);

        // 最后一次检查后立刻发送，中间不再 await / 等待。
        var now = host.ForegroundTarget();
        var current = host.ClipboardSequenceNumber;
        if (PasteGuard.DecideTarget(target, now, ours, current) != PasteDecision.Paste)
            return new(PasteOutcome.TargetChanged, ours);
        // 以 SendInput 的实际结果为准：按键没进输入队列就不算粘贴成功，译文留在剪贴板上提示手动粘贴
        if (!host.SendPaste()) return new(PasteOutcome.Copied, ours);
        return new(PasteOutcome.Pasted, ours);
    }

    private static readonly WindowsSelectionHost WindowsHost = new();

    private sealed class WindowsSelectionHost : ISelectionPasteHost, ISelectionCopyHost
    {
        public PasteTargetSnapshot ForegroundTarget() => TargetOf(GetForegroundWindow());
        public uint ClipboardSequenceNumber => GetClipboardSequenceNumber();
        public bool ModifiersAreDown() => ModifiersDown();
        public Task DelayAsync(int milliseconds) => Task.Delay(milliseconds);
        public bool SetClipboardText(string text) => SetPrivateText(text);
        public bool SendPaste() => SendChord(0x56);
        public bool SendCopy() => SendChord(0x43);
        public string? ReadText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { return null; }
        }
        public ClipboardBackup Backup() => SelectionGrabber.Backup();
        public void Restore(ClipboardBackup backup) => SelectionGrabber.Restore(backup);
    }

    public static PasteTargetSnapshot TargetOf(IntPtr window) =>
        new(window.ToInt64(), FocusOf(window).ToInt64(), ProcessIdOf(window), DateTime.Now);

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

    /// <summary>
    /// 备份剪贴板。区分三种情况：真的是空的（Empty，之后还原就是清空）；备份到了（Saved）；
    /// 没法可靠备份（Unavailable：被别的程序占着、读出错、或者里面只有存不下来的格式）——这种情况调用方不做临时复制。
    /// </summary>
    private static ClipboardBackup Backup()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            var formats = data?.GetFormats(false) ?? [];
            if (formats.Length == 0)
                return CountClipboardFormats() == 0 ? new(BackupState.Empty) : new(BackupState.Unavailable);
            var keep = new Dictionary<string, object>();
            foreach (var f in Formats)
            {
                if (!data!.GetDataPresent(f)) continue;
                if (data.GetData(f) is not { } v) return new(BackupState.Unavailable);   // 说有却读不出来
                keep[f] = v is StringCollection sc ? sc.Cast<string>().ToArray() : v;
            }
            return keep.Count > 0 ? new(BackupState.Saved, keep) : new(BackupState.Unavailable);   // 有内容但没有一种能存下来
        }
        catch { return new(BackupState.Unavailable); }
    }

    private static void Restore(ClipboardBackup backup)
    {
        if (backup.State == BackupState.Unavailable) return;   // 没备份到就不动（调用方本来也不会走到这里）
        var keep = backup.Data;
        try
        {
            if (backup.State == BackupState.Empty || keep == null || keep.Count == 0) { Clipboard.Clear(); return; }
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

    private static IntPtr FocusOf(IntPtr fg)
    {
        try
        {
            var tid = GetWindowThreadProcessId(fg, out _);
            var info = new GuiThreadInfo { cbSize = Marshal.SizeOf<GuiThreadInfo>() };
            return GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
        }
        catch { return IntPtr.Zero; }
    }

    private static int ProcessIdOf(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(window, out var pid);
            return checked((int)pid);
        }
        catch { return 0; }
    }

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

    private static bool SendChord(ushort vk)
    {
        var inputs = new[]
        {
            Key(0x11, false), Key(vk, false), Key(vk, true), Key(0x11, true),
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return true;
        App.Log($"SendInput 只送进了 {sent}/{inputs.Length} 个按键（错误 {Marshal.GetLastWin32Error()}）");
        return false;
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
    [DllImport("user32.dll")] private static extern int CountClipboardFormats();
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
