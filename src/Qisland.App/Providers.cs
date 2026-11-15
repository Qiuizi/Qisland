using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Qisland.App;

/// <summary>
/// 系统通知镜像：微信 / QQ / DeepSeek Harness 弹出的 Windows 通知。
/// 只读通知中心里本来就显示的标题和预览，不读聊天数据库。通知被清掉（你已经看过）时岛上的那条也跟着消失。
/// </summary>
public sealed class ToastMirror
{
    private readonly ActivityStore _store;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<uint, string> _toastToActivity = new();
    private readonly Dictionary<string, int> _counts = new();
    private UserNotificationListener? _listener;
    private bool _primed, _busy;

    public ToastMirror(ActivityStore store) { _store = store; _timer.Tick += async (_, _) => await Poll(); }

    public async Task<bool> StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            App.Log($"通知读取权限: {access}");
            if (access != UserNotificationListenerAccessStatus.Allowed) return false;
            _timer.Start();
            await Poll();
            return true;
        }
        catch (Exception ex) { App.Log("通知监听启动失败: " + ex.Message); return false; }
    }

    private async Task Poll()
    {
        if (_listener is null || _busy) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var alive = new HashSet<uint>();
            foreach (var n in list)
            {
                alive.Add(n.Id);
                if (_toastToActivity.ContainsKey(n.Id)) continue;
                string appName = "", aumid = "";
                try { appName = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; aumid = n.AppInfo?.AppUserModelId ?? ""; } catch { }
                var app = NotificationMirror.Detect(appName, aumid);
                if (app is null) { _toastToActivity[n.Id] = ""; continue; }
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
                    ?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                var title = texts.Count > 0 ? texts[0] : "";
                var body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "";
                if (!_primed) { _toastToActivity[n.Id] = ""; continue; } // 启动前就在的通知不当新消息
                _toastToActivity[n.Id] = Push(app.Value, title, body);
            }
            // 通知中心里被清掉的通知：说明你已经看过，岛上同步移除
            foreach (var gone in _toastToActivity.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                var act = _toastToActivity[gone];
                _toastToActivity.Remove(gone);
                if (act.Length > 0 && !_toastToActivity.Values.Contains(act)) { _store.Remove(act); _counts.Remove(act); }
            }
            _primed = true;
        }
        catch (Exception ex) { App.Log("通知轮询: " + ex.Message); }
        finally { _busy = false; }
    }

    private string Push(ChatApp app, string title, string body)
    {
        if (app == ChatApp.DeepSeekHarness)
        {
            var a = DeepSeekHarnessSignals.FromToast(title, body);
            if (a is null) return "";
            _store.Upsert(a);
            return a.Id;
        }
        var d = NotificationMirror.Present(app, title, body, new NotificationPolicy(true, true, true, false));
        if (!d.Show) return "";
        var source = app == ChatApp.WeChat ? "wechat" : "qq";
        var id = source + ":" + d.Title;
        var n = _counts[id] = _counts.GetValueOrDefault(id) + 1;
        _store.Upsert(new IslandActivity(id, source, d.Title, n > 1 ? $"[{n} 条] {d.Body}" : d.Body,
            ActivityKind.Info, 75, DurationSeconds: 15 * 60, ResetTtl: true));
        MessengerFlash.ClearFlash(source, _store); // 有正文的通知比“任务栏在闪”更具体
        return id;
    }
}

