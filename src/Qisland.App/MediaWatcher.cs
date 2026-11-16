using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Qisland.App;

public sealed record MediaState(
    string Title, string Artist, string App, string AppSource, bool Playing,
    BitmapSource? Art, TimeSpan Position, TimeSpan Duration, DateTime PositionAt,
    bool Video = false, int Sessions = 1, bool CanSeek = false, string? SongId = null);

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
        _ncmTimer = new Timer(_ => NcmTick(), null, 800, 700);
    }

    // ---------- 网易云（不报 SMTC，靠读它自己的日志） ----------
    private readonly NcmElog _ncm = new();
    private Timer? _ncmTimer;
    private MediaState? _ncmCur;
    private int _ncmBusy;
    private readonly Dictionary<string, (string title, string artist, string pic, double dur)> _ncmMeta = [];
    private readonly HashSet<string> _ncmMetaLoading = [];
    private string? _ncmArtId;
    private BitmapSource? _ncmArt;
    private static readonly HttpClient NcmHttp = MakeHttp();
    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 Qisland");
        h.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return h;
    }

    private bool SmtcHasCloudMusic() => All().Any(x => (x.SourceAppUserModelId ?? "").Contains("cloudmusic", StringComparison.OrdinalIgnoreCase));

    private void NcmTick()
    {
        if (Interlocked.Exchange(ref _ncmBusy, 1) == 1) return;
        try
        {
            MediaState? st = null;
            if (!SmtcHasCloudMusic() && NcmElog.ClientRunning())
            {
                _ncm.Poll();
                if (_ncm.Current() is { } c) st = NcmState(c);
            }
            var prev = _ncmCur;
            _ncmCur = st;
            var drift = st != null && prev != null && Current?.SongId == st.SongId && Current?.AppSource == "music"
                && Math.Abs((LivePosition() - st.Position).TotalSeconds) > 1.5;
            if (!Same(prev, st) || prev?.SongId != st?.SongId || drift) _ = RefreshAsync();
        }
        catch (Exception ex) { App.Log("网易云: " + ex.Message); }
        finally { _ncmBusy = 0; }
    }

    private MediaState NcmState(NcmElog.State c)
    {
        var id = c.Track.SongId;
        string title = c.Track.Title, artist = c.Track.Artist;
        var dur = c.Duration;
        if (_ncmMeta.TryGetValue(id, out var meta))
        {
            if (title.Length == 0) title = meta.title;
            if (artist.Length == 0) artist = meta.artist;
            if (dur <= TimeSpan.Zero && meta.dur > 0) dur = TimeSpan.FromMilliseconds(meta.dur);
        }
        else NcmLoadMeta(id);
        if (title.Length == 0 && NcmElog.WindowTitle() is { } w) { title = w.title; if (artist.Length == 0) artist = w.artist; }
        if (title.Length == 0) title = "网易云音乐";
        var pos = c.Position;
        if (dur > TimeSpan.Zero && pos > dur) pos = dur;
        return new MediaState(title, artist, "网易云音乐", "music", c.Playing, _ncmArtId == id ? _ncmArt : null,
            pos, dur, DateTime.Now, false, 1, false, id);
    }

    // songId → 歌名、歌手、封面（公开接口，不带任何账号信息）
    private void NcmLoadMeta(string id)
    {
        lock (_ncmMetaLoading) if (!_ncmMetaLoading.Add(id)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var doc = JsonDocument.Parse(await NcmHttp.GetStringAsync($"https://music.163.com/api/song/detail/?id={id}&ids=%5B{id}%5D"));
                if (!doc.RootElement.TryGetProperty("songs", out var songs) || songs.GetArrayLength() == 0) return;
                var s = songs[0];
                var title = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var artist = s.TryGetProperty("artists", out var ars) ? string.Join(" / ", ars.EnumerateArray().Select(a => a.TryGetProperty("name", out var an) ? an.GetString() : null).Where(x => !string.IsNullOrEmpty(x))) : "";
                var pic = s.TryGetProperty("album", out var al) && al.TryGetProperty("picUrl", out var pu) ? pu.GetString() ?? "" : "";
                var dur = s.TryGetProperty("duration", out var d) && d.TryGetDouble(out var dd) ? dd : 0;
                _ncmMeta[id] = (title, artist, pic, dur);
                if (pic.Length > 0)
                {
                    var bytes = await NcmHttp.GetByteArrayAsync(pic.Replace("http://", "https://") + "?param=132y132");
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 132;
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    _ncmArt = bmp; _ncmArtId = id;
                }
                _ncmCur = null; // 让下一拍按“有变化”刷新
            }
            catch (Exception ex) { App.Log("网易云详情: " + ex.Message); lock (_ncmMetaLoading) _ncmMetaLoading.Remove(id); }
        });
    }

    // 播放控制：只给网易云自己的主窗口发 WM_APPCOMMAND（媒体键消息），不模拟全局键盘输入
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private static void NcmCommand(int cmd)
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("cloudmusic"))
            using (p)
            {
                var h = p.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                PostMessage(h, 0x0319, h, (IntPtr)(cmd << 16));
                return;
            }
    }
    private bool CurrentIsNcm => Current is { AppSource: "music", SongId: not null } && _ncmCur != null;

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
                var st = Choose(await Read(_session));
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

    // 系统会话在播就用它；否则网易云日志里有歌就用网易云（播放中优先，暂停时也比没有强）
    private MediaState? Choose(MediaState? smtc)
    {
        var n = _ncmCur;
        if (n == null) return smtc;
        if (smtc is { Playing: true } && !n.Playing) return smtc with { Sessions = smtc.Sessions + 1 };
        if (n.Playing || smtc == null) return smtc == null ? n : n with { Sessions = smtc.Sessions + 1 };
        return smtc with { Sessions = smtc.Sessions + 1 };
    }

    // 只有界面上要换的东西变了才算变化。进度不算：浏览器每隔几秒就推一次时间轴，
    // 以前每推一次就整块重建展开视图，正是播放视频时一顿一顿、闪一下的原因。进度条每秒自己按 LivePosition 走。
    private static bool Same(MediaState? a, MediaState? b) =>
        a is null ? b is null : b is not null && a.Title == b.Title && a.Artist == b.Artist && a.Playing == b.Playing
            && ReferenceEquals(a.Art, b.Art) && a.Duration == b.Duration && a.App == b.App
            && a.Video == b.Video && a.Sessions == b.Sessions && a.CanSeek == b.CanSeek && a.SongId == b.SongId;

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

    public void Toggle() { if (CurrentIsNcm) NcmCommand(14); else Run(s => s.TryTogglePlayPauseAsync()); }
    public void Next() { if (CurrentIsNcm) NcmCommand(11); else Run(s => s.TrySkipNextAsync()); }
    public void Previous() { if (CurrentIsNcm) NcmCommand(12); else Run(s => s.TrySkipPreviousAsync()); }

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
