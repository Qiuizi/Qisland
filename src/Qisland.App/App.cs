using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;

namespace Qisland.App;

public sealed partial class App : Application
{
    public const int Port = 9840;
    private static App? _self;
    private readonly ActivityStore _store = new();
    private IslandWindow? _window;
    private ApiServer? _api;
    private System.Windows.Forms.NotifyIcon? _tray;
    private FileSystemWatcher? _inboxWatcher;
    private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromMinutes(3) }; // 收起后闲 3 分钟才压工作集，免得刚收起又展开时把页面一页页换回来（展开卡顿）
    private ToastMirror? _toasts;
    private MessengerFlash? _flash;
    private HarnessWatcher? _harness;
    private FocusTimer? _timer;
    private MediaWatcher? _media;
    private TodoFile? _todo;
    private Countdowns? _countdown;
    private ClassWatcher? _classes;
    private LyricsService? _lyrics;
    private HomeworkHub? _homework;
    private YuketangService? _ykt;
    private XuexitongService? _xxt;
    /// <summary>原始响应落盘只在调试时开：设 QISLAND_DEBUG=1，或在数据目录 debug 里放一个 enabled 文件。</summary>
    public static bool DebugDumps => _debugDumps ??= Environment.GetEnvironmentVariable("QISLAND_DEBUG") == "1"
        || File.Exists(Path.Combine(DataDir, "debug", "enabled"));
    private static bool? _debugDumps;

    private ApprovalBroker? _approvals;
    private readonly AiSettings _ai = AiSettings.Load();
    private DoubaoQuotaService? _doubao;
    private CodexQuota? _quota;
    private VolumeWatcher? _volume;
    private SystemWatch _sys = null!;
    private readonly PerfGuard _perf = new();
    private ImeWatch? _ime;
    private PrivacyWatcher? _privacy;
    private NetworkWatcher? _net;
    private CallWatcher? _calls;
    private BluetoothWatcher? _bt;
    private CalendarWatcher? _cal;
    private Updater _upd = null!;
    private DateTime _day = DateTime.Today;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tickN;
    private int _quitting;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qisland");
    public static string InboxDir => Path.Combine(DataDir, "inbox");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "hook") return HookRelay.Run(args[1], args.Contains("--approve"));
        var snapshot = args.Contains("--snapshot");
        using var mutex = new Mutex(true, snapshot ? @"Local\Qisland.Snapshot" : @"Local\Qisland.Island", out var first);
        if (!first)
        {
            // 自动重启时旧进程可能还没完全退出：最多等 10 秒拿到单实例锁
            if (!args.Contains("--restarted")) return 0;
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(10))) return 0; }
            catch (AbandonedMutexException) { }
        }
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _self = app;
        app.Startup += (_, _) => app.Boot(args);
        return app.Run();
    }

    public static int Hz { get; private set; } = 60;

    private string[] _args = [];

    private void Boot(string[] args)
    {
        _args = args;
        Directory.CreateDirectory(InboxDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"Qisland/{Updater.Current} (+https://github.com/Qiuizi/Qisland)");
        DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log("后台任务: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
        var snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0)
        {
            var dir = snap + 1 < args.Length ? args[snap + 1] : Path.Combine(DataDir, "snapshot");
            Hz = Native.RefreshRate();
            _window = new IslandWindow(_store) { SnapshotMode = true, Left = -4000, Top = -4000, ShowActivated = false };
            _window.Loaded += async (_, _) =>
            {
                _window.Left = -4000; _window.Top = -4000;
                var lk = Array.IndexOf(args, "--look");
                _window.ApplyLook(lk >= 0 && lk + 1 < args.Length && Enum.TryParse<Theme.Look>(args[lk + 1], true, out var look) ? look : Theme.Look.Dark);
                try { await _window.Snapshot(dir, Demo); } catch (Exception ex) { Log("snapshot: " + ex); }
                Shutdown();
            };
            _window.Show();
            return;
        }
        // 上次自动更新覆盖到一半被杀/断电：先交给恢复脚本按清单还原，本进程退出
        if (Updater.RecoverInterruptedUpdate()) { Shutdown(); return; }
        Autostart.InstallCrashRestart();
        Autostart.EnsureDefault();
        if (args.Contains("--restarted")) Log("已自动重启");

        // WPF 默认按 60 fps 出帧；这台屏是 120 Hz，60 帧的弹簧动画看起来就是「有点卡」。
        // 把动画默认帧率提到屏幕刷新率（频谱条、转圈这些显式写了低帧率的不受影响）。
        Hz = Native.RefreshRate();
        try { System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata(Hz)); }
        catch (Exception ex) { Log("帧率设置: " + ex.Message); }
        Log($"屏幕刷新率 {Hz} Hz");

        LoadLayout();
        IslandWindow.GlassModePref = LoadGlass();
        try
        {
            var ps = Path.Combine(DataDir, "perf-start.txt");
            if (File.Exists(ps) && File.ReadAllText(ps).Contains("sw")) { System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly; Log("测速: 软件渲染"); }
        }
        catch { }
        _window = new IslandWindow(_store) { Ai = _ai };
        _window.Show();

        var approvalKey = ApprovalKeyFile.Rotate(DataDir, out var keyError);
        if (approvalKey == null) Log("审批密钥: " + keyError);
        var apiReady = false;
        try
        {
            _api = new ApiServer(Port, _store, a => { _ = Dispatcher.InvokeAsync(a); }, approvalKey);
            _api.Start();
            apiReady = true;
        }
        catch (Exception ex) { Log("API 端口占用: " + ex.Message); }

        SetupInbox();
        SetupTray();
        _trim.Tick += (_, _) => { _trim.Stop(); Trim(); };
        _ = WeatherLoop();

        StartProviders();
        _window.SettleNow();
        if (args.Contains("--updated")) SignalUpdateReady(apiReady);
        WatchPowerAndSession();
        CleanDebugDumps();
        try { var n = ChangeLedger.Sync().Count; if (n > 0) Log($"改动账本: 对其他软件有 {n} 处改动（托盘 → 对其他软件的改动）"); } catch (Exception ex) { Log("改动账本: " + ex.Message); }
        if (args.Contains("--demo")) Demo();
        TrimSoon();
    }

    // ───────────── 睡眠唤醒 / 时间变化 ─────────────
    // 唤醒后：按分钟睡的循环（天气、信息站、更新检查）立刻醒来再拉一次，不顶着睡前的旧数据过几个小时；
    // 刚醒的 20 秒网络多半还没通，作业这类请求先别发（发了必失败，还要等 5–10 分钟才重试）。
    private static TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static DateTime ResumedAt { get; private set; } = DateTime.MinValue;

    /// <summary>睡一段时间；电脑从睡眠醒来时提前醒。</summary>
    public static async Task Nap(TimeSpan t)
    {
        var w = _wake.Task;
        await Task.WhenAny(Task.Delay(t), w);
    }

    /// <summary>作业这类联网轮询：刚唤醒或者没网时先不发。</summary>
    public static bool NetReady() =>
        DateTime.Now - ResumedAt > TimeSpan.FromSeconds(20) && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    private void WatchPowerAndSession()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            ResumedAt = DateTime.Now;
            Dispatcher.BeginInvoke(async () =>
            {
                Log("从睡眠唤醒");
                _window?.OnResume();
                _media?.Reconnect();
                _net?.OnResume();
                _ykt?.OnResume();
                await Task.Delay(TimeSpan.FromSeconds(20));   // 等网络
                Interlocked.Exchange(ref _wake, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            });
        };
        // 改了时区 / 系统时间：DateTime.Now 用的是缓存的时区，不清的话岛上的钟会一直按旧时区走
        Microsoft.Win32.SystemEvents.TimeChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            System.Globalization.CultureInfo.CurrentCulture.ClearCachedData();
            TimeZoneInfo.ClearCachedData();
            Log($"系统时间 / 时区变了：{TimeZoneInfo.Local.Id}");
            _window?.OnResume();
        });
        // 解锁 / 远程桌面连回来：重新摆位置、确认置顶
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                Dispatcher.BeginInvoke(() => _window?.OnResume());
        };
    }

    // ───────────── 常用软件 ─────────────
    private void StartProviders()
    {
        _sys = new SystemWatch(_store);
        _upd = new Updater(_store, Http, Quit);
        if (_args.Contains("--update-failed")) _upd.ReportFailedUpdate();
        _ = _upd.Loop();
        StartHealth();
        _window!.OpenRequested = a => OpenHealth(a) || OpenLogin(a) || _upd.Open(a) || _sys.Open(a) || (_calls?.Open(a) ?? false) || Launcher.Open(a);
        _window.ApplyLook(LoadLook());
        _timer = new FocusTimer(_store);
        _window.Timer = _timer;
        _timer.Finished += (label, task) => _window.OnFocusFinished(label, task);
        _harness = new HarnessWatcher(_store);
        try { _flash = new MessengerFlash(_store); _flash.Start(); } catch (Exception ex) { Log("ShellHook: " + ex.Message); }
        try { _volume = new VolumeWatcher(); _volume.Changed += _window.ShowVolume; _volume.DeviceChanged += name =>
            {
                if (_bt != null && DateTime.Now - _bt.LastConnect < TimeSpan.FromSeconds(12) && Features.On(Features.Bluetooth)) return;   // 蓝牙那条已经说了
                var icon = name.Contains("耳机") || name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || name.Contains("Buds", StringComparison.OrdinalIgnoreCase) || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ? "已连接耳机" : "声音输出已切换";
                _store.Upsert(new IslandActivity("audio:device", "audio", icon, $"{name} · 音量 {(int)Math.Round(_volume.Level * 100)}%", ActivityKind.Done, 72, DurationSeconds: 4, ResetTtl: true), progress: _volume.Level);
            }; } catch (Exception ex) { Log("音量: " + ex.Message); }
        try { _countdown = new Countdowns(); _window.Countdown = _countdown; _countdown.Changed += _window.OnCountdownChanged; _window.OnCountdownChanged(); }
        catch (Exception ex) { Log("倒计时: " + ex.Message); }
        try { _classes = new ClassWatcher(_store); _window.Classes = _classes; _classes.Changed += _window.OnCountdownChanged; _window.OnCountdownChanged(); }
        catch (Exception ex) { Log("课表: " + ex.Message); }
        try
        {
            _homework = new HomeworkHub(_store); _window.Homework = _homework; _homework.Changed += _window.OnHomeworkChanged;
            _ykt = new YuketangService(_homework, _store);
            _xxt = new XuexitongService(_homework, _store);
            _homework.Tick(); _window.OnHomeworkChanged();
        }
        catch (Exception ex) { Log("作业: " + ex.Message); }
        try
        {
            _approvals = new ApprovalBroker(a => { _ = Dispatcher.InvokeAsync(a); });
            _window.Approvals = _approvals; _approvals.Changed += _window.OnApprovalsChanged;
            if (_api != null) _api.Approval = _approvals.Request;
            if (_api != null) _api.AgentEvent = a => _window?.OnAgentEvent(a);
            if (_api != null) _api.Bench = () => _window?.RunBench();
            if (_api != null) _api.Jump = id =>
            {
                if (id == "media") { if (_window?.Media?.Current is { } m) Launcher.OpenMedia(m); else Log("调试跳转: 没有媒体"); return; }
                if (_store.Get(id) is not { } a) { Log($"调试跳转: 没有 {id}"); return; }
                Log($"调试跳转 {id}: {(_window?.OpenRequested?.Invoke(a) == true ? "成功" : "没跳")}");
            };
            if (_api != null) _api.Preview = id =>
                Dispatcher.Invoke(() => DebugJumpTarget.Resolve(_store, id, _media));
            if (_api != null) { _api.PerfJson = _perf.Json; _api.Peek = (s, p) => _window?.PeekFor(s, p); }
            PerfGuard.Changed += () => _window?.OnPerfChanged();
            PerfGuard.MemoryPressure += () => _window?.OnMemoryPressure();
            _quota = new CodexQuota(a => Dispatcher.BeginInvoke(a));
            _window.Quota = _quota; _quota.Changed += _window.OnQuotaChanged;
            _quota.Changed += _window.OnAiQuotaChanged;
        }
        catch (Exception ex) { Log("Codex: " + ex.Message); }
        try
        {
            _doubao = new DoubaoQuotaService(_ai, a => Dispatcher.BeginInvoke(a));
            _window.Doubao = _doubao; _doubao.Changed += _window.OnAiQuotaChanged;
            _doubao.Start();
            _ = BalanceLoop();
        }
        catch (Exception ex) { Log("AI 额度: " + ex.Message); }
        try { _todo = new TodoFile(_store); _window.Todo = _todo; _todo.Changed += () => _window.SetTodos(_todo.Open); _window.SetTodos(_todo.Open); }
        catch (Exception ex) { Log("待办: " + ex.Message); }
        _ime = new ImeWatch();
        _ime.Changed += () => _window.SetIme(_ime.State);
        _ime.Apply();
        _toasts = new ToastMirror(_store);
        _ = _toasts.StartAsync();
        _ = new InfoStationWatcher(_store, Http).Loop();
        _media = new MediaWatcher(Dispatcher);
        _window.Media = _media;
        _lyrics = new LyricsService();
        _window.Lyrics = _lyrics;
        _media.Changed += () => { _window.OnMediaChanged(); _lyrics.OnTrack(_media.Current); _window.UpdateLyricTimer(); };
        _lyrics.Changed += _window.OnLyricsChanged;
        _ = _media.StartAsync();
        StartGapFeatures();
        _tick.Tick += (_, _) =>
        {
            _tickN++;
            _timer.Tick();
            _sys.Tick(_tickN);
            _flash?.OnForeground(Native.GetForegroundWindow());
            if (_tickN % 3 == 0) { try { _harness.Tick(); } catch (Exception ex) { Log("DSH: " + ex.Message); } }
            if (_tickN % 5 == 0) { _store.Expire(); _perf.Sample(); }
            if (DateTime.Today != _day) { _day = DateTime.Today; _countdown?.Refresh(); }
            if (_tickN % 30 == 0) { try { _classes?.Tick(); } catch (Exception ex) { Log("课表: " + ex.Message); } }
            if (_tickN % 10 == 0) { try { _ykt?.Tick(); } catch (Exception ex) { Log("雨课堂: " + ex.Message); } }
            if (_tickN % 10 == 5) { try { _xxt?.Tick(); } catch (Exception ex) { Log("学习通: " + ex.Message); } }
            if (_tickN % 10 == 7) { try { _quota?.Tick(); } catch (Exception ex) { Log("Codex 额度: " + ex.Message); } }
            if (_tickN % 60 == 0) { try { _homework?.Tick(); _window.OnHomeworkChanged(); } catch (Exception ex) { Log("作业: " + ex.Message); } }
            try { _calls?.Tick(_tickN); } catch (Exception ex) { Log("通话: " + ex.Message); }
            if (_tickN % 30 == 3) { try { _cal?.Tick(); } catch (Exception ex) { Log("日历: " + ex.Message); } }
            if (_tickN % 60 == 13) { try { _bt?.Tick(); } catch (Exception ex) { Log("蓝牙: " + ex.Message); } }
            if (_tickN % 10 == 9 && FocusMode.Poll()) _window.OnFocusModeChanged();
        };
        _tick.Start();
        Log("providers started");
    }

    // ───────────── 从 Mac / iOS 灵动岛补来的：隐私指示、日历、通话、专注、蓝牙、网络、锁定键 ─────────────

    private void StartGapFeatures()
    {
        var w = _window!;
        FocusMode.Own = () => _timer is { Active: true, Paused: false, Label: "专注" };
        try
        {
            _calls = new CallWatcher(_store);
            _privacy = new PrivacyWatcher(Dispatcher);
            _privacy.Changed += (now, before) =>
            {
                _calls.OnDevices(now);
                OnPrivacy(now, before);
            };
            if (Features.On(Features.Privacy) || Features.On(Features.Calls)) _privacy.Start();
        }
        catch (Exception ex) { Log("隐私指示: " + ex.Message); }
        try { _net = new NetworkWatcher(_store, Dispatcher); if (Features.On(Features.Network)) _net.Start(); }
        catch (Exception ex) { Log("网络: " + ex.Message); }
        try { _bt = new BluetoothWatcher(_store, Dispatcher); if (Features.On(Features.Bluetooth)) _bt.Start(); }
        catch (Exception ex) { Log("蓝牙: " + ex.Message); }
        try
        {
            _cal = new CalendarWatcher(_store, Http);
            w.Calendar = _cal;
            _cal.Changed += w.OnCalendarChanged;
            _ = _cal.Loop();
        }
        catch (Exception ex) { Log("日历: " + ex.Message); }
        if (_volume != null)
        {
            _volume.LockChanged += (name, on) => { if (Features.On(Features.LockKeys)) w.ShowLock(name, on); };
            _volume.WatchLockKeys(Features.On(Features.LockKeys));
        }
        Features.Changed += key => Dispatcher.BeginInvoke(() => OnFeature(key));
        if (FocusMode.Poll()) w.OnFocusModeChanged();
    }

    private void OnFeature(string key)
    {
        var on = Features.On(key);
        switch (key)
        {
            case Features.Privacy:
            case Features.Calls:
                if (Features.On(Features.Privacy) || Features.On(Features.Calls)) _privacy?.Start(); else _privacy?.Stop();
                if (_privacy != null) OnPrivacy(_privacy.Uses, []);
                break;
            case Features.Network: if (on) _net?.Start(); else _net?.Stop(); break;
            case Features.Bluetooth: if (on) _bt?.Start(); else _bt?.Stop(); break;
            case Features.LockKeys: _volume?.WatchLockKeys(on); break;
            case Features.Calendar: _cal?.Tick(); _window?.OnCalendarChanged(); if (on) _cal?.RefreshNow(); break;
            case Features.Focus: FocusMode.Poll(); _window?.OnFocusModeChanged(); break;
        }
    }

    /// <summary>麦克风 / 摄像头：开始用时弹一条临时胶囊（通话软件的由通话那条代替），之后只留天气旁边的小圆点。</summary>
    private void OnPrivacy(IReadOnlyList<DeviceUse> now, IReadOnlyList<DeviceUse> before)
    {
        if (!Features.On(Features.Privacy))
        {
            _window?.SetPrivacyDots(false, false, "");
            _store.Remove("privacy:mic"); _store.Remove("privacy:cam");
            return;
        }
        var mic = now.Where(u => u.Mic).ToList();
        var cam = now.Where(u => !u.Mic).ToList();
        var tip = string.Join("\n", now.Select(u => $"{(u.Mic ? "麦克风" : "摄像头")}：{u.App}"));
        _window?.SetPrivacyDots(mic.Count > 0, cam.Count > 0, tip);
        foreach (var (dev, id, src, word) in new[] { ("microphone", "privacy:mic", "mic", "麦克风"), ("webcam", "privacy:cam", "camera", "摄像头") })
        {
            var cur = now.Where(u => u.Device == dev).ToList();
            if (cur.Count == 0) { _store.Remove(id); continue; }
            var fresh = cur.Where(u => !before.Any(b => b.Device == dev && b.Exe == u.Exe)).ToList();
            if (fresh.Count == 0) continue;
            Log($"{word}: {string.Join("、", fresh.Select(u => u.App))} 开始使用");
            // 通话软件开麦：通话胶囊会接手，不再单独弹；摄像头照样提示（视频通话时开摄像头是值得知道的）
            if (dev == "microphone" && Features.On(Features.Calls) && fresh.All(u => CallWatcher.IsCallApp(u.Exe))) continue;
            var names = string.Join("、", cur.Select(u => u.App).Distinct());
            _store.Upsert(new IslandActivity(id, src, $"{word}正在使用", names, ActivityKind.Info, 60, DurationSeconds: 5, ResetTtl: true));
        }
    }

    private void CalendarMenu(System.Windows.Forms.ToolStripMenuItem m)
    {
        m.DropDownItems.Clear();
        if (_cal == null) { m.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("日历没启动") { Enabled = false }); return; }
        m.DropDownItems.Add("新建本机日程…", null, (_, _) => Dispatcher.BeginInvoke(() => new CalendarEventWindow(_cal).Show()));
        m.DropDownItems.Add("添加 .ics 订阅链接…", null, (_, _) => Dispatcher.BeginInvoke(() => new CalendarAddWindow(_cal).Show()));
        var refresh = new System.Windows.Forms.ToolStripMenuItem(_cal.LastOk is { } t ? $"立即刷新（上次 {t:HH:mm}）" : "立即刷新") { Enabled = _cal.Feeds.Count > 0 };
        refresh.Click += (_, _) => Dispatcher.BeginInvoke(_cal.RefreshNow);
        m.DropDownItems.Add(refresh);
        if (_cal.LastError.Length > 0) m.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("上次出错：" + _cal.LastError) { Enabled = false });
        if (_cal.Feeds.Count > 0) m.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        foreach (var url in _cal.Feeds)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem($"{CalendarWatcher.Host(url)}（点击删除）") { ToolTipText = "链接本身加密保存，这里只显示域名" };
            var u = url;
            item.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (System.Windows.MessageBox.Show($"删除日历订阅 {CalendarWatcher.Host(u)}？", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK
                    && !_cal.Remove(u))
                    System.Windows.MessageBox.Show("订阅保存失败，请关闭占用日历文件的程序后重试", "Qisland", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
            m.DropDownItems.Add(item);
        }
    }

    /// <summary>岛上「登录过期了」那条：点一下直接弹扫码窗口（以前点了没反应，只能去托盘里找）。</summary>
    private bool OpenLogin(LiveActivity a)
    {
        if (a.Id is "ykt:expired" or "ykt:expired-note") { Dispatcher.BeginInvoke(YuketangLoginOrOut); return true; }
        if (a.Id is "xxt:expired" or "xxt:expired-note") { Dispatcher.BeginInvoke(XuexitongLoginOrOut); return true; }
        return false;
    }

    private void YuketangLoginOrOut()
    {
        if (_ykt == null) return;
        if (_ykt.LoggedIn && !_ykt.Expired)
        {
            if (System.Windows.MessageBox.Show($"退出雨课堂账号 {_ykt.UserName}？本机保存的登录状态会被删除。", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK
                && LogoutCleanup.Message("雨课堂", _ykt.Logout()) is { } yktErr)
                System.Windows.MessageBox.Show(yktErr, "Qisland", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var ykt = _ykt;
        QrLoginWindow? w = null;
        w = new QrLoginWindow("雨课堂扫码登录", "用微信扫一扫，登录后只读取各课程的作业和截止时间。", "请用微信扫码，并在手机上确认",
            async () => { var qr = await YuketangService.RequestQr(); return (QrLoginWindow.Render(qr.Content), ct => ykt.WaitLogin(qr, ct, w!.SetStatus)); });
        w.Closed += (_, _) =>
        {
            if (!w.Success) return;
            _store.Remove("ykt:expired");
            _store.Remove("ykt:expired-note");
            _store.Upsert(new IslandActivity("ykt:login", HomeworkHub.Source, "雨课堂已连接", $"{ykt.UserName} · 正在读取作业…", ActivityKind.Done, 60, DurationSeconds: 5));
        };
        w.Show();
    }

    private void XuexitongLoginOrOut()
    {
        if (_xxt == null) return;
        if (_xxt.LoggedIn && !_xxt.Expired)
        {
            if (System.Windows.MessageBox.Show($"退出学习通账号 {_xxt.UserName}？本机保存的登录状态会被删除。", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK
                && LogoutCleanup.Message("学习通", _xxt.Logout()) is { } xxtErr)
                System.Windows.MessageBox.Show(xxtErr, "Qisland", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var xxt = _xxt;
        QrLoginWindow? w = null;
        w = new QrLoginWindow("学习通扫码登录", "用学习通 App 扫一扫（首页右上角），登录后只读取作业和截止时间。", "请用学习通 App 扫码",
            async () => { var qr = await XuexitongService.RequestQr(); return (QrLoginWindow.FromBytes(qr.Png), ct => xxt.WaitLogin(qr, t => w!.SetStatus(t), ct)); });
        w.Closed += (_, _) =>
        {
            if (!w.Success) return;
            _store.Remove("xxt:expired");
            _store.Upsert(new IslandActivity("xxt:login", HomeworkHub.Source, "学习通已连接", $"{xxt.UserName} · 正在读取作业…", ActivityKind.Done, 60, DurationSeconds: 5));
        };
        w.Show();
    }

    /// <summary>DeepSeek / Kimi 余额：只在填了 Key 时读，1 小时一次。</summary>
    private async Task BalanceLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(40));
        while (true)
        {
            try
            {
                if (_ai.BalancesOn && _window != null)
                {
                    _window.Balances = await AiClient.Balances(CancellationToken.None);
                    _window.OnAiQuotaChanged();
                }
            }
            catch (Exception ex) { Log("余额: " + ex.GetType().Name); }
            await Task.Delay(TimeSpan.FromHours(1));
        }
    }

    private void OpenAiSettings()
    {
        if (_window == null) return;
        new AiSettingsWindow(_ai, _window.ApplyAiHotkeys).Show();
    }

    private void RestoreChanges()
    {
        var list = ChangeLedger.Sync();
        var text = string.Join("\n", list.Select(e => $"· {e.App}：{e.What}"));
        if (System.Windows.MessageBox.Show($"把 Qisland 对其他软件的改动全部还原：\n\n{text}\n\n只删 Qisland 自己加的条目，你自己的 hook 和插件原样保留，改前会备份。Codex 新开的会话、重启后的 DSH 生效。\n\n确定还原？",
                "Qisland", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _approvals?.CancelAll();
        var res = ChangeLedger.RestoreAll();
        System.Windows.MessageBox.Show(res.Count > 0 ? string.Join("\n", res) : "没有需要还原的改动。", "Qisland");
    }

    private void ImportTimetable()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择课表 .ics（教务 App / WakeUp 课程表导出）", Filter = "日历文件 (*.ics)|*.ics|所有文件|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!ClassWatcher.Import(dlg.FileName, out var ics))
        {
            _store.Upsert(new IslandActivity("class:import", ClassWatcher.Source, "课表导入失败", "这个文件里没有找到课程（需要 .ics 日历格式）", ActivityKind.Error, 70, DurationSeconds: 6));
            return;
        }
        _classes?.Use(ics);   // 立即换上新课表再数，不读防抖前的旧数据
        var n = _classes?.Day(DateTime.Today).Count ?? 0;
        _store.Upsert(new IslandActivity("class:import", ClassWatcher.Source, "课表已导入", n > 0 ? $"今天有 {n} 节课" : "今天没有课", ActivityKind.Done, 60, DurationSeconds: 5));
    }

    // ───────────── 收件箱：给 WSL NAT 模式等连不上 localhost 的场景 ─────────────
    // 一批最多 50 个，剩下的排到下一批（后台优先级），积压再多也会处理完、不卡界面；
    // 取不走的按 1s/2s/5s/10s/30s 退避重试，连续 8 次失败移到 inbox\failed\ 并记日志，不挡别的文件；
    // 先完整解析再删：字段不合要求的直接移到 failed\，一条出错不中断这一批
    private InboxPump? _inbox;

    private void SetupInbox()
    {
        _inbox = new InboxPump(() => Directory.EnumerateFiles(InboxDir, "*.json"), TakeInboxFile,
            run => Dispatcher.BeginInvoke(run, DispatcherPriority.Background), giveUp: GiveUpInboxFile,
            invalid: (f, ex) => MoveInboxToFailed(f, $"处理时出错（{ex.GetType().Name}）"));
        _inboxWatcher = new FileSystemWatcher(InboxDir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        _inboxWatcher.Created += (_, _) => Dispatcher.BeginInvoke(() => _inbox.Request(), DispatcherPriority.Background);
        _inboxWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() => _inbox.Request(), DispatcherPriority.Background);
        _inbox.Request();
    }

    /// <summary>一直取不走的收件箱文件：移到 inbox\failed\（移不动就留着，等下次重新计数），记日志。</summary>
    private static void GiveUpInboxFile(string f, int attempts) => MoveInboxToFailed(f, $"连续 {attempts} 次读取 / 解析失败");

    /// <summary>移到 inbox\failed\ 并记日志（只记文件名和原因，不记内容）；移不动返回 false。</summary>
    private static bool MoveInboxToFailed(string f, string why)
    {
        var name = Path.GetFileName(f);
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(InboxDir, "failed")).FullName;
            File.Move(f, Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}"), overwrite: true);
            Log($"收件箱: {name} {why}，已移到 inbox\\failed");
            return true;
        }
        catch (Exception ex) { Log($"收件箱: {name} {why}，移到 failed 也失败（{ex.GetType().Name}），稍后再试"); return false; }
    }

    /// <summary>
    /// 取走一个收件箱文件：先读、完整解析校验，再删、再上岛。
    /// 还没写完 / 被占用 / 不是完整 JSON 返回 false（按退避稍后重试）；字段不合要求的移到 failed\。
    /// </summary>
    private bool TakeInboxFile(string f)
    {
        string text;
        try { text = File.ReadAllText(f); }
        catch (IOException) { return false; } // 还没写完，下次再取
        catch (UnauthorizedAccessException) { return false; }
        var r = InboxDecoder.Decode(text);
        if (r.NotJson) return false;          // 可能还没写完：退避重试，一直这样 8 次后移到 failed
        if (!r.Ok) return MoveInboxToFailed(f, "格式不对：" + r.Error);
        try { File.Delete(f); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        foreach (var (d, p) in r.Cards) _store.Upsert(d, p);
        return true;
    }

    // ───────────── 天气（weather.txt 里配的地点，没配就让 wttr.in 按 IP 定位；20 分钟一次，Open-Meteo 兜底） ─────────────
    public static string WeatherFile => Path.Combine(DataDir, "weather.txt");
    /// <summary>显示用的地名：配置的显示名 → wttr.in 认出的地名 → 空（界面上就不写地名）。</summary>
    public static string WeatherLabel { get; private set; } = "";
    private (double Lat, double Lon)? _lastCoords;

    private static WeatherPlace LoadWeatherPlace()
    {
        try
        {
            if (!File.Exists(WeatherFile))
                File.WriteAllText(WeatherFile,
                    "# 天气地点。留空 = 按网络位置自动判断。\n" +
                    "# 写法：显示名 查询词，查询词可以是城市英文名或 纬度,经度。例如：\n" +
                    "# 北京 Beijing\n# 示例 39.90,116.40\n");
            return WeatherPlace.Parse(File.ReadAllText(WeatherFile));
        }
        catch { return WeatherPlace.Auto; }
    }

    private async Task WeatherLoop()
    {
        while (true)
        {
            var wait = TimeSpan.FromMinutes(15);   // Open-Meteo 的实况每 15 分钟更新一次
            var ok = false;
            var city = WeatherCityList.Current;
            var cityKey = CityKey(city);   // 记下发请求时的城市，结果按它入缓存
            var place = city != null ? new WeatherPlace(city.Label, city.Query) : LoadWeatherPlace();
            _weatherWake = new TaskCompletionSource();
            WeatherDetail? wttr = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                wttr = WttrParser.Parse(await Http.GetStringAsync(place.WttrUrl, cts.Token), DateTime.Now);
                if (wttr?.Lat is { } la && wttr.Lon is { } lo && city == null && WeatherCityList.Current == null) _lastCoords = (la, lo);
            }
            catch (Exception ex) { Log("wttr: " + ex.Message); }
            var label = place.Display.Length > 0 ? place.Display : wttr?.Area ?? "";
            var coords = city != null ? (city.Lat, city.Lon) : place.Coordinates ?? _lastCoords;
            if (coords is { } c)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var f = OpenMeteo.Parse(await Http.GetStringAsync(OpenMeteo.ForecastUrl(c.Lat, c.Lon), cts.Token), DateTime.Now);
                    if (f != null)
                    {
                        var d = WeatherDetail.FromForecast(f, wttr, DateTime.Now);
                        ShowWeather(cityKey, label, d.Now, d);
                        ok = true;
                    }
                }
                catch (Exception ex) { Log("Open-Meteo: " + ex.Message); }
            }
            if (!ok && wttr != null) { ShowWeather(cityKey, label, wttr.Now, wttr); ok = true; }
            if (!ok) wait = TimeSpan.FromMinutes(3);
            await Task.WhenAny(Nap(wait), _weatherWake.Task);
        }
    }

    // ───────────── 托盘 ─────────────
    private static string LookFile => Path.Combine(DataDir, "look.txt");
    private static Theme.Look LoadLook()
    {
        try { return Enum.TryParse<Theme.Look>(File.ReadAllText(LookFile).Trim(), out var l) ? l : Theme.Look.Dark; }
        catch { return Theme.Look.Dark; }
    }
    private static void SaveLook(Theme.Look l) { try { File.WriteAllText(LookFile, l.ToString()); } catch { } }
    private static string GlassFile => Path.Combine(DataDir, "glass.txt");
    private static GlassMode LoadGlass() { try { return File.Exists(GlassFile) ? GlassPref.Parse(File.ReadAllText(GlassFile)) : GlassMode.System; } catch { return GlassMode.System; } }
    internal static void SaveGlass(GlassMode m) { try { File.WriteAllText(GlassFile, GlassPref.Serialize(m)); } catch { } }

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _trayMenu = menu;
        menu.Items.Add("设置…", null, (_, _) => Dispatcher.BeginInvoke(() => OpenSettings()));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        var focus = new System.Windows.Forms.ToolStripMenuItem("计时");
        foreach (var (m, label) in new[] { (25, "专注 25 分钟"), (50, "专注 50 分钟"), (5, "计时 5 分钟"), (10, "计时 10 分钟") })
            focus.DropDownItems.Add(label, null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Start(m, label.StartsWith("专注") ? "专注" : "计时")));
        focus.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        focus.DropDownItems.Add("取消计时", null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Cancel()));
        menu.Items.Add(focus);
        var look = new System.Windows.Forms.ToolStripMenuItem("外观");
        foreach (var l in new[] { Theme.Look.Dark, Theme.Look.Light, Theme.Look.Glass, Theme.Look.Liquid })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(Theme.LookName(l));
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => { _window?.ApplyLook(l); SaveLook(l); });
            look.DropDownItems.Add(item);
        }
        // 液态玻璃的背景做法（只对液态玻璃外观生效）
        look.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        foreach (var g in new[] { GlassMode.System, GlassMode.Capture, GlassMode.Off })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem("玻璃背景：" + GlassPref.Name(g)) { Tag = g };
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => _window?.SetGlassMode(g));
            look.DropDownItems.Add(item);
        }
        look.DropDownOpening += (_, _) =>
        {
            foreach (var o in look.DropDownItems)
                if (o is System.Windows.Forms.ToolStripMenuItem i)
                    i.Checked = i.Tag is GlassMode g ? g == IslandWindow.GlassModePref : i.Text == Theme.LookName(Theme.Current);
        };
        menu.Items.Add(look);
        var motion = new System.Windows.Forms.ToolStripMenuItem("动效");
        foreach (var (p, label) in new[] { (IslandWindow.MotionPref.Full, "完整（默认）"), (IslandWindow.MotionPref.System, "跟随系统"), (IslandWindow.MotionPref.Reduced, "精简") })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(label) { Tag = p };
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => _window?.SetMotionPref(p));
            motion.DropDownItems.Add(item);
        }
        motion.DropDownOpening += (_, _) =>
        {
            foreach (System.Windows.Forms.ToolStripMenuItem i in motion.DropDownItems) i.Checked = (IslandWindow.MotionPref)i.Tag! == IslandWindow.MotionMode;
        };
        menu.Items.Add(motion);
        menu.Items.Add(DockMenu());
        menu.Items.Add("导入课表（.ics）…", null, (_, _) => Dispatcher.BeginInvoke(ImportTimetable));
        var hwMenu = new System.Windows.Forms.ToolStripMenuItem("作业 DDL");
        var yktLogin = new System.Windows.Forms.ToolStripMenuItem("雨课堂：扫码登录…");
        yktLogin.Click += (_, _) => Dispatcher.BeginInvoke(YuketangLoginOrOut);
        var hwRefresh = new System.Windows.Forms.ToolStripMenuItem("立即刷新作业");
        hwRefresh.Click += (_, _) => Dispatcher.BeginInvoke(() => { _ykt?.RefreshNow(); _xxt?.RefreshNow(); });
        var xxtLogin = new System.Windows.Forms.ToolStripMenuItem("学习通：扫码登录…");
        xxtLogin.Click += (_, _) => Dispatcher.BeginInvoke(XuexitongLoginOrOut);
        hwMenu.DropDownItems.Add(yktLogin);
        hwMenu.DropDownItems.Add(xxtLogin);
        hwMenu.DropDownItems.Add(hwRefresh);
        hwMenu.DropDownOpening += (_, _) =>
        {
            yktLogin.Text = _ykt is { LoggedIn: true } y ? (y.Expired ? "雨课堂：登录过期，重新扫码…" : $"雨课堂：已登录 {y.UserName}（点此退出）") : "雨课堂：扫码登录…";
            xxtLogin.Text = _xxt is { LoggedIn: true } x ? (x.Expired ? "学习通：登录过期，重新扫码…" : $"学习通：已登录 {x.UserName}（点此退出）") : "学习通：扫码登录…";
            hwRefresh.Enabled = _ykt?.LoggedIn == true || _xxt?.LoggedIn == true;
            var last = new[] { _ykt?.LastOk, _xxt?.LastOk }.Max();
            hwRefresh.Text = last is { } t ? $"立即刷新作业（上次 {t:HH:mm}）" : "立即刷新作业";
        };
        menu.Items.Add(hwMenu);
        menu.Items.Add(WeatherMenu());
        var calMenu = new System.Windows.Forms.ToolStripMenuItem("日历");
        calMenu.DropDownItems.Add("…");
        calMenu.DropDownOpening += (_, _) => CalendarMenu(calMenu);
        menu.Items.Add(calMenu);
        var hints = new System.Windows.Forms.ToolStripMenuItem("岛上提示");
        foreach (var (key, label, tip) in new[]
        {
            (Features.Privacy, "麦克风 / 摄像头占用", "有程序开始用麦克风 / 摄像头时弹一下，用着的时候天气左边留个橙点 / 绿点"),
            (Features.Calls, "通话 / 会议", "微信 / QQ 来电和通话中、腾讯会议 / 钉钉 / 飞书会议中，显示时长，点一下切过去"),
            (Features.Focus, "专注时只放行要紧的", "自己的专注计时或 Windows 专注会话进行中：胶囊只放行审批、通话、1 小时内截止的作业，其余只进列表"),
            (Features.Calendar, "日历日程和会前提醒", "订阅日历的接下来几个日程进列表和课程组件，开始前 10 分钟提醒，有会议链接可一键加入"),
            (Features.Bluetooth, "蓝牙耳机连接和电量", "蓝牙耳机 / 音箱连上、断开时提示，能读到电量就显示，低电量提醒"),
            (Features.Network, "网络断开 / 恢复和代理", "断网、恢复、换 Wi-Fi、校园网要网页认证、系统代理开关时提示"),
            (Features.LockKeys, "大写 / 数字锁定提示", "按 Caps Lock / Num Lock 时岛上显示开还是关（只看按键，不改按键）"),
            (Features.WeatherAlerts, "天气提醒", "按预报推算：快下雨、雷阵雨、下雪、大风、高温、严寒、明天大降温，同一件事一天只提醒一次"),
            (Features.Water, "喝水提醒（默认关）", "9:00–22:00 间隔 45 分钟没记喝水就提醒一次，午休 12:00–13:30 不打扰；点提醒记一杯"),
            (Features.Sit, "久坐提醒（默认关）", "连续用电脑 60 分钟提醒起来活动；离开 5 分钟算休息过"),
            (Features.Clock12, "12 小时制时钟（默认关）", "收起时的时钟显示成 3:05 而不是 15:05"),
            (Features.OledShift, "OLED 防烧屏（默认关）", "收起时岛每隔几分钟左右挪 1–3 像素，避免 OLED 屏留下残影"),
        })
        {
            var it = new System.Windows.Forms.ToolStripMenuItem(label) { CheckOnClick = true, Checked = Features.On(key), ToolTipText = tip, Tag = key };
            it.CheckedChanged += (_, _) => Features.Set(key, it.Checked);
            hints.DropDownItems.Add(it);
        }
        // 设置窗里也能改这些开关：打开菜单时按实际状态重新勾一遍
        hints.DropDownOpening += (_, _) =>
        {
            foreach (var it in hints.DropDownItems.OfType<System.Windows.Forms.ToolStripMenuItem>())
                if (it.Tag is string k && it.Checked != Features.On(k)) it.Checked = Features.On(k);
        };
        menu.Items.Add(hints);
        var cdx = new System.Windows.Forms.ToolStripMenuItem("Codex");
        var approve = new System.Windows.Forms.ToolStripMenuItem("在岛上批准 Codex 请求") { ToolTipText = "Codex 要你批准命令时，岛上直接点允许 / 拒绝；100 秒不点就回终端问你。只改 ~/.codex/hooks.json 里 Qisland 的 PermissionRequest 一条，改前备份。" };
        approve.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var on = CodexApproveHook.IsOn() == true;
            if (!on && System.Windows.MessageBox.Show("打开后，Codex 请求执行命令时会先等你在灵动岛上点「允许 / 拒绝 / 回终端」，最多等 100 秒，不点就照常在终端里问你，不会默认同意。\n\n这会修改 ~/.codex/hooks.json 里 Qisland 自己的 PermissionRequest 条目（改为同步、超时 120 秒），原文件备份为 hooks.json.qisland-bak。新开的 Codex 会话生效。\n\n确定打开？", "Qisland", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            if (!CodexApproveHook.Set(!on)) System.Windows.MessageBox.Show("没改成：没找到 ~/.codex/hooks.json 里 Qisland 的 PermissionRequest 条目，或文件写不进去。", "Qisland");
            else if (on) _approvals?.CancelAll();
        });
        var quotaItem = new System.Windows.Forms.ToolStripMenuItem("额度：读取中…");
        quotaItem.Click += (_, _) => Dispatcher.BeginInvoke(() => { _quota?.Refresh(); _window?.OnQuotaChanged(); });
        cdx.DropDownItems.Add(approve);
        cdx.DropDownItems.Add(quotaItem);
        cdx.DropDownOpening += (_, _) =>
        {
            var st = CodexApproveHook.IsOn();
            approve.Enabled = st != null; approve.Checked = st == true;
            if (st == null) approve.Text = "在岛上批准 Codex 请求（未找到 Qisland 的 Codex hook）";
            quotaItem.Text = _quota is { Fresh: true } q
                ? $"额度：5 小时已用 {q.Primary?.UsedPercent ?? 0:0}% · 本周已用 {q.Secondary?.UsedPercent ?? 0:0}%（{q.LastOk:HH:mm}，点击刷新）"
                : "额度：还没读到（点击刷新）";
        };
        menu.Items.Add(cdx);
        menu.Items.Add(AiMenu());
        menu.Items.Add(IntegrationsMenu());
        var ledger = new System.Windows.Forms.ToolStripMenuItem("对其他软件的改动");
        ledger.DropDownOpening += (_, _) =>
        {
            ledger.DropDownItems.Clear();
            var list = ChangeLedger.Sync();
            if (list.Count == 0) ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("没有（Qisland 没改过别的软件）") { Enabled = false });
            foreach (var e in list)
                ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem($"{e.App} · {e.What}") { Enabled = false, ToolTipText = $"{e.Target}\n{(e.Detected ? "启动时检测到" : "记录于")} {e.At:yyyy-MM-dd HH:mm}" });
            ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            var restore = new System.Windows.Forms.ToolStripMenuItem("一键还原…") { Enabled = list.Any(x => x.Id is "codex-hooks" or "dsh-bridge" || HookPlans.ById(x.Id) != null) };
            restore.Click += (_, _) => Dispatcher.BeginInvoke(RestoreChanges);
            ledger.DropDownItems.Add(restore);
            ledger.DropDownItems.Add("打开账本文件", null, (_, _) => { ChangeLedger.Sync(); if (File.Exists(ChangeLedger.PathName)) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + ChangeLedger.PathName + "\"") { UseShellExecute = true }); });
        };
        ledger.DropDownItems.Add("…");
        menu.Items.Add(ledger);
        menu.Items.Add("编辑倒计时", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Countdowns.PathName + "\"") { UseShellExecute = true }));
        var lyr = new System.Windows.Forms.ToolStripMenuItem("显示歌词") { CheckOnClick = true, Checked = LyricsService.Enabled, ToolTipText = "播放音乐时胶囊里滚动显示当前这句歌词。网易云按歌曲 id 取；Apple Music、QQ 音乐、酷狗、Spotify 等按歌名 + 歌手 + 时长到网易云 / LRCLIB 查，查不到就不显示。在歌词上滚滚轮可微调快慢" };
        lyr.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(() => { LyricsService.Enabled = lyr.Checked; _lyrics?.OnTrack(null); _lyrics?.OnTrack(_media?.Current); });
        menu.Items.Add(lyr);
        var otp = new System.Windows.Forms.ToolStripMenuItem("通知里的验证码一键复制") { CheckOnClick = true, Checked = ToastMirror.OtpEnabled, ToolTipText = "通知预览里有验证码时，岛上单独弹一条带「复制」的卡片；验证码只在内存里，不记日志、不进剪贴板历史" };
        otp.CheckedChanged += (_, _) => ToastMirror.OtpEnabled = otp.Checked;
        menu.Items.Add(otp);
        var ime = new System.Windows.Forms.ToolStripMenuItem("显示输入法中 / 英（实验）") { CheckOnClick = true, Checked = ImeWatch.Enabled, ToolTipText = "收起的岛上，天气左边显示当前输入法是中文还是英文。部分输入法 / 程序里可能不准" };
        ime.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(() => { ImeWatch.Enabled = ime.Checked; _ime?.Apply(); });
        menu.Items.Add(ime);
        var keys = new System.Windows.Forms.ToolStripMenuItem("接管音量键") { CheckOnClick = true, Checked = !File.Exists(VolumeWatcher.OffFlag), ToolTipText = "音量键改由灵动岛显示，不再弹出系统音量浮窗" };
        keys.CheckedChanged += (_, _) => _volume?.HookKeys(keys.Checked);
        menu.Items.Add(keys);
        var auto = new System.Windows.Forms.ToolStripMenuItem("开机自启");
        // 只走 Run 键时（排队启动，会晚几分钟）点一下是升级成登录计划任务，而不是关掉
        auto.Click += (_, _) => { if (Autostart.TaskOk()) Autostart.Set(false); else Autostart.Set(true); };
        menu.Opening += (_, _) =>
        {
            var task = Autostart.TaskOk();
            auto.Checked = task || Autostart.Enabled;
            auto.Text = task ? "开机自启" : auto.Checked ? "开机自启（排队中，点击改为登录即启动）" : "开机自启";
        };
        menu.Items.Add(auto);
        menu.Items.Add("编辑待办", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", "\"" + TodoFile.PathName + "\"") { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("发送测试卡片", null, (_, _) => Dispatcher.BeginInvoke(Demo));
        menu.Items.Add("清空活动", null, (_, _) => Dispatcher.BeginInvoke(() => { foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id); }));
        menu.Items.Add($"检查更新（当前 v{Updater.Current}）", null, (_, _) => Dispatcher.BeginInvoke(() => _ = _upd.Check(manual: true)));
        menu.Items.Add("跳过这个新版本", null, (_, _) => Dispatcher.BeginInvoke(() => _upd.Skip()));
        menu.Items.Add("打开数据目录", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", DataDir) { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出 Qisland", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = $"Qisland v{Updater.Current}",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private async void Quit()
    {
        if (Interlocked.Exchange(ref _quitting, 1) != 0) return;
        _approvals?.CancelAll();
        if (_api != null)
        {
            var stopped = await _api.StopAsync(TimeSpan.FromSeconds(1.5));
            Log(stopped ? "API 已停机" : "API 停机超时，已强制关闭连接");
        }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _volume?.Dispose();
        _privacy?.Dispose();
        _net?.Dispose();
        _bt?.Stop();
        _inboxWatcher?.Dispose();
        Shutdown();
    }

    /// <summary>更新脚本启动新版本后，等窗口和 API 都起来再落就绪标记。</summary>
    private void SignalUpdateReady(bool apiReady)
    {
        if (!apiReady || _window?.IsVisible != true) return;
        var path = Environment.GetEnvironmentVariable("QISLAND_UPDATE_READY");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"v{Updater.Current}\n{DateTime.Now:O}\n");
            Log("更新就绪标记已写入");
        }
        catch (Exception ex) { Log("更新就绪标记写入失败: " + ex.Message); }
    }

    private void Demo()
    {
        var now = DateTime.Now;
        _store.Upsert(new PushDraft { Id = "demo:claude", Source = "claude-code", Type = "running", Title = "正在重写 IslandWindow.cs", Body = "Qisland · 已编辑 6 个文件", DurationSeconds = 120 });
        _store.Upsert(new PushDraft { Id = "demo:dsh", Source = "deepseek-harness", Type = "warning", Priority = "high", Title = "需要你确认命令", Body = "dotnet publish -c Release", DurationSeconds = 120 });
        _store.Upsert(new PushDraft { Id = "demo:wx", Source = "wechat", Title = "软件工程 2025 级群", Body = "王老师：明天的实验课改到 B403", DurationSeconds = 120 });
        _store.Upsert(new PushDraft { Id = "demo:course", Source = "coursedesk", Type = "running", Title = "离散数学作业 4", Body = "周二 23:59 截止", DurationSeconds = 120 }, 0.62);
        _store.Upsert(new PushDraft { Id = "demo:codex", Source = "codex", Type = "success", Title = "测试全部通过", Body = "info-station · 42 passed", DurationSeconds = 120 });
    }

    /// <summary>调试开关关着时，把以前调试留下的原始响应（ykt-*.json、xxt-*.html）删掉，只留开关文件。</summary>
    private static void CleanDebugDumps()
    {
        if (DebugDumps) return;
        try
        {
            var dir = Path.Combine(DataDir, "debug");
            if (!Directory.Exists(dir)) return;
            var n = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (!(name.StartsWith("ykt-") || name.StartsWith("xxt-"))) continue;
                try { File.Delete(f); n++; } catch { }
            }
            if (n > 0) Log($"清掉了 {n} 个旧的调试转储（调试开关没开）");
        }
        catch (Exception ex) { Log("清理调试转储: " + ex.Message); }
    }

    // ───────────── 内存：动画结束后把空闲页还给系统 ─────────────
    public static void TrimSoon()
    {
        if (_self is null) return;
        _self._trim.Stop();
        _self._trim.Start();
    }

    private static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, false, true);
        Native.EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        var info = GC.GetGCMemoryInfo();
        Log($"trim: managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, gc committed {info.TotalCommittedBytes / 1048576.0:0.0} MB, private {Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:0.0} MB");
    }

    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(DataDir, "qisland.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}"); }
        catch { }
    }
}
