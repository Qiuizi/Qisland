using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Qisland.Core;

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
    private TodoFile? _todo;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _tickN;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qisland");
    public static string InboxDir => Path.Combine(DataDir, "inbox");

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\Qisland.Island", out var first);
        if (!first) return 0;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        _self = app;
        app.Startup += (_, _) => app.Boot(args);
        return app.Run();
    }

    private void Boot(string[] args)
    {
        Directory.CreateDirectory(InboxDir);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Qisland/0.2 (+https://github.com/Qiuizi)");
        DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); e.Handled = true; };

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
        _window!.OpenRequested = Launcher.Open;
        _timer = new FocusTimer(_store);
        _harness = new HarnessWatcher(_store);
        try { _flash = new MessengerFlash(_store); _flash.Start(); } catch (Exception ex) { Log("ShellHook: " + ex.Message); }
        try { _todo = new TodoFile(_store); _todo.Changed += () => _window.SetTodos(_todo.Open); _window.SetTodos(_todo.Open); }
        catch (Exception ex) { Log("待办: " + ex.Message); }
        _toasts = new ToastMirror(_store);
        _ = _toasts.StartAsync();
        _ = new InfoStationWatcher(_store, Http).Loop();
        _tick.Tick += (_, _) =>
        {
            _tickN++;
            _timer.Tick();
            _flash?.OnForeground(Native.GetForegroundWindow());
            if (_tickN % 3 == 0) { try { _harness.Tick(); } catch (Exception ex) { Log("DSH: " + ex.Message); } }
            if (_tickN % 5 == 0) _store.Expire();
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

    // ───────────── 天气（Open-Meteo，成都，20 分钟一次） ─────────────
    private async Task WeatherLoop()
    {
        const string url = "https://api.open-meteo.com/v1/forecast?latitude=30.66&longitude=104.07&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m&daily=temperature_2m_max,temperature_2m_min&timezone=Asia%2FShanghai&forecast_days=1";
        while (true)
        {
            var wait = TimeSpan.FromMinutes(20);
            WeatherSnapshot? w = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                w = WeatherParser.ParseForecast(await Http.GetStringAsync(url, cts.Token));
            }
            catch (Exception ex) { Log("Open-Meteo: " + ex.Message); }
            if (w is null)
            {
                // 国内直连 Open-Meteo 经常超时，退到 wttr.in
                try { w = ParseWttr(await Http.GetStringAsync("https://wttr.in/Chengdu?format=j1")); }
                catch (Exception ex) { Log("wttr: " + ex.Message); }
            }
            if (w is not null) _window?.SetWeather(w); else wait = TimeSpan.FromMinutes(3);
            await Task.Delay(wait);
        }
    }

    internal static WeatherSnapshot? ParseWttr(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var cur = root.GetProperty("current_condition")[0];
        static double D(System.Text.Json.JsonElement e, string n) => double.Parse(e.GetProperty(n).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var temp = D(cur, "temp_C");
        var code = int.Parse(cur.GetProperty("weatherCode").GetString()!);
        var day = root.GetProperty("weather")[0];
        var wmo = code switch
        {
            113 => 0,
            116 => 2,
            119 or 122 => 3,
            143 or 248 or 260 => 45,
            200 or (>= 386 and <= 395) => 95,
            179 or 182 or 185 or 227 or 230 or (>= 317 and <= 338) or (>= 368 and <= 377) => 71,
            _ => 61,
        };
        return new WeatherSnapshot(temp, wmo, D(cur, "FeelsLikeC"), D(cur, "humidity"), 0, D(cur, "precipMM"), D(day, "maxtempC"), D(day, "mintempC"));
    }

    // ───────────── 托盘 ─────────────
    private void SetupTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var focus = new System.Windows.Forms.ToolStripMenuItem("计时");
        foreach (var (m, label) in new[] { (25, "专注 25 分钟"), (50, "专注 50 分钟"), (5, "计时 5 分钟"), (10, "计时 10 分钟") })
            focus.DropDownItems.Add(label, null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Start(m, label.StartsWith("专注") ? "专注" : "计时")));
        focus.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        focus.DropDownItems.Add("取消计时", null, (_, _) => Dispatcher.BeginInvoke(() => _timer?.Cancel()));
        menu.Items.Add(focus);
        menu.Items.Add("编辑待办", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", "\"" + TodoFile.PathName + "\"") { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("发送测试卡片", null, (_, _) => Dispatcher.BeginInvoke(Demo));
        menu.Items.Add("清空活动", null, (_, _) => Dispatcher.BeginInvoke(() => { foreach (var a in _store.Ranked().ToList()) _store.Remove(a.Id); }));
        menu.Items.Add("打开数据目录", null, (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", DataDir) { UseShellExecute = true }));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出 Qisland", null, (_, _) => Dispatcher.BeginInvoke(Quit));
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Qisland",
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    private void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _api?.Dispose();
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
