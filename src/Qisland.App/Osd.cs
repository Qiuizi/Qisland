using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Qisland.App;

/// <summary>
/// 系统音量：Core Audio 的 IAudioEndpointVolume。音量变化（不管谁改的）都会回调 Changed；
/// 开启「接管音量键」时用低级键盘钩子吃掉音量键、自己改音量，这样 Windows 原生的音量弹窗就不会出现。
/// </summary>
public sealed class VolumeWatcher : IDisposable
{
    public event Action<float, bool>? Changed;   // (0..1, 静音)
    /// <summary>默认播放设备换了（插上耳机 / 连上蓝牙）：设备名。</summary>
    public event Action<string>? DeviceChanged;
    public string DeviceName { get; private set; } = "";
    public float Level { get; private set; }
    public bool Muted { get; private set; }

    private readonly Dispatcher _ui;
    private IMMDeviceEnumerator? _enum;
    private IAudioEndpointVolume? _vol;
    private readonly VolumeCallback _cb;
    private readonly DeviceCallback _devCb;
    private Guid _ctx = Guid.NewGuid();
    private IntPtr _hook;
    private LowLevelKeyboardProc? _proc;
    private DateTime _quietUntil = DateTime.Now.AddSeconds(2);

    public static string OffFlag => Path.Combine(App.DataDir, "volumekeys.off");
    public bool KeysHooked => _hook != IntPtr.Zero;

