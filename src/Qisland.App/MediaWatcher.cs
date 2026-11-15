using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Qisland.App;

public sealed record MediaState(
    string Title, string Artist, string App, string AppSource, bool Playing,
    BitmapSource? Art, TimeSpan Position, TimeSpan Duration, DateTime PositionAt);

/// <summary>系统媒体会话（网易云、QQ 音乐、Spotify、浏览器都会上报）：读歌名/封面/进度，并能切歌暂停。</summary>
public sealed class MediaWatcher
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
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

    // 优先正在播放的会话；都没播放时用系统给的当前会话
    private GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_mgr == null) return null;
        try
        {
            foreach (var s in _mgr.GetSessions())
                if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) return s;
            return _mgr.GetCurrentSession();
        }
        catch { return null; }
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

    private static bool Same(MediaState? a, MediaState? b) =>
        a is null ? b is null : b is not null && a.Title == b.Title && a.Artist == b.Artist && a.Playing == b.Playing
            && ReferenceEquals(a.Art, b.Art) && Math.Abs((a.Position - b.Position).TotalSeconds) < 1.5 && a.Duration == b.Duration;

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

        var key = props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
        if (key != _artKey)
        {
            _artKey = key;
            _art = props.Thumbnail != null ? await LoadArt(props.Thumbnail) : null;
        }
        var dur = tl.EndTime - tl.StartTime;
        return new MediaState(props.Title.Trim(), (props.Artist ?? "").Trim(), app, src, playing, _art,
            tl.Position, dur < TimeSpan.Zero ? TimeSpan.Zero : dur, tl.LastUpdatedTime.LocalDateTime);
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

    private static (string, string) AppName(string aumid)
    {
        var a = aumid.ToLowerInvariant();
        if (a.Contains("cloudmusic")) return ("网易云音乐", "music");
        if (a.Contains("qqmusic")) return ("QQ 音乐", "qqmusic");
        if (a.Contains("spotify")) return ("Spotify", "spotify");
        if (a.Contains("kugou")) return ("酷狗音乐", "kugou");
        if (a.Contains("msedge") || a.Contains("chrome") || a.Contains("firefox")) return ("浏览器", "browser");
        if (a.Contains("zunemusic") || a.Contains("media")) return ("媒体播放器", "player");
        var n = Path.GetFileNameWithoutExtension(aumid);
        return (string.IsNullOrEmpty(n) ? "正在播放" : n, "player");
    }

    public TimeSpan LivePosition()
    {
        if (Current is not { } c) return TimeSpan.Zero;
        var p = c.Playing ? c.Position + (DateTime.Now - c.PositionAt) : c.Position;
        if (c.Duration > TimeSpan.Zero && p > c.Duration) p = c.Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

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
