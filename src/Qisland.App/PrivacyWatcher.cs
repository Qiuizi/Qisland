using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Qisland.App;

/// <summary>正在占用麦克风 / 摄像头的一个程序。</summary>
public sealed record DeviceUse(string Device, string Exe, string App, DateTime Since)
{
    public bool Mic => Device == "microphone";
}

/// <summary>
/// 麦克风 / 摄像头占用（iOS 的橙点、绿点）。
/// Windows 每次有程序开始 / 停止用麦克风或摄像头，都会在
/// HKCU\...\CapabilityAccessManager\ConsentStore\{microphone,webcam} 下写这个程序的 LastUsedTimeStart / LastUsedTimeStop，
/// Stop 为 0 就是正在用。这里用 RegNotifyChangeKeyValue 等这两个键变化，变了才读一遍（去抖 300 毫秒），平时零开销。
/// 只在本机读，不记录用了什么、不上报。
/// </summary>
public sealed class PrivacyWatcher : IDisposable
{
    private const string Base = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private RegWatch? _watch;
    private static readonly DateTime Boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeviceUse> Uses { get; private set; } = [];
    public event Action<IReadOnlyList<DeviceUse>, IReadOnlyList<DeviceUse>>? Changed;   // (现在, 之前)

    public PrivacyWatcher(Dispatcher ui)
    {
        _ui = ui;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Scan(); };
    }

    public void Start()
    {
        if (_watch != null) return;
        _watch = new RegWatch("privacy",
            [(Registry.CurrentUser, Base + @"\microphone", true), (Registry.CurrentUser, Base + @"\webcam", true)],
            () => _ui.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); }));
        Scan();
        App.Log($"麦克风 / 摄像头监听: {(_watch.Active ? "已开始（注册表通知）" : "没挂上")}");
    }

    public void Stop()
    {
        _watch?.Dispose(); _watch = null;
        _debounce.Stop();
        var before = Uses;
        Uses = [];
        if (before.Count > 0) Changed?.Invoke(Uses, before);
    }

    private void Scan()
    {
        Counters.Hit("privacy.scan");
        var list = new List<DeviceUse>();
        foreach (var dev in new[] { "microphone", "webcam" })
        {
            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(Base + "\\" + dev);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    if (sub.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
                    {
                        using var np = root.OpenSubKey(sub);
                        if (np == null) continue;
                        foreach (var app in np.GetSubKeyNames()) Check(np, app, dev, packaged: false, list);
                    }
                    else Check(root, sub, dev, packaged: true, list);
                }
            }
            catch (Exception ex) { App.Log("隐私指示读取: " + ex.Message); }
        }
        var before = Uses;
        var same = before.Count == list.Count && before.All(b => list.Any(x => x.Device == b.Device && x.Exe == b.Exe));
        Uses = list;
        if (!same) Changed?.Invoke(list, before);
    }

    private static void Check(RegistryKey parent, string name, string dev, bool packaged, List<DeviceUse> list)
    {
        using var k = parent.OpenSubKey(name);
        if (k == null) return;
        var start = Qword(k.GetValue("LastUsedTimeStart"));
        var stop = Qword(k.GetValue("LastUsedTimeStop"));
        if (start <= 0 || stop != 0) return;
        DateTime since;
        try { since = DateTime.FromFileTime(start); } catch { return; }
        if (since < Boot) return;                       // 开机前留下的（程序崩了没写 Stop）
        if (Path.GetFileName(name.Replace('#', '\\')).StartsWith("Qisland", StringComparison.OrdinalIgnoreCase)) return;
        if (!packaged)
        {
            var path = name.Replace('#', '\\');
            var exe = Path.GetFileNameWithoutExtension(path);
            if (!Running(exe)) return;                  // 进程已经没了：也是没写 Stop 的残留
            list.Add(new DeviceUse(dev, exe, Display(exe, path), since));
        }
        else
        {
            // Microsoft.WindowsCamera_8wekyb3d8bbwe → WindowsCamera
            var pkg = name.Split('_')[0];
            var shortName = pkg.Contains('.') ? pkg[(pkg.LastIndexOf('.') + 1)..] : pkg;
            list.Add(new DeviceUse(dev, shortName, Display(shortName, null), since));
        }
    }

    private static long Qword(object? v) => v switch { long l => l, int i => i, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), _ => 0 };

    private static bool Running(string exe)
    {
        try
        {
            var ps = Process.GetProcessesByName(exe);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }
        catch { return true; }
    }

    /// <summary>程序的显示名：常用的直接给中文名，其他取 exe 的文件说明。</summary>
    public static string Display(string exe, string? path)
    {
        var known = exe.ToLowerInvariant() switch
        {
            "weixin" or "wechat" => "微信",
            "qq" => "QQ",
            "wemeetapp" or "wemeet" => "腾讯会议",
            "dingtalk" => "钉钉",
            "feishu" => "飞书",
            "lark" => "Lark",
            "ms-teams" or "teams" => "Teams",
            "zoom" => "Zoom",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "obs64" or "obs32" => "OBS",
            "discord" => "Discord",
            "windowscamera" => "相机",
            "soundrecorder" or "windowssoundrecorder" => "录音机",
            "imeservice" => "豆包输入法",
            _ => null,
        };
        if (known != null) return known;
        lock (NameCache)
        {
            if (NameCache.TryGetValue(exe, out var n)) return n;
            n = exe;
            try
            {
                if (path != null && File.Exists(path))
                {
                    var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(d) && d.Length <= 24) n = d;
                }
            }
            catch { }
            return NameCache[exe] = n;
        }
    }

    public void Dispose() => Stop();
}
