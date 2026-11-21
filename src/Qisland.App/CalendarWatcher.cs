using System.IO;
using System.Net.Http;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 日历订阅：.ics「私密地址」（Outlook / Google / 飞书 / 钉钉都能导出），15 分钟拉一次，本地解析。
/// · 链接里带口令：用 DPAPI 加密存在 calendars.dat，日志里只写域名；
/// · 拉到的内容也加密缓存一份（calendars-cache.dat），开机没网时照样有日程；
/// · 未来 3 小时内的日程进展开后的列表（安静，不占胶囊）；开始前 10 分钟弹一条临时提醒，有会议链接的点一下就入会。
/// </summary>
public sealed class CalendarWatcher
{
    public const string Source = "calendar", ListSource = "agenda";
    private readonly string _dataDir;
    private readonly Action<string> _log;
    private string FeedsPath => Path.Combine(_dataDir, "calendars.dat");
    private string CachePath => Path.Combine(_dataDir, "calendars-cache.dat");
    private string LocalPath => Path.Combine(_dataDir, "local-calendar.dat");
    private LocalCalendar _local = new();
    /// <summary>本机日程（不依赖订阅；DPAPI 加密存在 local-calendar.dat）。</summary>
    public LocalCalendar Local => _local;
    public bool HasAny => _feeds.Count > 0 || _local.Count > 0;
    public string LastSaveError { get; private set; } = "";
    private readonly ActivityStore _store;
    private readonly HttpClient _http;
    private readonly HashSet<string> _reminded = [];
    private readonly Dictionary<string, CalEvent> _shownReminders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _feedErrors = new(StringComparer.Ordinal);
    private readonly object _feedsGate = new();
    private readonly Func<string, bool> _featureOn;
    private readonly Func<bool> _netReady;
    // 订阅 + 缓存；拉取时领票，期间退订（或退订后重订）的结果不会被收下
    private readonly CalendarFeeds _feeds = new();
    private readonly Generation _gen = new();
    public IReadOnlyList<CalEvent> Events { get; private set; } = [];
    public DateTime? LastOk { get; private set; }
    public string LastError { get; private set; } = "";
    public event Action? Changed;
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CalendarWatcher(ActivityStore store, HttpClient http) : this(store, http, App.DataDir) { }

    internal CalendarWatcher(ActivityStore store, HttpClient http, string dataDir, Action<string>? log = null, Func<string, bool>? featureOn = null, Func<bool>? netReady = null)
    {
        _store = store;
        _http = http;
        _dataDir = dataDir;
        _log = log ?? App.Log;
        _featureOn = featureOn ?? Features.On;
        _netReady = netReady ?? App.NetReady;
    }

    internal Task LoadForTestAsync() => LoadAsync();
    internal Task WaitForRebuildAsync() => _rebuild;

    internal async Task ReplaceFeedContentForTestAsync(string url, string ics)
    {
        _feeds.Apply(_feeds.BeginFetch(url), ics);
        await Rebuild();
    }

    internal Task RunRefreshLoopForTestAsync(int iterations, Func<Task>? wait = null) =>
        RunRefreshLoopAsync(iterations, wait);

    internal string FeedErrorForTest(string url)
    {
        lock (_feedErrors) return _feedErrors.GetValueOrDefault(url, "");
    }

    public IReadOnlyDictionary<string, string> FeedErrors
    {
        get { lock (_feedErrors) return new Dictionary<string, string>(_feedErrors, StringComparer.Ordinal); }
    }

