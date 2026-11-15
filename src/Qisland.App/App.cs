using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;

namespace Qisland.App;

public sealed class App : Application
{
    public const int Port = 9840;
    private static App? _self;
    private readonly ActivityStore _store = new();
    private IslandWindow? _window;
    private ApiServer? _api;
    private System.Windows.Forms.NotifyIcon? _tray;
    private FileSystemWatcher? _inboxWatcher;
    private readonly DispatcherTimer _trim = new() { Interval = TimeSpan.FromSeconds(30) };
    private ToastMirror? _toasts;
    private MessengerFlash? _flash;
    private HarnessWatcher? _harness;
    private FocusTimer? _timer;
    private MediaWatcher? _media;
    private TodoFile? _todo;
    private Countdowns? _countdown;
    private VolumeWatcher? _volume;
    private SystemWatch _sys = null!;
    private Updater _upd = null!;
    private DateTime _day = DateTime.Today;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tickN;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qisland");
    public static string InboxDir => Path.Combine(DataDir, "inbox");

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "hook") return HookRelay.Run(args[1]);
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

    private void Boot(string[] args)
    {
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
        Autostart.InstallCrashRestart();
        Autostart.EnsureDefault();
        if (args.Contains("--restarted")) Log("已自动重启");

        // WPF 默认按 60 fps 出帧；这台屏是 120 Hz，60 帧的弹簧动画看起来就是「有点卡」。
        // 把动画默认帧率提到屏幕刷新率（频谱条、转圈这些显式写了低帧率的不受影响）。
        Hz = Native.RefreshRate();
        try { System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(System.Windows.Media.Animation.Timeline), new FrameworkPropertyMetadata(Hz)); }
        catch (Exception ex) { Log("帧率设置: " + ex.Message); }
        Log($"屏幕刷新率 {Hz} Hz");

        _window = new IslandWindow(_store);
        _window.Show();

        try
        {
            _api = new ApiServer(Port, _store, a => Dispatcher.BeginInvoke(a));
            _api.Start();
        }
        catch (Exception ex) { Log("API 端口占用: " + ex.Message); }

        SetupInbox();
        SetupTray();
        _trim.Tick += (_, _) => { _trim.Stop(); Trim(); };
        _ = WeatherLoop();

        StartProviders();
        if (args.Contains("--demo")) Demo();
        TrimSoon();
    }

    // ───────────── 常用软件 ─────────────
    private void StartProviders()
    {
        _sys = new SystemWatch(_store);
        _upd = new Updater(_store, Http, Quit);
        _ = _upd.Loop();
        _window!.OpenRequested = a => _upd.Open(a) || _sys.Open(a) || Launcher.Open(a);
        _window.ApplyLook(LoadLook());
        _timer = new FocusTimer(_store);
        _window.Timer = _timer;
        _timer.Finished += (label, task) => _window.OnFocusFinished(label, task);
        _harness = new HarnessWatcher(_store);
        try { _flash = new MessengerFlash(_store); _flash.Start(); } catch (Exception ex) { Log("ShellHook: " + ex.Message); }
        try { _volume = new VolumeWatcher(); _volume.Changed += _window.ShowVolume; _volume.DeviceChanged += name =>
            {
                var icon = name.Contains("耳机") || name.Contains("Headphone", StringComparison.OrdinalIgnoreCase) || name.Contains("Buds", StringComparison.OrdinalIgnoreCase) || name.Contains("AirPods", StringComparison.OrdinalIgnoreCase) || name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ? "已连接耳机" : "声音输出已切换";
                _store.Upsert(new IslandActivity("audio:device", "audio", icon, $"{name} · 音量 {(int)Math.Round(_volume.Level * 100)}%", ActivityKind.Done, 72, DurationSeconds: 4, ResetTtl: true), progress: _volume.Level);
            }; } catch (Exception ex) { Log("音量: " + ex.Message); }
        try { _countdown = new Countdowns(); _window.Countdown = _countdown; _countdown.Changed += _window.OnCountdownChanged; _window.OnCountdownChanged(); }
        catch (Exception ex) { Log("倒计时: " + ex.Message); }
        try { _todo = new TodoFile(_store); _window.Todo = _todo; _todo.Changed += () => _window.SetTodos(_todo.Open); _window.SetTodos(_todo.Open); }
        catch (Exception ex) { Log("待办: " + ex.Message); }
        _toasts = new ToastMirror(_store);
        _ = _toasts.StartAsync();
        _ = new InfoStationWatcher(_store, Http).Loop();
        _media = new MediaWatcher(Dispatcher);
        _window.Media = _media;
        _media.Changed += () => _window.OnMediaChanged();
        _ = _media.StartAsync();
        _tick.Tick += (_, _) =>
        {
            _tickN++;
            _timer.Tick();
            _sys.Tick(_tickN);
            _flash?.OnForeground(Native.GetForegroundWindow());
            if (_tickN % 3 == 0) { try { _harness.Tick(); } catch (Exception ex) { Log("DSH: " + ex.Message); } }
            if (_tickN % 5 == 0) _store.Expire();
            if (DateTime.Today != _day) { _day = DateTime.Today; _countdown?.Refresh(); }
        };
        _tick.Start();
        Log("providers started");
    }

    // ───────────── 收件箱：给 WSL NAT 模式等连不上 localhost 的场景 ─────────────
    private void SetupInbox()
    {
        _inboxWatcher = new FileSystemWatcher(InboxDir, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
        _inboxWatcher.Created += (_, _) => Dispatcher.BeginInvoke(DrainInbox, DispatcherPriority.Background);
        _inboxWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(DrainInbox, DispatcherPriority.Background);
        DrainInbox();
    }

    private void DrainInbox()
    {
        foreach (var f in Directory.EnumerateFiles(InboxDir, "*.json").Take(50).ToList())
        {
            string text;
            try { text = File.ReadAllText(f); File.Delete(f); }
            catch (IOException) { continue; } // 还没写完，下次再取
            catch (UnauthorizedAccessException) { continue; }
            var trimmed = text.TrimStart();
            if (trimmed.Contains("\"activities\"") || trimmed.Contains("\"kind\"") || trimmed.StartsWith('['))
                foreach (var d in QislandIngest.Parse(text)) _store.Upsert(d);
            else if (ApiServer.ParsePush(text, out var p) is { } d)
                _store.Upsert(d, p);
        }
    }

    // ───────────── 天气（成都，20 分钟一次；wttr.in 有逐 3 小时预报和日出日落，Open-Meteo 兜底） ─────────────
    private async Task WeatherLoop()
    {
        const string url = "https://api.open-meteo.com/v1/forecast?latitude=30.66&longitude=104.07&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=Asia%2FShanghai&forecast_days=1";
        while (true)
        {
            var wait = TimeSpan.FromMinutes(20);
            var ok = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                var d = WttrParser.Parse(await Http.GetStringAsync("https://wttr.in/Chengdu?format=j1&lang=zh", cts.Token), DateTime.Now);
                if (d != null) { _window?.SetWeather(d.Now, d); ok = true; }
            }
            catch (Exception ex) { Log("wttr: " + ex.Message); }
            if (!ok)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var w = WeatherParser.ParseForecast(await Http.GetStringAsync(url, cts.Token));
                    if (w != null) { _window?.SetWeather(w, null); ok = true; }
                }
                catch (Exception ex) { Log("Open-Meteo: " + ex.Message); }
            }
            if (!ok) wait = TimeSpan.FromMinutes(3);
            await Task.Delay(wait);
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

    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var focus = new System.Windows.Forms.ToolStripMenuItem("计时");
        foreach (var (m, label) in new[] { (25, "专注 25 分钟"), (50, "专注 50 分钟"), (5, "计时 5 分钟"), (10, "计时 10 分钟") })
            focus.DropDownItems.Add(label, null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Start(m, label.StartsWith("专注") ? "专注" : "计时")));
        focus.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        focus.DropDownItems.Add("取消计时", null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Cancel()));
        menu.Items.Add(focus);
        var look = new System.Windows.Forms.ToolStripMenuItem("外观");
        foreach (var l in new[] { Theme.Look.Dark, Theme.Look.Light, Theme.Look.Glass })
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(Theme.LookName(l));
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => { _window?.ApplyLook(l); SaveLook(l); });
            look.DropDownItems.Add(item);
        }
        look.DropDownOpening += (_, _) =>
        {
            foreach (System.Windows.Forms.ToolStripMenuItem i in look.DropDownItems) i.Checked = i.Text == Theme.LookName(Theme.Current);
        };
        menu.Items.Add(look);
        menu.Items.Add("编辑倒计时", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Countdowns.PathName + "\"") { UseShellExecute = true }));
        var keys = new System.Windows.Forms.ToolStripMenuItem("接管音量键") { CheckOnClick = true, Checked = !File.Exists(VolumeWatcher.OffFlag), ToolTipText = "音量键改由灵动岛显示，不再弹出系统音量浮窗" };
        keys.CheckedChanged += (_, _) => _volume?.HookKeys(keys.Checked);
        menu.Items.Add(keys);
        var auto = new System.Windows.Forms.ToolStripMenuItem("开机自启");
        auto.Click += (_, _) => Autostart.Set(!Autostart.Enabled);
        menu.Opening += (_, _) => auto.Checked = Autostart.Enabled;
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

    private void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _api?.Dispose();
        _volume?.Dispose();
        _inboxWatcher?.Dispose();
        Shutdown();
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
