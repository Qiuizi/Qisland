using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Qisland.App;

public sealed record MediaState(
    string Title, string Artist, string App, string AppSource, bool Playing,
    BitmapSource? Art, TimeSpan Position, TimeSpan Duration, DateTime PositionAt,
    bool Video = false, int Sessions = 1, bool CanSeek = false);

/// <summary>系统媒体会话（网易云、QQ 音乐、Spotify、浏览器都会上报）：读歌名/封面/进度，并能切歌暂停。</summary>
public sealed class MediaWatcher
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _pinnedId;                       // 用户手动切到的来源
    private readonly HashSet<GlobalSystemMediaTransportControlsSession> _hooked = [];
    private TimeSpan _start;
    private string _artKey = "";
    private BitmapSource? _art;
    public MediaState? Current { get; private set; }
    public event Action? Changed;

    public MediaWatcher(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, _) => Attach();
            _mgr.SessionsChanged += (_, _) => Attach();
            Attach();
            App.Log("media: 已连接系统媒体会话");
        }
        catch (Exception ex) { App.Log("media: " + ex.Message); }
    }

    private void Attach()
    {
        HookAll();
        var s = Pick();
        if (!ReferenceEquals(s, _session))
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = s;
            if (s != null)
            {
                s.MediaPropertiesChanged += OnProps;
                s.PlaybackInfoChanged += OnPlayback;
                s.TimelinePropertiesChanged += OnTimeline;
            }
        }
        _ = RefreshAsync();
    }

    // 每个会话的播放状态都要听：另一个软件开始播放时自动切过去
    private void HookAll()
    {
        if (_mgr == null) return;
        try
        {
            lock (_hooked)
                foreach (var x in _mgr.GetSessions())
                    if (_hooked.Add(x)) x.PlaybackInfoChanged += (_, _) => { if (x != _session) Attach(); };
        }
        catch { }
    }

    private List<GlobalSystemMediaTransportControlsSession> All()
    {
        try { return _mgr?.GetSessions().ToList() ?? []; } catch { return []; }
    }

    // 手动选的来源优先；否则正在播放的（最近开始的优先），都没播放时用系统给的当前会话
    private GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_mgr == null) return null;
        try
        {
            var all = All();
            if (_pinnedId != null)
            {
                var pinned = all.FirstOrDefault(x => x.SourceAppUserModelId == _pinnedId);
                if (pinned != null) return pinned;
                _pinnedId = null;
            }
            var playing = all.Where(x => x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToList();
            if (_session != null && playing.Contains(_session)) return _session; // 正在播的不被别人抢
            if (playing.Count > 0) return playing[^1];
            return _mgr.GetCurrentSession() ?? all.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>同时有多个来源时（比如网易云 + 浏览器视频），在它们之间轮换。</summary>
    public void Cycle()
    {
        var all = All();
        if (all.Count < 2) return;
        var i = _session == null ? -1 : all.IndexOf(_session);
        _pinnedId = all[(i + 1 + all.Count) % all.Count].SourceAppUserModelId;
        Attach();
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshAsync();
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Attach();
    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => _ = RefreshAsync();

    private int _refreshing;
    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) { _again = true; return; }
        try
        {
            do
            {
                _again = false;
                var st = await Read(_session);
                await _ui.InvokeAsync(() =>
                {
                    var changed = !Same(Current, st);
                    Current = st;
                    if (changed) Changed?.Invoke();
                });
            } while (_again);
        }
        catch (Exception ex) { App.Log("media read: " + ex.Message); }
        finally { _refreshing = 0; }
    }
    private volatile bool _again;

    // 只有界面上要换的东西变了才算变化。进度不算：浏览器每隔几秒就推一次时间轴，
    // 以前每推一次就整块重建展开视图，正是播放视频时一顿一顿、闪一下的原因。进度条每秒自己按 LivePosition 走。
    private static bool Same(MediaState? a, MediaState? b) =>
        a is null ? b is null : b is not null && a.Title == b.Title && a.Artist == b.Artist && a.Playing == b.Playing
            && ReferenceEquals(a.Art, b.Art) && a.Duration == b.Duration && a.App == b.App
            && a.Video == b.Video && a.Sessions == b.Sessions && a.CanSeek == b.CanSeek;

    private async Task<MediaState?> Read(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s == null) return null;
        var props = await s.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title)) return null;
        var pb = s.GetPlaybackInfo();
        var tl = s.GetTimelineProperties();
        var playing = pb?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var aumid = s.SourceAppUserModelId ?? "";
        var (app, src) = AppName(aumid);
        var video = props.PlaybackType == Windows.Media.MediaPlaybackType.Video;
        var title = props.Title.Trim();
        var artist = (props.Artist ?? "").Trim();
        if (src is "chrome" or "edge" or "firefox" or "browser")
        {
            var (clean, site) = CleanWebTitle(title);
            title = clean;
            if (site != null) app = $"{site} · {app}";
            video |= site != null;
        }

        var key = props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
        if (key != _artKey)
        {
            _artKey = key;
            _art = props.Thumbnail != null ? await LoadArt(props.Thumbnail) : null;
        }
        var dur = tl.EndTime - tl.StartTime;
        _start = tl.StartTime;
        var canSeek = pb?.Controls?.IsPlaybackPositionEnabled == true && dur > TimeSpan.FromSeconds(1);
        return new MediaState(title, artist, app, src, playing, _art,
            tl.Position - tl.StartTime, dur < TimeSpan.Zero ? TimeSpan.Zero : dur, tl.LastUpdatedTime.LocalDateTime,
            video, Math.Max(1, All().Count), canSeek);
    }

    private static async Task<BitmapSource?> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference r)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            using var net = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 132; // 44 DIP × 300% 缩放
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // AUMID 例子：cloudmusic.exe、QQMusic.exe、Spotify.exe / SpotifyAB.SpotifyMusic_…!Spotify、Chrome、MSEdge、308046B0AF4A39CB（Firefox）
    private static (string, string) AppName(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("cloudmusic")) return ("网易云音乐", "music");
        if (a.Contains("qqmusic")) return ("QQ 音乐", "qqmusic");
        if (a.Contains("spotify")) return ("Spotify", "spotify");
        if (a.Contains("kugou")) return ("酷狗音乐", "kugou");
        if (a.Contains("kwmusic") || a.Contains("kuwo")) return ("酷我音乐", "kuwo");
        if (a.Contains("applemusic") || a.Contains("appleinc")) return ("Apple Music", "applemusic");
        if (a.Contains("zunemusic")) return ("媒体播放器", "player");
        if (a.Contains("potplayer")) return ("PotPlayer", "potplayer");
        if (a.Contains("vlc")) return ("VLC", "potplayer");
        if (a.Contains("foobar")) return ("foobar2000", "player");
        if (a.Contains("bilibili") || a.Contains("哔哩")) return ("哔哩哔哩", "bilibili");
        if (a.Contains("msedge")) return ("Edge", "edge");
        if (a.Contains("chrome")) return ("Chrome", "chrome");
        if (a.Contains("firefox") || a.Contains("308046b0af4a39cb")) return ("Firefox", "firefox");
        if (a.Contains("opera") || a.Contains("brave") || a.Contains("360") || a.Contains("qqbrowser")) return ("浏览器", "browser");
        var n = Path.GetFileNameWithoutExtension(aumid);
        return (string.IsNullOrEmpty(n) ? "正在播放" : n, "player");
    }

    private static readonly (string suffix, string site)[] Sites =
    [
        ("_哔哩哔哩_bilibili", "哔哩哔哩"), ("_bilibili", "哔哩哔哩"), (" - 哔哩哔哩", "哔哩哔哩"),
        (" - YouTube", "YouTube"), (" - 优酷", "优酷"), ("_腾讯视频", "腾讯视频"), (" - 腾讯视频", "腾讯视频"),
        ("-爱奇艺", "爱奇艺"), ("_爱奇艺", "爱奇艺"), (" - Netflix", "Netflix"), (" - 抖音", "抖音"), (" - 西瓜视频", "西瓜视频"),
    ];

    private static (string, string?) CleanWebTitle(string t)
    {
        foreach (var (suffix, site) in Sites)
        {
            var i = t.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (i > 0) return (t[..i].Trim(), site);
        }
        return (t, null);
    }

    public TimeSpan LivePosition()
    {
        if (Current is not { } c) return TimeSpan.Zero;
        var p = c.Playing ? c.Position + (DateTime.Now - c.PositionAt) : c.Position;
        if (c.Duration > TimeSpan.Zero && p > c.Duration) p = c.Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    /// <summary>拖进度条：fraction 0~1。</summary>
    public void Seek(double fraction)
    {
        if (Current is not { CanSeek: true } c) return;
        var target = _start + TimeSpan.FromSeconds(c.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        Current = c with { Position = target - _start, PositionAt = DateTime.Now };
        Run(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }

    /// <summary>截图演示用：直接塞一个播放状态，不连系统会话。</summary>
    public void ShowDemo(MediaState s) { Current = s; Changed?.Invoke(); }

    public void Toggle() => Run(s => s.TryTogglePlayPauseAsync());
    public void Next() => Run(s => s.TrySkipNextAsync());
    public void Previous() => Run(s => s.TrySkipPreviousAsync());

    private void Run(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op)
    {
        var s = _session;
        if (s == null) return;
        _ = Task.Run(async () =>
        {
            try { await op(s); } catch (Exception ex) { App.Log("media op: " + ex.Message); }
        });
    }
}
