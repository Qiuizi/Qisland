using System.Runtime.InteropServices;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 专注联动：自己的「专注」计时在走，或者 Windows 11 的专注会话（时钟 → 专注）开着时，
/// 收起的胶囊只放行：等你批准的、通话 / 来电、1 小时内截止的作业，以及计时本身和麦克风 / 摄像头指示；
/// 其余的照样进展开后的列表，只是不上胶囊、不弹出来（专注结束时攒成一条摘要）。
/// </summary>
public static class FocusMode
{
    public static Func<bool>? Own;
    public static bool System { get; private set; }
    public static bool Active => Features.On(Features.Focus) && (Own?.Invoke() == true || System);

    public static bool Passes(LiveActivity a) =>
        a.Kind == ActivityKind.WaitingApproval
        || a.Dispatch is "call" or "meeting" or "timer" or "mic" or "camera"
        || a.Id == "hw:alert" && a.Kind == ActivityKind.Error;

    // ───── Windows 11 专注会话：Windows.UI.Shell.FocusSessionManager（22H2 起）─────
    // 项目引用的 Windows SDK 投影是 19041，没有这个类型：直接走 WinRT 激活工厂 + vtable 调用，只读 IsFocusActive。
    private static readonly Guid IidStatics = new("834df764-cb9a-5d0a-aa9f-73df4f249395");
    private static IntPtr _mgr;
    private static GetBool? _isActive;
    private static bool _tried, _supported;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetPtr(IntPtr self, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBool(IntPtr self, [MarshalAs(UnmanagedType.U1)] out bool value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr cls, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string s, int len, out IntPtr h);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr h);

    private static T Slot<T>(IntPtr obj, int i) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), i * IntPtr.Size));

    private static void Init()
    {
        _tried = true;
        if (Environment.OSVersion.Version.Build < 22621) { App.Log("系统专注: 系统版本不支持"); return; }
        IntPtr hs = IntPtr.Zero, statics = IntPtr.Zero;
        try
        {
            const string cls = "Windows.UI.Shell.FocusSessionManager";
            WindowsCreateString(cls, cls.Length, out hs);
            var iid = IidStatics;
            var hr = RoGetActivationFactory(hs, ref iid, out statics);
            if (hr != 0 || statics == IntPtr.Zero) { App.Log($"系统专注: 拿不到 FocusSessionManager（0x{hr:X8}）"); return; }
            // IInspectable 占 0–5，IFocusSessionManagerStatics: 6 GetDefault, 7 get_IsSupported
            if (Slot<GetBool>(statics, 7)(statics, out var sup) != 0 || !sup) { App.Log("系统专注: 这台电脑不支持专注会话"); return; }
            if (Slot<GetPtr>(statics, 6)(statics, out _mgr) != 0 || _mgr == IntPtr.Zero) { App.Log("系统专注: GetDefault 失败"); return; }
            _isActive = Slot<GetBool>(_mgr, 6);   // IFocusSessionManager: 6 get_IsFocusActive
            _supported = true;
            App.Log("系统专注: 已接上 Windows 专注会话");
        }
        catch (Exception ex) { App.Log("系统专注: " + ex.Message); }
        finally
        {
            if (statics != IntPtr.Zero) Marshal.Release(statics);
            if (hs != IntPtr.Zero) WindowsDeleteString(hs);
        }
    }

    /// <summary>每 10 秒调一次（一次 COM 调用，微秒级）。返回是否变化。</summary>
    public static bool Poll()
    {
        if (!Features.On(Features.Focus)) { var was = System; System = false; return was; }
        if (!_tried) Init();
        if (!_supported) return false;
        try
        {
            if (_isActive!(_mgr, out var on) != 0) return false;
            Counters.Hit("focus.poll");
            if (on == System) return false;
            System = on;
            App.Log($"系统专注: {(on ? "开始" : "结束")}");
            return true;
        }
        catch (Exception ex) { App.Log("系统专注: " + ex.Message); _supported = false; return false; }
    }
}