    internal static Dictionary<string, CalEvent> BuildReminderIndex(IEnumerable<CalEvent> events, Action<string>? log = null)
    {
        var byId = new Dictionary<string, CalEvent>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var id = CalendarReminder.ActivityId(e);
            if (!byId.TryAdd(id, e)) log?.Invoke($"日历提醒: 日程身份重复，保留一条 {id}");
        }
        return byId;
    }

    private Task? _load;
    private Task _rebuild = Task.CompletedTask;

    /// <summary>读加密的订阅和缓存（后台线程，不占启动时间）。</summary>
    private async Task LoadAsync()
    {
        var loaded = await Task.Run(() =>
        {
            var f = File.Exists(FeedsPath) ? (Dpapi.Load(FeedsPath) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : [];
            var c = new Dictionary<string, string>();
            try
            {
                var cached = f.Count > 0 && File.Exists(CachePath) ? Dpapi.Load(CachePath) : null;
                if (cached != null)
                    foreach (var block in cached.Split("\u0001\u0001", StringSplitOptions.RemoveEmptyEntries))
                    {
                        var i = block.IndexOf('\u0001');
                        if (i > 0 && f.Contains(block[..i])) c[block[..i]] = block[(i + 1)..];
                    }
            }
            catch { }
            return (f, c, LocalCalendar.FromJson(Dpapi.Load(LocalPath)));
        });
        var (feeds, cache, local) = loaded;
        if (_local.Count == 0) _local = local;
        foreach (var u in feeds) if (!_feeds.Contains(u)) _feeds.Add(u, cache.GetValueOrDefault(u));
        if (_feeds.Count > 0 || _local.Count > 0) _log($"日历: {_feeds.Count} 个订阅，缓存 {_feeds.Cache.Count} 个，本机日程 {_local.Count} 个");
        await Rebuild();
    }

    public IReadOnlyList<string> Feeds => _feeds.Urls;
    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "?";

    /// <summary>webcal:// 改成 https://；只收 http(s)。</summary>
    public static string? Normalize(string? url)
    {
        url = url?.Trim().Trim('"');
        if (string.IsNullOrEmpty(url)) return null;
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;
    }

    /// <summary>加一个订阅：先拉一次验证是不是日历，返回（成功, 说明）。</summary>
    public async Task<(bool ok, string msg)> Add(string raw)
    {
        if (Normalize(raw) is not { } url) return (false, "不是有效的 http(s) / webcal 链接");
        try { if (_load != null) await _load; } catch { }   // 先等已保存的订阅读完，免得保存时把它们覆盖掉
        if (_feeds.Contains(url)) return (false, "这个日历已经订阅过了");
        try
        {
            var ics = await Fetch(url);
            if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return (false, "这个链接返回的不是 .ics 日历");
            var sourceId = CalendarIdentity.FeedId(url);
            var parsed = await Task.Run(() => CalendarIcs.Expand(ics, DateTime.Today, DateTime.Today.AddDays(8), null, sourceId));
            _ = BuildReminderIndex(CalendarIcs.Dedupe(Events.Concat(parsed)), _log);
            lock (_feedsGate)
            {
                if (_feeds.Contains(url)) return (false, "这个日历已经订阅过了");
                if (!TryPersistFeedConfig(_feeds.Urls.Append(url).ToArray()))
                    return (false, "订阅保存失败，请关闭占用日历文件的程序后重试");
                _feeds.Add(url, ics);
            }
            PersistCache();
            try { await Rebuild(); }
            catch (Exception ex) { _log("日历: 新订阅已保存，首次刷新稍后重试 · " + ex.Message); }
            var n = parsed.Count;
            _log($"日历: 添加订阅 {Host(url)}，未来一周 {n} 个日程");
            return (true, $"已添加（{Host(url)}），未来一周有 {n} 个日程");
        }
        catch (Exception ex) { _log($"日历: 添加 {Host(url)} 失败 {ex.GetType().Name}"); return (false, "新增订阅失败：" + Describe(ex)); }
    }

    public bool Remove(string url)
    {
        lock (_feedsGate)
        {
            if (!_feeds.Contains(url)) return false;
            if (!TryPersistFeedConfig(_feeds.Urls.Where(u => u != url).ToArray()))
            {
                LastSaveError = "订阅保存失败，请关闭占用日历文件的程序后重试";
                return false;
            }
            _feeds.Remove(url);
        }
        lock (_feedErrors) _feedErrors.Remove(url);
        PersistCache();
        _rebuild = Rebuild();
        _log($"日历: 删除订阅 {Host(url)}");
        return true;
    }

    public void RefreshNow() => _kick.TrySetResult();

    /// <summary>新增 / 修改本机日程。写盘失败时内存退回原样并返回 WriteFailed（窗口留着让用户重试），说明在 LastSaveError。</summary>
    public LocalSaveResult SaveLocal(LocalEvent e) => Done(_local.TrySave(e, PersistLocal));

    public LocalSaveResult DeleteLocal(string id) => Done(_local.TryDelete(id, PersistLocal));

    private LocalSaveResult Done(LocalSaveResult r)
    {
        if (r == LocalSaveResult.Saved) _ = Rebuild();
        return r;
    }

    private bool PersistLocal(string json)
    {
        string? err;
        var ok = _local.Count == 0 ? AtomicFile.TryDelete(LocalPath, out err) : Dpapi.TrySave(LocalPath, json, out err);
        LastSaveError = ok ? "" : err ?? "写入失败";
        if (!ok) _log("本机日程保存失败: " + LastSaveError);
        return ok;
    }

    /// <summary>订阅配置是必须落盘的状态：写成功之后才允许改内存。</summary>
    private bool TryPersistFeedConfig(IReadOnlyList<string> urls)
    {
        string? error;
        var ok = urls.Count == 0
            ? AtomicFile.TryDelete(FeedsPath, out error)
            : Dpapi.TrySave(FeedsPath, string.Join("\n", urls), out error);
        LastSaveError = ok ? "" : "订阅保存失败，请关闭占用日历文件的程序后重试";
        if (!ok) _log("日历订阅保存失败: " + (error ?? "写入失败"));
        return ok;
    }

    /// <summary>日历内容只是缓存：写不进去时下次还能重新拉，不能影响订阅配置的成败。</summary>
    private void PersistCache()
    {
        string? error;
        var ok = _feeds.Count == 0
            ? AtomicFile.TryDelete(CachePath, out error)
            : Dpapi.TrySave(CachePath, string.Join("\u0001\u0001", _feeds.Cache.Select(kv => kv.Key + "\u0001" + kv.Value)), out error);
        if (!ok) _log("日历缓存保存失败: " + (error ?? "写入失败"));
    }

    private async Task<string> Fetch(string url)
    {
        Counters.Hit("cal.fetch");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > 8 << 20) throw new IOException("日历文件太大");
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>后台循环：15 分钟一次；电脑醒来、点「立即刷新」时提前。</summary>
    public async Task Loop()
    {
        _load = LoadAsync();
        try { await _load; } catch (Exception ex) { _log("日历读取: " + ex.Message); }
        await Task.Delay(TimeSpan.FromSeconds(30));
        await RunRefreshLoopAsync();
    }

    private async Task RunRefreshLoopAsync(int? iterations = null, Func<Task>? wait = null)
    {
        var done = 0;
        while (iterations is not { } max || done < max)
        {
            try
            {
                if (_feeds.Count > 0 && _featureOn(Features.Calendar) && _netReady())
                    await RefreshFeedsAsync();
            }
            catch (Exception ex)
            {
                LastError = "刷新异常：" + ex.GetType().Name;
                _log("日历刷新循环: " + ex.Message);
            }
            done++;
            if (iterations is { } limit && done >= limit) break;
            try
            {
                if (wait != null) await wait();
                else if (iterations == null)
                {
                    var kick = _kick.Task;
                    await Task.WhenAny(App.Nap(TimeSpan.FromMinutes(15)), kick);
                    if (kick.IsCompleted) _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            catch (Exception ex) { _log("日历刷新等待: " + ex.GetType().Name); }
        }
    }

    private async Task RefreshFeedsAsync()
    {
        var ok = 0;
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var url in _feeds.Urls.ToList())
        {
            var ticket = _feeds.BeginFetch(url);
            try
            {
                var ics = await Fetch(url);
                if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("返回的不是 .ics 日历");
                var sourceId = CalendarIdentity.FeedId(url);
                var parsed = await Task.Run(() => CalendarIcs.Expand(ics, DateTime.Today.AddDays(-1), DateTime.Today.AddDays(8), null, sourceId));
                _ = BuildReminderIndex(parsed, _log);
                if (_feeds.Apply(ticket, ics)) ok++;
            }
            catch (Exception ex)
            {
                var reason = Describe(ex);
                errors[url] = reason;
                _log($"日历拉取失败 {Host(url)}: {reason}");
            }
        }
        lock (_feedErrors)
        {
            _feedErrors.Clear();
            foreach (var (url, reason) in errors) _feedErrors[url] = reason;
        }
        LastError = string.Join("；", errors.Select(kv => $"{Host(kv.Key)}: {kv.Value}"));
        if (ok > 0) { LastOk = DateTime.Now; PersistCache(); }
        await Rebuild();
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status}",
        HttpRequestException => "网络请求失败",
        OperationCanceledException => "请求超时",
        IOException io => io.Message,
        _ => ex.GetType().Name,
    };

    /// <summary>解析放到后台线程（大的日历文件几十毫秒），结果回到 UI 线程再换上。</summary>
    private async Task Rebuild()
    {
        var gen = _gen.Next();
        var sources = _feeds.SourcesWithUrls();
        var from = DateTime.Today.AddDays(-1);
        var local = _local.Expand(from, DateTime.Today.AddDays(8)).ToList();
        var events = await Task.Run(() =>
        {
            var all = new List<CalEvent>();
            foreach (var (url, ics) in sources)
            {
                try { all.AddRange(CalendarIcs.Expand(ics, from, DateTime.Today.AddDays(8), null, CalendarIdentity.FeedId(url))); }
                catch (Exception ex) { _log("日历解析: " + ex.Message); }
            }
            all.AddRange(local);
            return CalendarIcs.Dedupe(all);
        });
        if (!_gen.IsCurrent(gen)) return;   // 期间又有更新的重建（比如刚退订），旧结果作废
        Events = events;
        Tick();
        Changed?.Invoke();
    }

    private DateTime _day = DateTime.Today;

    /// <summary>今天还没结束的、以及之后几天的日程（给组件用）。</summary>
    public IReadOnlyList<CalEvent> Upcoming(int max)
    {
        var now = DateTime.Now;
        return Events.Where(e => e.End > now && (!e.AllDay || e.Start.Date >= DateTime.Today)).Take(max).ToList();
    }

    /// <summary>每 30 秒调一次：更新列表里的「接下来」、到点前 10 分钟提醒。</summary>
    public void Tick()
    {
        if (_day != DateTime.Today) { _day = DateTime.Today; _ = Rebuild(); return; }   // 过了零点：展开窗口往后挪一天
        if (!_featureOn(Features.Calendar) || !HasAny)
        {
            _store.ReplaceSource(ListSource, []);
            ClearCalendarReminders();
            return;
        }
        var now = DateTime.Now;
        var soon = Events.Where(e => !e.AllDay && e.End > now && e.Start - now <= TimeSpan.FromHours(3)).Take(4).ToList();
        _store.ReplaceSource(ListSource, soon.Select(e => new IslandActivity("agenda:" + e.Key, ListSource, e.Title,
            Line(e, now), e.Start <= now ? ActivityKind.Running : ActivityKind.Info, 30, OpenUri: e.JoinUrl)), sticky: true, quiet: true);
        SyncCalendarReminders(now);

        foreach (var e in Events)
        {
            if (e.AllDay) continue;
            var left = e.Start - now;
            if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(10) || !_reminded.Add(e.Key)) continue;
            ShowReminder(e, now);
            break;
        }
        var liveKeys = Events.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        _reminded.RemoveWhere(k => !liveKeys.Contains(k));
        if (_reminded.Count > 200) _reminded.Clear();
    }

    /// <summary>每次快照都核对已上岛的提醒：事件没了就撤回，链接 / 时间 / 标题变了就原地刷新。</summary>
    private void SyncCalendarReminders(DateTime now)
    {
        var byId = BuildReminderIndex(Events, _log);
        var cards = _store.Ranked()
            .Where(a => a.Source == Source && a.Id.StartsWith(CalendarReminder.IdPrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var card in cards)
        {
            if (!byId.TryGetValue(card.Id, out var current))
            {
                _store.Remove(card.Id);
                _shownReminders.Remove(card.Id);
                _reminded.Remove(card.Id[CalendarReminder.IdPrefix.Length..]);
                continue;
            }

            if (!_shownReminders.TryGetValue(card.Id, out var shown))
            {
                if (card.OpenUri != current.JoinUrl) ShowReminder(current, now);
                _shownReminders[card.Id] = current;
                continue;
            }

            if (SameReminder(shown, current) && card.OpenUri == current.JoinUrl) continue;
            ShowReminder(current, now);
            _shownReminders[card.Id] = current;
        }

        foreach (var id in _shownReminders.Keys.Where(id => _store.Get(id) == null).ToList())
            _shownReminders.Remove(id);
    }

    private void ShowReminder(CalEvent e, DateTime now)
    {
        var left = e.Start - now;
        var mins = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
        _store.Upsert(new IslandActivity(CalendarReminder.ActivityId(e), Source, $"{mins} 分钟后 · {e.Title}",
            e.JoinUrl != null ? $"{e.Start:HH:mm} 开始 · 点这里加入{e.JoinApp}" : Line(e, now), ActivityKind.Warning, 80,
            DurationSeconds: 120, ResetTtl: true, OpenUri: e.JoinUrl, Replace: true));
        _shownReminders[CalendarReminder.ActivityId(e)] = e;
        _log($"日历提醒: {mins} 分钟后有日程{(e.JoinUrl != null ? "（带会议链接 " + e.JoinApp + "）" : "")}");
    }

    private void ClearCalendarReminders()
    {
        foreach (var card in _store.Ranked()
                     .Where(a => a.Source == Source && a.Id.StartsWith(CalendarReminder.IdPrefix, StringComparison.Ordinal))
                     .ToList())
            _store.Remove(card.Id);
        _shownReminders.Clear();
        _reminded.Clear();
    }

    private static bool SameReminder(CalEvent a, CalEvent b) =>
        a.Title == b.Title && a.Start == b.Start && a.End == b.End && a.JoinUrl == b.JoinUrl && a.JoinApp == b.JoinApp;

    public static string Line(CalEvent e, DateTime now)
    {
        var when = e.Start <= now ? $"进行中 · {e.End:HH:mm} 结束" : $"{e.Start:HH:mm}–{e.End:HH:mm}";
        var parts = new List<string> { when };
        if (e.Location.Length > 0 && e.Location.Length <= 20 && !(e.JoinApp != null && e.Location.Contains(e.JoinApp))) parts.Add(e.Location);
        if (e.JoinApp != null) parts.Add(e.JoinApp);
        return string.Join(" · ", parts);
    }
}