/// <summary>微信电脑版常常不发系统通知，只闪任务栏。监听 ShellHook 的闪烁事件；你切到微信窗口时自动清掉。</summary>
public sealed class MessengerFlash : IDisposable
{
    private const int HSHELL_FLASH = 0x8006;
    [DllImport("user32.dll")] private static extern bool RegisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DeregisterShellHookWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string s);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private readonly ActivityStore _store;
    private HwndSource? _src;
    private int _msg;
    private readonly Dictionary<string, uint> _flashing = new();

    public MessengerFlash(ActivityStore store) { _store = store; }

    public void Start()
    {
        _src = new HwndSource(new HwndSourceParameters("QislandShellHook") { Width = 0, Height = 0, WindowStyle = 0 });
        _src.AddHook(WndProc);
        _msg = RegisterWindowMessage("SHELLHOOK");
        if (!RegisterShellHookWindow(_src.Handle)) App.Log("ShellHook 注册失败");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == _msg && w.ToInt32() == HSHELL_FLASH) OnFlash(l);
        return IntPtr.Zero;
    }

    private void OnFlash(IntPtr win)
    {
        GetWindowThreadProcessId(win, out var pid);
        if (pid == 0) return;
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return; }
        var source = name switch { "weixin" or "wechat" => "wechat", "qq" => "qq", _ => null };
        if (source is null) return;
        _flashing[source] = pid;
        // 已经有带正文的通知就不再叠一条“有新消息”
        if (_store.Ranked().Any(a => a.Source == source && !a.Id.EndsWith(":flash", StringComparison.Ordinal))) return;
        _store.Upsert(new IslandActivity(source + ":flash", source, source == "wechat" ? "微信有新消息" : "QQ 有新消息",
            "点一下打开", ActivityKind.Info, 74, DurationSeconds: 30 * 60, ResetTtl: false));
    }

    /// <summary>每秒调用：前台切到了微信/QQ，就当你已经看了。</summary>
    public void OnForeground(IntPtr fg)
    {
        if (_flashing.Count == 0 || fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out var pid);
        foreach (var (source, p) in _flashing.ToList())
        {
            if (p != pid) continue;
            _flashing.Remove(source);
            foreach (var a in _store.Ranked().Where(a => a.Source == source).ToList()) _store.Remove(a.Id);
        }
    }

    public static void ClearFlash(string source, ActivityStore store) => store.Remove(source + ":flash");

    public void Dispose()
    {
        if (_src is null) return;
        DeregisterShellHookWindow(_src.Handle);
        _src.Dispose();
    }
}

/// <summary>DeepSeek Harness 窗口标题里出现“等待确认 / 出错 / 完成 / 正在”时上岛；平常开着不打扰。</summary>
public sealed class HarnessWatcher
{
    private readonly ActivityStore _store;
    private string _lastTitle = "\0";
    public HarnessWatcher(ActivityStore store) { _store = store; }

    public void Tick()
    {
        string? title = null;
        var running = false;
        foreach (var name in new[] { "DeepSeek Harness", "DeepSeekHarness" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                running = true;
                try { if (!string.IsNullOrWhiteSpace(p.MainWindowTitle)) title = p.MainWindowTitle; } catch { }
                p.Dispose();
            }
            if (running) break;
        }
        var key = running ? title ?? "" : "\u0001";
        if (key == _lastTitle) return;
        _lastTitle = key;
        const string id = "agent:deepseek-harness:window";
        var a = DeepSeekHarnessSignals.FromWindow(running, title);
        if (a is null || DeepSeekHarnessSignals.ClassifyText(title) == ActivityKind.Info) { _store.Remove(id); return; }
        _store.Upsert(a);
    }
}

/// <summary>信息监控站：截止 3 天内的常驻；新出现的高分资讯和每日简报只提醒一次。</summary>
public sealed class InfoStationWatcher
{
    private readonly ActivityStore _store;
    private readonly HttpClient _http;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _primed;
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";

    public InfoStationWatcher(ActivityStore store, HttpClient http) { _store = store; _http = http; }

    public async Task Loop()
    {
        while (true)
        {
            try { await Once(); }
            catch (HttpRequestException) { /* 信息站没开 */ }
            catch (TaskCanceledException) { }
            catch (Exception ex) { App.Log("信息站: " + ex.Message); }
            await Task.Delay(TimeSpan.FromMinutes(2));
        }
    }

