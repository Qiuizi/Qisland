using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Threading;
using Qisland.Core;
using WinIslands.UI;

namespace WinIslands.Services;

/// <summary>
/// 把各个提供方的快照送进现有上岛队列。媒体仍走 SMTC，不在这里再推一张卡片。
/// </summary>
public sealed class QislandHost : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IslandViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly AgentSessionProvider _agents = new();
    private readonly InfoStationProvider _info = new();
    private readonly CourseDeskProvider _courses = new();
    private readonly DeepSeekHarnessProvider _deepSeek = new();
    private readonly Dictionary<string, string> _signatures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _ids = new(StringComparer.Ordinal);
    private int _tick;
    private bool _busy;
    private bool _disposed;

    public QislandHost(SettingsService settings, IslandViewModel vm)
    {
        _settings = settings;
        _vm = vm;
        Directory.CreateDirectory(AppPaths.InboxDir);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => _ = OnTickAsync();
        _timer.Start();
    }

    public void ShowDeepSeekToast(string title, string body)
    {
        var settings = _settings.Current;
        if (!settings.QislandAgentsEnabled || !settings.QislandDeepSeekEnabled) return;
        _deepSeek.ApplyToast(title, body, settings.QislandNotifyPrivacy);
        Publish("deepseek", _deepSeek.Snapshot());
    }

    private async Task OnTickAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var settings = _settings.Current;
            DrainInbox();
            if (settings.QislandAgentsEnabled && settings.QislandDeepSeekEnabled)
                PollDeepSeek(settings.QislandNotifyPrivacy);
            else
                Publish("deepseek", Array.Empty<IslandActivity>());

            if (!settings.QislandAgentsEnabled)
                Publish("agents", Array.Empty<IslandActivity>());
            else if (_tick % 40 == 0)
                PollQuota();

            if (_tick % 20 == 0)
            {
                if (settings.QislandInfoStationEnabled) await PollInfoAsync(settings).ConfigureAwait(true);
                else Publish("info-station", Array.Empty<IslandActivity>());
            }
            if (_tick % 10 == 0)
            {
                if (settings.QislandCourseDeskEnabled) PollCourse(settings.CourseDeskSnapshotPath);
                else Publish("coursedesk", Array.Empty<IslandActivity>());
            }
            _tick++;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Qisland tick: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void DrainInbox()
    {
        foreach (var file in Directory.EnumerateFiles(AppPaths.InboxDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                File.Delete(file);
                foreach (var draft in QislandIngest.Parse(json))
                    PushDraft(draft);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"Inbox {Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private void PollDeepSeek(bool privacy)
    {
        var running = false;
        string title = "";
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!DeepSeekHarnessSignals.IsHarnessProcess(process.ProcessName)) continue;
                    running = true;
                    var window = process.MainWindowTitle;
                    if (!string.IsNullOrWhiteSpace(window)) title = window;
                }
                catch { /* 进程已退出 */ }
                finally { process.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"DeepSeek process scan: {ex.Message}");
        }
        _deepSeek.Enabled = true;
        _deepSeek.ApplyWindow(running, title, privacy);
        Publish("deepseek", _deepSeek.Snapshot());
    }

    private void PollQuota()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var files = new[]
        {
            Path.Combine(home, ".qisland", "quota.json"),
            Path.Combine(home, ".claude", "stats-cache.json"),
            Path.Combine(home, ".codex", "quota.json"),
        };
        foreach (var file in files) _agents.ApplyQuota(AgentQuotaReader.ReadFile(file));
        _agents.Enabled = true;
        Publish("agents", _agents.Snapshot());
    }

    private async Task PollInfoAsync(AppSettings settings)
    {
        var root = (settings.InfoStationBaseUrl ?? "").TrimEnd('/');
        if (root.Length == 0) return;
        _info.MinScore = Math.Clamp(settings.InfoStationMinScore, 0, 100);
        var today = DateOnly.FromDateTime(DateTime.Now);
        var items = await GetAsync(root + "/api/items?view=featured&sort=score&read=unread&limit=5", settings.InfoStationToken).ConfigureAwait(true);
        var brief = await GetAsync(root + "/api/briefs/today?refresh=false", settings.InfoStationToken).ConfigureAwait(true);
        var deadlines = await GetAsync(root + "/api/deadlines?horizon=14", settings.InfoStationToken).ConfigureAwait(true);
        if (items is not null) _info.ApplyItems(items);
        if (brief is not null) _info.ApplyBrief(brief, today);
        if (deadlines is not null) _info.ApplyDeadlines(deadlines);
        if (items is null && brief is null && deadlines is null) return;
        _info.Enabled = true;
        Publish("info-station", _info.Snapshot());
    }

    private async Task<string?> GetAsync(string url, string? token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(token)) request.Headers.TryAddWithoutValidation("X-Local-Token", token);
            using var response = await _http.SendAsync(request).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Info Station {url}: {ex.Message}");
            return null;
        }
    }

    private void PollCourse(string? configuredPath)
    {
        var path = FirstExisting(configuredPath, DefaultCoursePaths());
        if (path is null) return;
        try
        {
            _courses.ApplySnapshot(File.ReadAllText(path));
            _courses.Enabled = true;
            Publish("coursedesk", _courses.Snapshot());
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"CourseDesk snapshot: {ex.Message}");
        }
    }

    private static IEnumerable<string> DefaultCoursePaths()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(roaming, "CourseDesk", "qisland-snapshot.json");
        yield return Path.Combine(roaming, "coursedesk", "qisland-snapshot.json");
    }

    private static string? FirstExisting(string? configured, IEnumerable<string> fallbacks)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var path in fallbacks)
            if (File.Exists(path)) return path;
        return null;
    }

    private void PushDraft(PushDraft draft)
    {
        var push = new IslandPush
        {
            Id = string.IsNullOrWhiteSpace(draft.Id) ? Guid.NewGuid().ToString("N") : draft.Id,
            Title = draft.Title,
            Body = draft.Body,
            Subtitle = draft.Subtitle,
            Icon = draft.Icon,
            Type = draft.Type,
            Priority = draft.Priority,
            DurationSeconds = draft.DurationSeconds,
            ExpiresAt = DateTime.UtcNow.AddSeconds(Math.Max(4, draft.DurationSeconds)),
            RefreshTtl = true,
        };
        _vm.PushIsland(push, refreshTtl: true);
    }

    private void Publish(string source, IReadOnlyList<IslandActivity> activities)
    {
        var signature = string.Join("\n", activities.Select(a => a.Id + "|" + a.Kind + "|" + a.Title + "|" + a.Body));
        var known = _ids.TryGetValue(source, out var previous) ? previous : new HashSet<string>(StringComparer.Ordinal);
        var keep = new HashSet<string>(activities.Select(a => a.Id), StringComparer.Ordinal);
        var missing = activities.Any(a => _vm.ActivePushes.All(p => !string.Equals(p.Id, a.Id, StringComparison.Ordinal)));
        if (_signatures.TryGetValue(source, out var previousSignature) && previousSignature == signature && !missing)
        {
            foreach (var activity in activities)
                _vm.TouchPushExpiry(activity.Id, DateTime.UtcNow.AddSeconds(Math.Max(4, activity.DurationSeconds)));
            return;
        }
        _signatures[source] = signature;
        foreach (var id in known)
            if (!keep.Contains(id)) _vm.RemoveIslandPush(id);
        foreach (var activity in activities)
        {
            var draft = PushMapping.From(activity);
            PushDraft(draft);
        }
        _ids[source] = keep;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _http.Dispose();
    }
}
