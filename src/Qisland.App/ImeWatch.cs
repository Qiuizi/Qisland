using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 输入法中 / 英：问前台窗口的默认 IME 窗口（ImmGetDefaultIMEWnd + WM_IME_CONTROL），
/// 只在键盘布局是中文时显示。微软拼音等 TSF 输入法在部分程序里报得不准，所以默认关，托盘里手动打开。
/// 每 400 ms 问一次，用 SendMessageTimeout（对方卡住 50 ms 就放弃），不会拖住岛。
/// </summary>
public sealed class ImeWatch
{
    [DllImport("imm32.dll")] private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO { public int cbSize, flags; public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret; public int l, t, r, b; }

    public static string OnFlag => Path.Combine(App.DataDir, "ime-on");
    public static bool Enabled { get => File.Exists(OnFlag); set { try { if (value) File.WriteAllText(OnFlag, ""); else File.Delete(OnFlag); } catch { } } }

    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(400) };
    /// <summary>"中" / "英" / ""（不是中文键盘或读不到）。</summary>
    public string State { get; private set; } = "";
    public event Action? Changed;

    public ImeWatch() { _t.Tick += (_, _) => Poll(); }

    public void Apply()
    {
        if (Enabled) { if (!_t.IsEnabled) _t.Start(); Poll(); }
        else { _t.Stop(); Set(""); }
    }

    private void Poll()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) { Set(""); return; }
        var thread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        var lang = (long)GetKeyboardLayout(thread) & 0xFFFF;
        if (lang is not (0x0804 or 0x0404 or 0x0C04)) { Set(""); return; }   // 简体 / 繁体中文键盘才显示
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        var target = GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : fg;
        var ime = ImmGetDefaultIMEWnd(target);
        if (ime == IntPtr.Zero) { Set(""); return; }
        const uint WM_IME_CONTROL = 0x0283, SMTO_ABORTIFHUNG = 0x2;
        if (SendMessageTimeout(ime, WM_IME_CONTROL, 0x5 /*IMC_GETOPENSTATUS*/, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out var open) == IntPtr.Zero) return;
        if (open == IntPtr.Zero) { Set("英"); return; }
        if (SendMessageTimeout(ime, WM_IME_CONTROL, 0x1 /*IMC_GETCONVERSIONMODE*/, IntPtr.Zero, SMTO_ABORTIFHUNG, 50, out var conv) == IntPtr.Zero) return;
        Set(((long)conv & 1) != 0 ? "中" : "英");   // IME_CMODE_NATIVE
    }

    private void Set(string s)
    {
        if (s == State) return;
        State = s;
        Changed?.Invoke();
    }
}
