using System.Runtime.InteropServices;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 通话 / 会议（iOS 的通话胶囊）。两个便宜的信号：
/// · 主信号：麦克风被微信 / QQ / 腾讯会议 / 钉钉 / 飞书 / Teams / Zoom 占着超过 3 秒（来自隐私监听的注册表通知，零轮询），
///   开始时间直接用系统记下的 LastUsedTimeStart，时长是准的；按住说话发语音不到 3 秒，不算。
/// · 辅信号：每 2 秒（性能护栏降级时 6 秒）枚举一遍顶层窗口，只看这些进程的窗口标题，认「邀请你语音通话」这类来电窗口，
///   也顺便记下通话 / 会议窗口，点胶囊时切过去。
/// 只在本机看窗口标题，不读聊天内容、不记录对方是谁（日志里只写软件名）。
/// </summary>
public sealed class CallWatcher
{
    private const string Id = "call";
    private static readonly HashSet<string> ImApps = new(StringComparer.OrdinalIgnoreCase) { "weixin", "wechat", "qq" };
    private static readonly HashSet<string> MeetingApps = new(StringComparer.OrdinalIgnoreCase) { "wemeetapp", "wemeet", "dingtalk", "feishu", "lark", "ms-teams", "teams", "zoom" };
    private static readonly string[] RingWords = ["邀请你", "来电", "正在呼叫你", "invites you", "incoming call"];
    private static readonly string[] CallWords = ["语音通话", "视频通话", "通话中", "正在呼叫", "等待对方接受", "等待对方接听", "voice call", "video call"];

    private readonly ActivityStore _store;
    private DeviceUse? _mic;               // 通话类软件正在占用的麦克风
    private IntPtr _callWnd, _ringWnd;
    private string _ringApp = "";
    private bool _shown;
    private readonly Dictionary<uint, string> _exe = [];
    private DateTime _exeReset = DateTime.Now;

    public CallWatcher(ActivityStore store) { _store = store; }

    public bool InCall => _shown && _mic != null;

    public static bool IsCallApp(string exe) => ImApps.Contains(exe) || MeetingApps.Contains(exe);

    /// <summary>隐私监听变化时调用。</summary>
    public void OnDevices(IReadOnlyList<DeviceUse> uses)
    {
        var mic = uses.Where(u => u.Mic && IsCallApp(u.Exe)).OrderBy(u => u.Since).FirstOrDefault();
        if (mic?.Exe == _mic?.Exe && mic?.Since == _mic?.Since) return;
        _mic = mic;
        if (mic == null) End();
    }

    /// <summary>每秒调一次。</summary>
    public void Tick(int n)
    {
        if (!Features.On(Features.Calls)) { if (_shown || _store.Get(Id) != null) { _shown = false; _store.Remove(Id); } return; }
        var every = PerfGuard.Degraded ? 6 : 2;
        if (n % every == 0) Scan();
        var now = DateTime.Now;
        if (_mic is { } m && now - m.Since >= TimeSpan.FromSeconds(3))
        {
            var meeting = MeetingApps.Contains(m.Exe);
            var len = now - m.Since;
            var text = len.TotalHours >= 1 ? len.ToString(@"h\:mm\:ss") : len.ToString(@"mm\:ss");
            if (!_shown) App.Log($"通话: {m.App} 开始{(meeting ? "开会" : "通话")}");
            _shown = true;
            _ringWnd = IntPtr.Zero;
            _store.Upsert(new IslandActivity(Id, meeting ? "meeting" : "call", $"{(meeting ? "会议中" : "通话中")} · {text}", m.App, ActivityKind.Running, 86), sticky: true);
        }
        else if (_mic == null && _ringWnd != IntPtr.Zero)
        {
            if (!_shown) App.Log($"通话: {_ringApp} 来电");
            _shown = true;
            _store.Upsert(new IslandActivity(Id, "call", $"{_ringApp}来电", "点这里切过去接听", ActivityKind.Warning, 95), sticky: true);
        }
        else if (_shown && _mic == null) End();
    }

    private void End()
    {
        if (!_shown) return;
        _shown = false;
        _callWnd = IntPtr.Zero;
        _store.Remove(Id);
        App.Log("通话: 结束");
    }

    /// <summary>点胶囊：切到通话 / 来电窗口；找不到就切到这个软件。</summary>
    public bool Open(LiveActivity a)
    {
        if (a.Id != Id) return false;
        var h = _ringWnd != IntPtr.Zero && IsWindow(_ringWnd) ? _ringWnd : _callWnd != IntPtr.Zero && IsWindow(_callWnd) ? _callWnd : IntPtr.Zero;
        if (h != IntPtr.Zero) { Launcher.UnlockForeground(); var ok = Launcher.ForceForeground(h); App.Log($"通话: 切到通话窗口 {(ok ? "成功" : "失败")}"); if (ok) return true; }
        var src = _mic?.Exe.ToLowerInvariant() switch { "weixin" or "wechat" => "wechat", "qq" => "qq", _ => null };
        return src != null && Launcher.Open(src);
    }

    private void Scan()
    {
        Counters.Hit("call.scan");
        if (DateTime.Now - _exeReset > TimeSpan.FromMinutes(10)) { _exe.Clear(); _exeReset = DateTime.Now; }
        IntPtr ring = IntPtr.Zero, call = IntPtr.Zero, big = IntPtr.Zero;
        var ringApp = "";
        long bigArea = 0;
        var micExe = _mic?.Exe;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            var exe = ExeOf(pid);
            if (exe.Length == 0 || !IsCallApp(exe)) return true;
            var title = Title(h);
            if (title.Length == 0) return true;
            if (ImApps.Contains(exe) && ring == IntPtr.Zero && RingWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                ring = h; ringApp = PrivacyWatcher.Display(exe, null);
            }
            if (micExe != null && exe.Equals(micExe, StringComparison.OrdinalIgnoreCase))
            {
                if (call == IntPtr.Zero && CallWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase))) call = h;
                if (GetWindowRect(h, out var r)) { var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top); if (area > bigArea) { bigArea = area; big = h; } }
            }
            return true;
        }, IntPtr.Zero);
        if (ring != _ringWnd && ring != IntPtr.Zero) Counters.Hit("call.ring");
        _ringWnd = ring; _ringApp = ringApp;
        _callWnd = call != IntPtr.Zero ? call : big;
    }

    private string ExeOf(uint pid)
    {
        if (_exe.TryGetValue(pid, out var n)) return n;
        n = "";
        var h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(520); var len = sb.Capacity;
                if (QueryFullProcessImageName(h, 0, sb, ref len)) n = System.IO.Path.GetFileNameWithoutExtension(sb.ToString());
            }
            finally { CloseHandle(h); }
        }
        return _exe[pid] = n;
    }

    // 别的进程的窗口：GetWindowText 直接读系统里存的标题，不给对方发消息，对方卡死也不会把岛拖住
    private readonly StringBuilder _sb = new(256);
    private string Title(IntPtr h)
    {
        _sb.Clear();
        return GetWindowText(h, _sb, _sb.Capacity) > 0 ? _sb.ToString() : "";
    }

    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int len);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
