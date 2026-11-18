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
    private static string FeedsPath => Path.Combine(App.DataDir, "calendars.dat");
    private static string CachePath => Path.Combine(App.DataDir, "calendars-cache.dat");
    private readonly ActivityStore _store;
    private readonly HttpClient _http;
    private readonly HashSet<string> _reminded = [];
    private List<string> _feeds = [];
    private readonly Dictionary<string, string> _cache = [];   // url → ics
    public IReadOnlyList<CalEvent> Events { get; private set; } = [];
    public DateTime? LastOk { get; private set; }
    public string LastError { get; private set; } = "";
    public event Action? Changed;
    private TaskCompletionSource _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CalendarWatcher(ActivityStore store, HttpClient http)
    {
        _store = store; _http = http;
        _feeds = (Dpapi.Load(FeedsPath) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        try
        {
            var cached = Dpapi.Load(CachePath);
            if (cached != null)
                foreach (var block in cached.Split("\u0001\u0001", StringSplitOptions.RemoveEmptyEntries))
                {
                    var i = block.IndexOf('\u0001');
                    if (i > 0 && _feeds.Contains(block[..i])) _cache[block[..i]] = block[(i + 1)..];
                }
        }
        catch { }
        Rebuild();
    }

    public IReadOnlyList<string> Feeds => _feeds;
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
        if (_feeds.Contains(url)) return (false, "这个日历已经订阅过了");
        try
        {
            var ics = await Fetch(url);
            if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return (false, "这个链接返回的不是 .ics 日历");
            _feeds.Add(url);
            _cache[url] = ics;
            Save();
            Rebuild();
            var n = CalendarIcs.Expand(ics, DateTime.Today, DateTime.Today.AddDays(8)).Count;
            App.Log($"日历: 添加订阅 {Host(url)}，未来一周 {n} 个日程");
            return (true, $"已添加（{Host(url)}），未来一周有 {n} 个日程");
        }
        catch (Exception ex) { App.Log($"日历: 添加 {Host(url)} 失败 {ex.GetType().Name}"); return (false, "拉取失败：" + ex.Message); }
    }

    public void Remove(string url)
    {
        if (!_feeds.Remove(url)) return;
        _cache.Remove(url);
        Save();
        Rebuild();
        App.Log($"日历: 删除订阅 {Host(url)}");
    }

    public void RefreshNow() => _kick.TrySetResult();

    private void Save()
    {
        try
        {
            if (_feeds.Count == 0) { File.Delete(FeedsPath); File.Delete(CachePath); return; }
            Dpapi.Save(FeedsPath, string.Join("\n", _feeds));
            Dpapi.Save(CachePath, string.Join("\u0001\u0001", _cache.Select(kv => kv.Key + "\u0001" + kv.Value)));
        }
        catch (Exception ex) { App.Log("日历保存: " + ex.Message); }
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
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (_feeds.Count > 0 && Features.On(Features.Calendar) && App.NetReady())
            {
                var ok = 0; var err = "";
                foreach (var url in _feeds.ToList())
                {
                    try { var ics = await Fetch(url); if (ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) { _cache[url] = ics; ok++; } }
                    catch (Exception ex) { err = $"{Host(url)}: {ex.GetType().Name}"; App.Log("日历拉取失败 " + err); }
                }
                if (ok > 0) { LastOk = DateTime.Now; Save(); }
                LastError = err;
                Rebuild();
            }
            var kick = _kick.Task;
            await Task.WhenAny(App.Nap(TimeSpan.FromMinutes(15)), kick);
            if (kick.IsCompleted) _kick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void Rebuild()
    {
        var from = DateTime.Today.AddDays(-1);
        var all = new List<CalEvent>();
        foreach (var ics in _cache.Values)
        {
            try { all.AddRange(CalendarIcs.Expand(ics, from, DateTime.Today.AddDays(8))); }
            catch (Exception ex) { App.Log("日历解析: " + ex.Message); }
        }
        Events = all.GroupBy(e => (e.Title, e.Start)).Select(g => g.First()).OrderBy(e => e.Start).ToList();
        Tick();
        Changed?.Invoke();
    }

    /// <summary>今天还没结束的、以及之后几天的日程（给组件用）。</summary>
    public IReadOnlyList<CalEvent> Upcoming(int max)
    {
        var now = DateTime.Now;
        return Events.Where(e => e.End > now && (!e.AllDay || e.Start.Date >= DateTime.Today)).Take(max).ToList();
    }

    /// <summary>每 30 秒调一次：更新列表里的「接下来」、到点前 10 分钟提醒。</summary>
    public void Tick()
    {
        if (!Features.On(Features.Calendar) || _feeds.Count == 0)
        {
            _store.ReplaceSource(ListSource, []);
            return;
        }
        var now = DateTime.Now;
        var soon = Events.Where(e => !e.AllDay && e.End > now && e.Start - now <= TimeSpan.FromHours(3)).Take(4).ToList();
        _store.ReplaceSource(ListSource, soon.Select(e => new IslandActivity("agenda:" + e.Key, ListSource, e.Title,
            Line(e, now), e.Start <= now ? ActivityKind.Running : ActivityKind.Info, 30, OpenUri: e.JoinUrl)), sticky: true, quiet: true);

        foreach (var e in Events)
        {
            if (e.AllDay) continue;
            var left = e.Start - now;
            if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(10) || !_reminded.Add(e.Key)) continue;
            var mins = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes));
            _store.Upsert(new IslandActivity("cal:remind", Source, $"{mins} 分钟后 · {e.Title}",
                e.JoinUrl != null ? $"{e.Start:HH:mm} 开始 · 点这里加入{e.JoinApp}" : Line(e, now), ActivityKind.Warning, 80,
                DurationSeconds: 120, ResetTtl: true, OpenUri: e.JoinUrl));
            App.Log($"日历提醒: {mins} 分钟后有日程{(e.JoinUrl != null ? "（带会议链接 " + e.JoinApp + "）" : "")}");
            break;
        }
        if (_reminded.Count > 200) _reminded.Clear();
    }

    public static string Line(CalEvent e, DateTime now)
    {
        var when = e.Start <= now ? $"进行中 · {e.End:HH:mm} 结束" : $"{e.Start:HH:mm}–{e.End:HH:mm}";
        var parts = new List<string> { when };
        if (e.Location.Length > 0 && e.Location.Length <= 20 && !(e.JoinApp != null && e.Location.Contains(e.JoinApp))) parts.Add(e.Location);
        if (e.JoinApp != null) parts.Add(e.JoinApp);
        return string.Join(" · ", parts);
    }
}
