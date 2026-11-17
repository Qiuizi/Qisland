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
    private CodexQuota? _quota;
    private VolumeWatcher? _volume;
    private SystemWatch _sys = null!;
    private readonly PerfGuard _perf = new();
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
        try { var n = ChangeLedger.Sync().Count; if (n > 0) Log($"改动账本: 对其他软件有 {n} 处改动（托盘 → 对其他软件的改动）"); } catch (Exception ex) { Log("改动账本: " + ex.Message); }
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
            _approvals = new ApprovalBroker(a => Dispatcher.BeginInvoke(a));
            _window.Approvals = _approvals; _approvals.Changed += _window.OnApprovalsChanged;
            if (_api != null) _api.Approval = _approvals.Request;
            if (_api != null) _api.Bench = () => _window?.RunBench();
            if (_api != null) { _api.PerfJson = _perf.Json; _api.Peek = s => _window?.PeekFor(s); }
            PerfGuard.Changed += () => _window?.OnPerfChanged();
            _quota = new CodexQuota(a => Dispatcher.BeginInvoke(a));
            _window.Quota = _quota; _quota.Changed += _window.OnQuotaChanged;
        }
        catch (Exception ex) { Log("Codex: " + ex.Message); }
        try { _todo = new TodoFile(_store); _window.Todo = _todo; _todo.Changed += () => _window.SetTodos(_todo.Open); _window.SetTodos(_todo.Open); }
        catch (Exception ex) { Log("待办: " + ex.Message); }
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
        };
        _tick.Start();
        Log("providers started");
    }

    private void YuketangLoginOrOut()
    {
        if (_ykt == null) return;
        if (_ykt.LoggedIn && !_ykt.Expired)
        {
            if (System.Windows.MessageBox.Show($"退出雨课堂账号 {_ykt.UserName}？本机保存的登录状态会被删除。", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK) _ykt.Logout();
            return;
        }
        var ykt = _ykt;
        var w = new QrLoginWindow("雨课堂扫码登录", "用微信扫一扫，登录后只读取各课程的作业和截止时间。", "请用微信扫码，并在手机上确认",
            async () => { var qr = await YuketangService.RequestQr(); return (QrLoginWindow.Render(qr.Content), ct => ykt.WaitLogin(qr, ct)); });
        w.Closed += (_, _) =>
        {
            if (!w.Success) return;
            _store.Remove("ykt:expired");
            _store.Upsert(new IslandActivity("ykt:login", HomeworkHub.Source, "雨课堂已连接", $"{ykt.UserName} · 正在读取作业…", ActivityKind.Done, 60, DurationSeconds: 5));
        };
        w.Show();
    }

    private void XuexitongLoginOrOut()
    {
        if (_xxt == null) return;
        if (_xxt.LoggedIn && !_xxt.Expired)
        {
            if (System.Windows.MessageBox.Show($"退出学习通账号 {_xxt.UserName}？本机保存的登录状态会被删除。", "Qisland", MessageBoxButton.OKCancel) == MessageBoxResult.OK) _xxt.Logout();
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择课表 .ics（SCU Plus / WakeUp 导出）", Filter = "日历文件 (*.ics)|*.ics|所有文件|*.*" };
        if (dlg.ShowDialog() != true) return;
        if (!ClassWatcher.Import(dlg.FileName))
        {
            _store.Upsert(new IslandActivity("class:import", ClassWatcher.Source, "课表导入失败", "这个文件里没有找到课程（需要 .ics 日历格式）", ActivityKind.Error, 70, DurationSeconds: 6));
            return;
        }
        _classes?.Tick();
        var n = _classes?.Day(DateTime.Today).Count ?? 0;
        _store.Upsert(new IslandActivity("class:import", ClassWatcher.Source, "课表已导入", n > 0 ? $"今天有 {n} 节课" : "今天没有课", ActivityKind.Done, 60, DurationSeconds: 5));
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
        var ledger = new System.Windows.Forms.ToolStripMenuItem("对其他软件的改动");
        ledger.DropDownOpening += (_, _) =>
        {
            ledger.DropDownItems.Clear();
            var list = ChangeLedger.Sync();
            if (list.Count == 0) ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("没有（Qisland 没改过别的软件）") { Enabled = false });
            foreach (var e in list)
                ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem($"{e.App} · {e.What}") { Enabled = false, ToolTipText = $"{e.Target}\n{(e.Detected ? "启动时检测到" : "记录于")} {e.At:yyyy-MM-dd HH:mm}" });
            ledger.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
            var restore = new System.Windows.Forms.ToolStripMenuItem("一键还原…") { Enabled = list.Any(x => x.Id is "codex-hooks" or "dsh-bridge") };
            restore.Click += (_, _) => Dispatcher.BeginInvoke(RestoreChanges);
            ledger.DropDownItems.Add(restore);
            ledger.DropDownItems.Add("打开账本文件", null, (_, _) => { ChangeLedger.Sync(); if (File.Exists(ChangeLedger.PathName)) Process.Start(new ProcessStartInfo("notepad.exe", "\"" + ChangeLedger.PathName + "\"") { UseShellExecute = true }); });
        };
        ledger.DropDownItems.Add("…");
        menu.Items.Add(ledger);
        menu.Items.Add("编辑倒计时", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", "\"" + Countdowns.PathName + "\"") { UseShellExecute = true }));
        var lyr = new System.Windows.Forms.ToolStripMenuItem("显示歌词") { CheckOnClick = true, Checked = LyricsService.Enabled, ToolTipText = "播放音乐时胶囊里滚动显示当前这句歌词（网易云 / LRCLIB）" };
        lyr.CheckedChanged += (_, _) => Dispatcher.BeginInvoke(() => { LyricsService.Enabled = lyr.Checked; _lyrics?.OnTrack(null); _lyrics?.OnTrack(_media?.Current); });
        menu.Items.Add(lyr);
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