    public VolumeWatcher()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _cb = new VolumeCallback(this);
        _devCb = new DeviceCallback(this);
        try
        {
            _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            _enum.RegisterEndpointNotificationCallback(_devCb);
            Bind();
        }
        catch (Exception ex) { App.Log("音量监听初始化失败: " + ex.Message); }
        if (!File.Exists(OffFlag)) HookKeys(true);
    }

    private void Bind()
    {
        try
        {
            if (_vol != null) { try { _vol.UnregisterControlChangeNotify(_cb); } catch { } Marshal.ReleaseComObject(_vol); _vol = null; }
            _enum!.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var dev);
            var iid = typeof(IAudioEndpointVolume).GUID;
            dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var o);
            DeviceName = FriendlyName(dev);
            Marshal.ReleaseComObject(dev);
            _vol = (IAudioEndpointVolume)o;
            _vol.GetMasterVolumeLevelScalar(out var l); _vol.GetMute(out var m);
            Level = l; Muted = m;
            _vol.RegisterControlChangeNotify(_cb);
        }
        catch (Exception ex) { _vol = null; App.Log("绑定默认音频设备失败: " + ex.Message); }
    }

    internal void OnNotify(float level, bool muted)
    {
        _ui.BeginInvoke(() =>
        {
            var changed = Math.Abs(level - Level) > 0.001 || muted != Muted;
            Level = level; Muted = muted;
            if (changed && DateTime.Now > _quietUntil) Changed?.Invoke(level, muted);
        });
    }

    internal void OnDefaultChanged() => _ui.BeginInvoke(() =>
    {
        var before = DeviceName;
        _quietUntil = DateTime.Now.AddSeconds(1);
        Bind();
        if (DeviceName.Length > 0 && DeviceName != before && DateTime.Now - _lastDevice > TimeSpan.FromSeconds(1.5))
        {
            _lastDevice = DateTime.Now;
            DeviceChanged?.Invoke(DeviceName);
        }
    });
    private DateTime _lastDevice = DateTime.MinValue;

    private static string FriendlyName(IMMDevice dev)
    {
        try
        {
            dev.OpenPropertyStore(0, out var store);
            var key = new PropertyKey { Fmt = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 };
            var pv = new PropVariant();
            store.GetValue(ref key, ref pv);
            var s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "" : "";
            PropVariantClear(ref pv);
            Marshal.ReleaseComObject(store);
            return s;
        }
        catch { return ""; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint n);
        int GetAt(uint i, out PropertyKey key);
        int GetValue(ref PropertyKey key, ref PropVariant pv);
    }

    public void Step(int dir)
    {
        if (_vol == null) Bind();
        if (_vol == null) return;
        try
        {
            _vol.GetMasterVolumeLevelScalar(out var l);
            var n = Math.Clamp(MathF.Round((l + dir * 0.02f) * 50) / 50, 0f, 1f);
            if (Muted) _vol.SetMute(false, ref _ctx);
            _vol.SetMasterVolumeLevelScalar(n, ref _ctx);
            if (Math.Abs(n - l) < 0.001) Changed?.Invoke(n, false);   // 已到 0 / 100 也给个反馈
        }
        catch (Exception ex) { App.Log("调音量失败: " + ex.Message); }
    }

    public void ToggleMute()
    {
        if (_vol == null) return;
        try { _vol.GetMute(out var m); _vol.SetMute(!m, ref _ctx); } catch { }
    }

    public void HookKeys(bool on)
    {
        if (on && _hook == IntPtr.Zero)
        {
            _proc = KeyProc;
            using var mod = Process.GetCurrentProcess().MainModule!;
            _hook = SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, _proc, GetModuleHandle(mod.ModuleName), 0);
            App.Log(_hook != IntPtr.Zero ? "已接管音量键" : "音量键钩子失败");
        }
        else if (!on && _hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        try { if (on) File.Delete(OffFlag); else File.WriteAllText(OffFlag, ""); } catch { }
    }

    private IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            if (vk is 0xAD or 0xAE or 0xAF)
            {
                var down = (int)wParam is 0x100 or 0x104;
                if (down) _ui.BeginInvoke(() => { if (vk == 0xAD) ToggleMute(); else Step(vk == 0xAF ? 1 : -1); });
                return 1;   // 吃掉：系统就不会弹自己的音量浮窗
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        try { if (_vol != null) _vol.UnregisterControlChangeNotify(_cb); } catch { }
        try { _enum?.UnregisterEndpointNotificationCallback(_devCb); } catch { }
    }

    // ---- COM ----
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore store);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float db, ref Guid ctx);
        int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        int GetMasterVolumeLevel(out float db);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        int GetChannelVolumeLevel(uint ch, out float db);
        int GetChannelVolumeLevelScalar(uint ch, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

    [ComImport, Guid("7991EEC9-7C89-41F3-A8C2-4DDE7C6F8F2C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [StructLayout(LayoutKind.Sequential)] internal struct PropertyKey { public Guid Fmt; public int Pid; }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly VolumeWatcher _w;
        public VolumeCallback(VolumeWatcher w) { _w = w; }
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: Guid(16) BOOL bMuted(4) float fMasterVolume(4) ...
            var muted = Marshal.ReadInt32(data, 16) != 0;
            var level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            _w.OnNotify(level, muted);
            return 0;
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly VolumeWatcher _w;
        public DeviceCallback(VolumeWatcher w) { _w = w; }
        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == 0 && role == 1) _w.OnDefaultChanged(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
}

/// <summary>
/// 屏幕亮度：订阅电源设置 GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS，亮度一变窗口就收到 WM_POWERBROADCAST。
/// 笔记本的亮度键由固件/驱动处理，没法吃掉，所以系统的亮度浮窗仍会出现。
/// </summary>
public static class BrightnessWatcher
{
    public static readonly Guid Setting = new("8ffee2c6-2d01-46be-adb9-398addc5b4ff");
    [DllImport("user32.dll")] private static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
    public static void Register(IntPtr hwnd) { var g = Setting; RegisterPowerSettingNotification(hwnd, ref g, 0); }

    /// <summary>WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE 的 lParam → 亮度 0..100，不是亮度就返回 -1。</summary>
    public static int Parse(IntPtr wParam, IntPtr lParam)
    {
        if ((int)wParam != 0x8013 || lParam == IntPtr.Zero) return -1;
        var g = Marshal.PtrToStructure<Guid>(lParam);
        if (g != Setting) return -1;
        return Marshal.ReadInt32(lParam, 20);   // POWERBROADCAST_SETTING: Guid(16) DataLength(4) Data
    }
}
