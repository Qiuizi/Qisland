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

    public ToastMirror(ActivityStore store) { _store = store; Current = this; _timer.Tick += async (_, _) => await Poll(); }
    public static ToastMirror? Current { get; private set; }

    /// <summary>这条活动对应的系统通知还在通知中心里吗（在的话可以去通知中心点原通知，让软件自己跳到那个会话）。</summary>
    public bool Alive(string activityId) => activityId.Length > 0 && _toastToActivity.ContainsValue(activityId);

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
                if (!_primed) { _toastToActivity[n.Id] = ""; continue; } // 启动前就在的通知不当新消息
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
                    ?.Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                var title = texts.Count > 0 ? texts[0] : "";
                var body = texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "";
                PushOtp(n.Id, appName, title + " " + body);
                if (app is null) { _toastToActivity[n.Id] = ""; continue; }
                _toastToActivity[n.Id] = Push(app.Value, title, body, aumid);
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

    /// <summary>
    /// 通知里带验证码：岛上单独弹一条「验证码 123456」，点「复制」。验证码只在内存里，
    /// 不写日志、不进剪贴板历史，3 分钟后自己消失。
    /// </summary>
    private void PushOtp(uint toastId, string appName, string text)
    {
        if (!OtpEnabled || Otp.Extract(text) is not { } code) return;
        var id = "otp:" + toastId;
        _store.Upsert(new IslandActivity(id, "otp", "验证码 " + code, appName.Length > 0 ? appName + " · 点一下复制" : "点一下复制",
            ActivityKind.Info, 86, "\uE8C8", DurationSeconds: 180, ResetTtl: true));
        App.Log("验证码通知: 已上岛（内容不记录）");
    }

    public static string OtpOffFlag => Path.Combine(App.DataDir, "otp-off");
    public static bool OtpEnabled { get => !File.Exists(OtpOffFlag); set { try { if (value) File.Delete(OtpOffFlag); else File.WriteAllText(OtpOffFlag, ""); } catch { } } }

    // aumid：发通知的应用，点这一条时按它找窗口 / 激活应用（AppJump）
    private string Push(ChatApp app, string title, string body, string aumid = "")
    {
        var appId = string.IsNullOrWhiteSpace(aumid) ? null : aumid;
        if (app == ChatApp.DeepSeekHarness)
        {
            var a = DeepSeekHarnessSignals.FromToast(title, body);
            if (a is null) return "";
            _store.Upsert(a with { AppId = a.AppId ?? appId });
            return a.Id;
        }
        if (AgentToasts.Source(app) != null)
        {
            var g = AgentToasts.FromToast(app, title, body);
            if (g is null) return "";
            _store.Upsert(g with { AppId = g.AppId ?? appId });
            return g.Id;
        }
        var d = NotificationMirror.Present(app, title, body, new NotificationPolicy(true, true, true, false));
        if (!d.Show) return "";
        var source = app == ChatApp.WeChat ? "wechat" : "qq";
        var id = source + ":" + d.Title;
        var n = _counts[id] = _counts.GetValueOrDefault(id) + 1;
        _store.Upsert(new IslandActivity(id, source, d.Title, n > 1 ? $"[{n} 条] {d.Body}" : d.Body,
            ActivityKind.Info, 75, DurationSeconds: 15 * 60, ResetTtl: true, AppId: appId));
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
    private static string BriefFile => Path.Combine(App.DataDir, "infostation-brief.txt");
    private static string BriefShown() { try { return File.ReadAllText(BriefFile).Trim(); } catch { return ""; } }

    public async Task Loop()
    {
        while (true)
        {
            try { await Once(); }
            catch (HttpRequestException) { /* 信息站没开 */ }
            catch (TaskCanceledException) { }
            catch (Exception ex) { App.Log("信息站: " + ex.Message); }
            await App.Nap(TimeSpan.FromMinutes(2));
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
        _store.ReplaceSource("info-deadline", dl.Select(a => a with { Source = "info-deadline" }), sticky: true, quiet: true); // 几天后的截止不占胶囊，展开能看到

        var items = await _http.GetStringAsync(BaseUrl + "/api/items?view=featured&sort=score&read=unread&limit=8", cts.Token);
        foreach (var a in InfoStationProvider.ReadItems(items, 80))
        {
            if (!_seen.Add(a.Id) || !_primed) continue;
            _store.Upsert(a with { DurationSeconds = 10 * 60 });
        }

        var brief = await _http.GetStringAsync(BaseUrl + "/api/briefs/today?refresh=false", cts.Token);
        // 简报每天只提醒一次，但不要求“在 Qisland 运行期间才生成”：早上 0 点生成、9 点开机也要提醒。
        // 已提醒过的日期记在文件里，重启不会重复弹。
        if (InfoStationProvider.ReadBrief(brief, DateOnly.FromDateTime(DateTime.Now)) is { } b && _seen.Add(b.Id) && BriefShown() != b.Id)
        {
            _store.Upsert(b with { DurationSeconds = 10 * 60 });
            try { File.WriteAllText(BriefFile, b.Id); } catch { }
        }
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

    /// <summary>正在专注的待办（可为空）。</summary>
    public string? Task { get; private set; }
    /// <summary>计时结束：(标签, 待办)。</summary>
    public event Action<string, string?>? Finished;

    public void Start(int minutes, string label, string? task = null)
    {
        _start = DateTime.Now;
        _end = _start.AddMinutes(minutes);
        _label = label;
        Task = task;
        Active = true;
        Tick();
    }

    public void Cancel() { Active = false; Paused = false; Task = null; _store.Remove(Id); }
    public TimeSpan Remaining => !Active ? TimeSpan.Zero : Paused ? _pausedLeft : _end - DateTime.Now;
    public bool Paused { get; private set; }
    private TimeSpan _pausedLeft;

    public void Pause()
    {
        if (!Active || Paused) return;
        _pausedLeft = _end - DateTime.Now;
        Paused = true;
        var text = _pausedLeft.TotalHours >= 1 ? _pausedLeft.ToString(@"h\:mm\:ss") : _pausedLeft.ToString(@"mm\:ss");
        _store.Upsert(new IslandActivity(Id, "timer", $"{Task ?? _label} · 已暂停", $"还剩 {text}", ActivityKind.Info, 50), sticky: true, progress: 1 - _pausedLeft.TotalSeconds / Math.Max(1, Total.TotalSeconds));
    }

    public void Resume()
    {
        if (!Active || !Paused) return;
        var now = DateTime.Now;
        _start += now - (_end - _pausedLeft);   // 暂停的这段不算进总时长
        _end = now + _pausedLeft;
        Paused = false;
        Tick();
    }

    /// <summary>再加几分钟（暂停中也可以）。</summary>
    public void Extend(int minutes)
    {
        if (!Active) return;
        _end = _end.AddMinutes(minutes);
        if (Paused) _pausedLeft += TimeSpan.FromMinutes(minutes); else Tick();
    }
    public TimeSpan Total => _end - _start;
    public string Label => _label;

    public void Tick()
    {
        if (!Active || Paused) return;
        var left = _end - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            Active = false;
            _store.Remove(Id);
            var task = Task;
            Task = null;
            if (task == null)
                _store.Upsert(new IslandActivity("timer:done", "timer", _label + "结束", "休息一下", ActivityKind.Done, 95, DurationSeconds: 60, ResetTtl: true));
            System.Media.SystemSounds.Asterisk.Play();
            Finished?.Invoke(_label, task);
            return;
        }
        var total = (_end - _start).TotalSeconds;
        var p = 1 - left.TotalSeconds / total;
        var text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        var title = Task != null ? $"{Task} · {text}" : $"{_label} · {text}";
        var sub = Task != null ? $"{_label}中 · {_end:HH:mm} 结束" : $"{_end:HH:mm} 结束";
        _store.Upsert(new IslandActivity(Id, "timer", title, sub, ActivityKind.Running, 58), sticky: true, progress: p);
    }
}

/// <summary>
/// 待办存在 %APPDATA%\Qisland\todo.md（标准 Markdown 任务列表，可以用任何编辑器改）。
/// 岛上添加 / 勾选 / 删除都直接改这个文件，外部改动也会实时同步回岛上。
/// </summary>
public sealed class TodoFile : IDisposable
{
    private readonly ActivityStore _store;
    private readonly FileSystemWatcher _watcher;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public static string PathName => System.IO.Path.Combine(App.DataDir, "todo.md");
    private const string Sample = TodoLines.Sample;
    public IReadOnlyList<TodoItem> Items { get; private set; } = [];
    /// <summary>未完成的标题（去掉 ⏰ / 🔁 标记），给首页和组件显示。</summary>
    public IReadOnlyList<string> Open => Items.Where(i => !i.Done).Select(i => TodoMeta.Parse(i.Text).Title).ToList();
    public event Action? Changed;

    public TodoFile(ActivityStore store)
    {
        _store = store;
        if (!File.Exists(PathName))
            File.WriteAllText(PathName, "# 待办（一行一条，完成后把 [ ] 改成 [x]）\n");
        _watcher = new FileSystemWatcher(App.DataDir, "todo.md") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
        _watcher.Changed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Created += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _watcher.Renamed += (_, _) => _debounce.Dispatcher.BeginInvoke(Kick);
        _debounce.Tick += (_, _) => { _debounce.Stop(); Load(); };
        Load();
    }

    private void Kick() { _debounce.Stop(); _debounce.Start(); }

    private static (bool ok, bool done, string text) Parse(string raw) => TodoLines.Parse(raw);

    private void Load()
    {
        try
        {
            Items = TodoLines.Items(File.ReadAllLines(PathName));
            Changed?.Invoke();
        }
        catch (IOException) { Kick(); }
    }

    /// <summary>
    /// 读文件 → 改 → 原子写回。change 返回 Conflict（要改的那条找不到 / 不唯一）时不写；
    /// 写不进去返回 WriteFailed。每次都重新读文件，按原文定位那一行（编辑窗口开着时文件可能被改过）。
    /// </summary>
    private TodoEditResult Edit(Func<List<string>, TodoEditResult> change)
    {
        TodoEditResult r;
        try
        {
            var lines = File.ReadAllLines(PathName).ToList();
            r = change(lines);
            if (r == TodoEditResult.Ok)
            {
                var text = string.Concat(lines.Select(l => l + Environment.NewLine));
                if (!AtomicFile.TryWrite(PathName, new System.Text.UTF8Encoding(false).GetBytes(text), out var err)) { App.Log("待办写入: " + err); r = TodoEditResult.WriteFailed; }
            }
            else App.Log("待办写入: 文件已被改动，找不到要改的那一条");
        }
        catch (Exception ex) { App.Log("待办写入: " + ex.Message); r = TodoEditResult.WriteFailed; }
        Load();
        return r;
    }

    /// <summary>新待办插在未完成列表的最后、已完成的前面。</summary>
    public TodoEditResult Add(string text)
    {
        text = TodoLines.Clean(text);
        if (text.Length == 0) return TodoEditResult.Conflict;
        return Edit(lines =>
        {
            lines.RemoveAll(l => l.Contains("[ ] " + Sample));
            var at = lines.Count;
            for (var i = lines.Count - 1; i >= 0; i--)
                if (Parse(lines[i]) is { ok: true, done: false }) { at = i + 1; break; }
                else if (Parse(lines[i]) is { ok: true, done: true }) at = i;
            lines.Insert(at, "- [ ] " + text);
            return TodoEditResult.Ok;
        });
    }

    /// <summary>改一条的文字（提醒时间 / 重复写在文字里）。返回 Ok / Conflict（那一条找不到或不唯一）/ WriteFailed。</summary>
    public TodoEditResult SetText(TodoItem it, string text) => Edit(lines => TodoLines.SetText(lines, it, text));

    public TodoEditResult SetDone(TodoItem it, bool done) => Edit(lines => TodoLines.SetDone(lines, it, done, DateTime.Now));

    public TodoEditResult Delete(TodoItem it) => Edit(lines => TodoLines.Delete(lines, it));

    public void ClearDone() => Edit(lines => { lines.RemoveAll(l => Parse(l) is { ok: true, done: true }); return TodoEditResult.Ok; });

    public void Dispose() => _watcher.Dispose();
}