    private async Task Once()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var deadlines = await _http.GetStringAsync(BaseUrl + "/api/deadlines?horizon=7", cts.Token);
        var dl = InfoStationProvider.ReadDeadlines(deadlines)
            .Where(a => a.Priority >= 72) // 3 天内
            .Select(a => a with { Kind = a.Priority >= 84 ? ActivityKind.Warning : ActivityKind.Info, Priority = Math.Min(a.Priority, 70) })
            .ToList();
        _store.ReplaceSource("info-deadline", dl.Select(a => a with { Source = "info-deadline" }), sticky: true);

        var items = await _http.GetStringAsync(BaseUrl + "/api/items?view=featured&sort=score&read=unread&limit=8", cts.Token);
        foreach (var a in InfoStationProvider.ReadItems(items, 80))
        {
            if (!_seen.Add(a.Id) || !_primed) continue;
            _store.Upsert(a with { DurationSeconds = 10 * 60 });
        }

        var brief = await _http.GetStringAsync(BaseUrl + "/api/briefs/today?refresh=false", cts.Token);
        if (InfoStationProvider.ReadBrief(brief, DateOnly.FromDateTime(DateTime.Now)) is { } b && _seen.Add(b.Id) && _primed)
            _store.Upsert(b with { DurationSeconds = 10 * 60 });
        _primed = true;
    }
}

/// <summary>倒计时 / 番茄钟。常驻一条进度活动，每秒更新剩余时间。</summary>
public sealed class FocusTimer
{
    private const string Id = "timer:focus";
    private readonly ActivityStore _store;
    private DateTime _end, _start;
    private string _label = "";
    public bool Active { get; private set; }
    public FocusTimer(ActivityStore store) { _store = store; }

    public void Start(int minutes, string label)
    {
        _start = DateTime.Now;
        _end = _start.AddMinutes(minutes);
        _label = label;
        Active = true;
        Tick();
    }

    public void Cancel() { Active = false; _store.Remove(Id); }
    public TimeSpan Remaining => Active ? _end - DateTime.Now : TimeSpan.Zero;
    public TimeSpan Total => _end - _start;
    public string Label => _label;

    public void Tick()
    {
        if (!Active) return;
        var left = _end - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            Active = false;
            _store.Remove(Id);
            _store.Upsert(new IslandActivity("timer:done", "timer", _label + "结束", "休息一下", ActivityKind.Done, 95, DurationSeconds: 60, ResetTtl: true));
            System.Media.SystemSounds.Asterisk.Play();
            return;
        }
        var total = (_end - _start).TotalSeconds;
        var p = 1 - left.TotalSeconds / total;
        var text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        _store.Upsert(new IslandActivity(Id, "timer", $"{_label} · {text}", $"{_end:HH:mm} 结束", ActivityKind.Running, 58), sticky: true, progress: p);
    }
}

/// <summary>待办：%APPDATA%\Qisland\todo.md，一行一条 “- [ ] 事项”，勾成 “- [x]” 就算完成。用记事本就能改。</summary>
public sealed class TodoFile : IDisposable
{
    private readonly ActivityStore _store;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public static string PathName => System.IO.Path.Combine(App.DataDir, "todo.md");
    public IReadOnlyList<string> Open { get; private set; } = [];
    public event Action? Changed;

    public TodoFile(ActivityStore store)
    {
        _store = store;
        if (!File.Exists(PathName))
            File.WriteAllText(PathName, "# 待办（一行一条，完成后把 [ ] 改成 [x]）\n- [ ] 在这里写下第一件事\n");
        _watcher = new FileSystemWatcher(App.DataDir, "todo.md") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Created += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Renamed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(); };
        Load();
    }

    private void Kick() { _debounce.Stop(); _debounce.Start(); }

    private void Load()
    {
        try
        {
            Open = File.ReadAllLines(PathName)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("- [ ]", StringComparison.Ordinal) || l.StartsWith("* [ ]", StringComparison.Ordinal))
                .Select(l => l[5..].Trim())
                .Where(l => l.Length > 0 && l != "在这里写下第一件事")
                .ToList();
            Changed?.Invoke();
        }
        catch (IOException) { Kick(); }
    }

    public void Dispose() => _watcher.Dispose();
}
