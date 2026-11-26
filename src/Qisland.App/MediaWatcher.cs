using Qisland.Core;
﻿using System.IO;
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
    bool Video = false, int Sessions = 1, bool CanSeek = false, string? SongId = null, bool FromSmtc = true, string Aumid = "");

/// <summary>
/// 系统媒体会话（SMTC：网易云、QQ 音乐、酷狗、酷我、Apple Music、Spotify、foobar2000、浏览器……）：读歌名/封面/进度，并能切歌暂停。
/// 进度统一走 <see cref="PlaybackClock"/>：播放器报的时间轴只在差得多时才对齐，平时按本地时钟推，歌词不抖。
/// 网易云同时读它的日志拿 songId（歌词按 id 取，不会取到别的版本）；网易云的 SMTC 时间轴不在动时，退回日志里的进度。
/// </summary>
public sealed class MediaWatcher
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _pinnedId;                       // 用户手动切到的来源：候选键（MediaSources.Keys，同程序第 n 个会话是 id#n），或网易云日志伪来源
    private GlobalSystemMediaTransportControlsSession? _pinnedSession;   // 手动切到的那个会话本身（同 id 的会话靠它区分）
    // 每个会话挂一个播放状态监听；会话关掉（浏览器标签页、播放器退出）就摘掉，不让它们越攒越多
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>> _hooked = [];
    private TimeSpan _start;
    private readonly PlaybackClock _clock = new();
    private string _sessionsLogged = "";
    private string? _clockCompared;
    private string _artKey = "";
    private BitmapSource? _art;
    // 封面：很多播放器先报歌名、过一会儿才补封面（第二次 MediaPropertiesChanged，有的干脆不再发事件），
    // 切歌瞬间读到的还可能是上一首的封面。所以封面和歌名分开判断：没拿到 / 疑似上一首的，就隔一会儿再读，每首最多补读 3 次。
    private int _artTries;
    private string _artAlbum = "", _prevAlbum = "";
    private ulong _artHash, _prevHash;
    private int _artRetryArmed;
    private const int MaxArtTries = 3;
    public MediaState? Current { get; private set; }
    public event Action? Changed;

    public MediaWatcher(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        _ncmTimer = new Timer(_ => NcmTick(), null, 800, 700);
        // 开机自启时系统的媒体会话服务可能还没起来：连不上就隔一会儿再连，而不是这一次开机都没有音乐
        for (var attempt = 1; !await Connect(); attempt++)
        {
            if (attempt == 1 || attempt % 10 == 0) App.Log($"media: 第 {attempt} 次没连上系统媒体会话，稍后再试");
            await App.Nap(TimeSpan.FromSeconds(Math.Min(60, 5 * attempt)));
        }
    }

    private async Task<bool> Connect()
    {
        try
        {
            var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (mgr == null) return false;
            var old = _mgr;
            if (old != null)
            {
                old.CurrentSessionChanged -= OnMgrChanged;
                old.SessionsChanged -= OnMgrSessions;
            }
            UnhookAll();
            _mgr = mgr;
            mgr.CurrentSessionChanged += OnMgrChanged;
            mgr.SessionsChanged += OnMgrSessions;
            Attach();
            App.Log(old == null ? "media: 已连接系统媒体会话" : "media: 重新连接了系统媒体会话");
            return true;
        }
        catch (Exception ex) { App.Log("media: " + ex.Message); return false; }
    }

    private void OnMgrChanged(GlobalSystemMediaTransportControlsSessionManager m, CurrentSessionChangedEventArgs e) => Attach();
    private void OnMgrSessions(GlobalSystemMediaTransportControlsSessionManager m, SessionsChangedEventArgs e) => Attach();

    /// <summary>睡眠唤醒后：系统媒体服务可能重启过，旧的连接不再来事件，重连一次。</summary>
    public void Reconnect() => _ = Task.Run(async () => { await Task.Delay(3000); await Connect(); });

    private void UnhookAll()
    {
        lock (_hooked)
        {
            foreach (var (s, h) in _hooked) try { s.PlaybackInfoChanged -= h; } catch { }
            _hooked.Clear();
        }
    }

    // ---------- 网易云（不报 SMTC，靠读它自己的日志） ----------
    private readonly NcmElog _ncm = new();
    private Timer? _ncmTimer;
    private MediaState? _ncmCur;
    private int _ncmBusy;
    private string? _ncmLogged;
    private long _ncmCheckedAt;
    private bool _ncmEligible;
    private volatile bool _ncmSmtc;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string title, string artist, string pic, double dur)> _ncmMeta = new();
    private readonly HashSet<string> _ncmMetaLoading = [];
    private volatile Tuple<string, BitmapSource>? _ncmArt; // 封面和它的 songId 一起换，避免串歌
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
            // 查进程和系统会话比读文件贵：每 3 秒才查一次，中间沿用结果
            var now = Environment.TickCount64;
            // 网易云开了 SMTC 以后，歌名 / 封面 / 状态 / 控制都走 SMTC；日志照样读（只读、共享读），为了拿 songId 和备用进度
            if (now - _ncmCheckedAt > 3000) { _ncmCheckedAt = now; _ncmSmtc = SmtcHasCloudMusic(); _ncmEligible = NcmElog.ClientRunning(); }
            if (_ncmEligible)
            {
                _ncm.Poll();
                if (_ncm.Current() is { } c) st = NcmState(c);
            }
            var prev = _ncmCur;
            _ncmCur = st;
            if (_pinnedId == MediaSources.NcmLog && !NcmLogOnly) _pinnedId = null;   // 手动切到的网易云日志没了（或改走 SMTC）：取消手动选择
            var drift = st != null && prev != null && Current?.SongId == st.SongId && Current is { AppSource: "music", FromSmtc: false }
                && Math.Abs((LivePosition() - st.Position).TotalSeconds) > 1.5;
            CompareClocks(st);
            if (st?.SongId != null && st.SongId != _ncmLogged) { _ncmLogged = st.SongId; App.Log($"网易云(日志): {st.SongId} {st.Title} - {st.Artist} {(st.Playing ? "播放" : "暂停")} {st.Position:mm\\:ss}"); }
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
            // 详情拿到了、封面下载却失败过（超时、断网）：以前不会再下，这首歌一直没封面；现在每首最多补下 3 次
            if (meta.pic.Length > 0 && _ncmArt?.Item1 != id) NcmLoadArt(id, meta.pic);
        }
        else NcmLoadMeta(id);
        if (title.Length == 0 && NcmElog.WindowTitle() is { } w) { title = w.title; if (artist.Length == 0) artist = w.artist; }
        if (title.Length == 0) title = "网易云音乐";
        var pos = c.Position;
        if (dur > TimeSpan.Zero && pos > dur) pos = dur;
        return new MediaState(title, artist, "网易云音乐", "music", c.Playing, _ncmArt is { } art && art.Item1 == id ? art.Item2 : null,
            pos, dur, DateTime.Now, false, 1, false, id, FromSmtc: false, Aumid: "cloudmusic.exe");
    }

    // 网易云同时有 SMTC 和日志时，每首歌放到 15 秒左右记一次两边进度的差，方便判断哪边准（只记时间，不记别的）
    private void CompareClocks(MediaState? log)
    {
        if (log?.SongId is not { } id || !log.Playing || Current is not { AppSource: "music", FromSmtc: true } c || c.SongId != id) return;
        if (_clockCompared == id || log.Position < TimeSpan.FromSeconds(15)) return;
        _clockCompared = id;
        var smtc = _clock.Position(DateTime.Now, c.Duration);
        App.Log($"网易云 进度对比: SMTC {smtc:mm\\:ss\\.f}（{(_clock.Live ? "时间轴在走" : "时间轴不动")}）/ 日志 {log.Position:mm\\:ss\\.f}，差 {(smtc - log.Position).TotalMilliseconds:+0;-0} ms");
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
                _ncmCur = null; // 让下一拍按“有变化”刷新（下一拍的 NcmState 会去下封面）
            }
            catch (Exception ex) { App.Log("网易云详情: " + ex.Message); lock (_ncmMetaLoading) _ncmMetaLoading.Remove(id); }
        });
    }

    private readonly Dictionary<string, int> _ncmArtTries = [];
    private int _ncmArtBusy;
    private void NcmLoadArt(string id, string pic)
    {
        lock (_ncmArtTries)
        {
            var n = _ncmArtTries.GetValueOrDefault(id);
            if (n >= 3 || Interlocked.Exchange(ref _ncmArtBusy, 1) == 1) return;
            if (_ncmArtTries.Count > 200) _ncmArtTries.Clear();
            _ncmArtTries[id] = n + 1;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await NcmHttp.GetByteArrayAsync(pic.Replace("http://", "https://") + "?param=132y132");
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.DecodePixelWidth = 132;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                _ncmArt = Tuple.Create(id, (BitmapSource)bmp);
                _ncmCur = null;
            }
            catch (Exception ex) { App.Log("网易云封面: " + ex.GetType().Name + " " + ex.Message); await Task.Delay(5000); }
            finally { _ncmArtBusy = 0; }
        });
    }

    // 播放控制：只给网易云自己的主窗口发 WM_APPCOMMAND（媒体键消息），不模拟全局键盘输入
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // 网易云不接受发给它窗口的 WM_APPCOMMAND（实测主窗口、渲染窗口都没反应），也不注册系统媒体会话。
    // 经用户同意（2026-11-16），改为按一下系统媒体键：和用户自己按键盘上的媒体键效果一样。
    private static void NcmCommand(int cmd)
    {
        byte vk = cmd switch { 11 => 0xB0, 12 => 0xB1, _ => 0xB3 }; // 下一首 / 上一首 / 播放暂停
        const uint Extended = 0x1, KeyUp = 0x2;
        keybd_event(vk, 0, Extended, UIntPtr.Zero);
        keybd_event(vk, 0, Extended | KeyUp, UIntPtr.Zero);
        App.Log($"网易云控制：媒体键 0x{vk:X2}");
    }
    private bool CurrentIsNcm => Current is { AppSource: "music", SongId: not null, FromSmtc: false } && _ncmCur != null;
    /// <summary>网易云只在日志里（没开 SMTC）：它是系统会话之外的一个可切换来源。</summary>
    private bool NcmLogOnly => _ncmCur != null && !_ncmSmtc;
    private List<string> SessionIds() => Ids(All());
    private static List<string> Ids(List<GlobalSystemMediaTransportControlsSession> all) => all.Select(x => x.SourceAppUserModelId ?? "").ToList();
    /// <summary>切换按钮的计数和轮换用同一份候选（规则在 Core 的 MediaSources）。</summary>
    private int SourceCount() => MediaSources.Count(SessionIds(), NcmLogOnly);

    private void Attach()
    {
        HookAll();
        LogSessions();
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
            var now = _mgr.GetSessions().ToList();
            lock (_hooked)
            {
                foreach (var gone in _hooked.Keys.Where(k => !now.Contains(k)).ToList())
                {
                    try { gone.PlaybackInfoChanged -= _hooked[gone]; } catch { }
                    _hooked.Remove(gone);
                }
                foreach (var x in now)
                {
                    if (_hooked.ContainsKey(x)) continue;
                    Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> h = (_, _) => { if (x != _session) Attach(); };
                    x.PlaybackInfoChanged += h;
                    _hooked[x] = h;
                }
            }
        }
        catch { }
    }

    /// <summary>会话列表变化时记一行：有哪些软件在向系统报播放（只记软件名和状态，不记歌名）。</summary>
    private void LogSessions()
    {
        try
        {
            var list = All().Select(x =>
            {
                var id = x.SourceAppUserModelId ?? "";
                return $"{AppName(id).Item1}[{ShortAumid(id)}]";
            }).ToList();
            var line = string.Join(", ", list);
            if (line == _sessionsLogged) return;
            _sessionsLogged = line;
            App.Log($"media: {list.Count} 个会话 · {(list.Count == 0 ? "无" : line)}");
        }
        catch { }
    }

    private static string ShortAumid(string a)
    {
        var i = a.IndexOf('_'); var j = a.IndexOf('!');
        if (i > 0 && j > i) a = a[..i] + a[j..];      // 去掉包族名里的哈希
        return a.Length > 40 ? a[..40] : a;
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
            if (_pinnedId != null && _pinnedId != MediaSources.NcmLog)   // 切到网易云日志时系统会话照常挑，显示由 Choose 决定
            {
                // 先认会话本身（同 id 的会话顺序变了也不会认错），再按候选键找
                if (_pinnedSession != null && all.Contains(_pinnedSession)) return _pinnedSession;
                var i = MediaSources.Resolve(Ids(all), _pinnedId);
                if (i >= 0) return _pinnedSession = all[i];
                _pinnedId = null; _pinnedSession = null;
            }
            var playing = all.Where(x => x.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing).ToList();
            if (_session != null && playing.Contains(_session)) return _session; // 正在播的不被别人抢
            if (playing.Count > 0) return playing[^1];
            return _mgr.GetCurrentSession() ?? all.FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>同时有多个来源时（比如网易云 + 浏览器视频），在它们之间轮换；网易云只在日志里时也算一个来源。</summary>
    public void Cycle()
    {
        // 计数、轮换、选中都用 MediaSources 的候选键：同一个程序的两个会话（来源 id 相同）也能切过去
        var all = All();
        var ids = Ids(all);
        var candidates = MediaSources.Candidates(ids, NcmLogOnly);
        var at = _session == null ? -1 : all.IndexOf(_session);
        var current = CurrentIsNcm && NcmLogOnly ? MediaSources.NcmLog : at >= 0 ? MediaSources.KeyAt(ids, at) : null;
        if (MediaSources.Next(candidates, current) is not { } next) return;
        _pinnedId = next;
        _pinnedSession = MediaSources.Resolve(ids, next) is var j and >= 0 ? all[j] : null;
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
                    Clock(st);
                    var changed = !Same(Current, st);
                    Current = st;
                    if (changed) Changed?.Invoke();
                });
            } while (_again);
        }
        catch (Exception ex) { App.Log($"media read: {ex.GetType().Name} {ex.HResult:X8} {ex.Message}".TrimEnd()); } // 会话刚关掉时常见，消息为空，记下类型和错误码
        finally { _refreshing = 0; }
    }
    private volatile bool _again;

    // 系统会话在播就用它；否则网易云日志里有歌就用网易云（播放中优先，暂停时也比没有强）
    // 用户手动切到的会话（不是网易云、还在）优先，网易云日志不能把它抢回去（规则在 Core 的 MediaPick）
    private MediaState? Choose(MediaState? smtc)
    {
        var n = _ncmCur;
        var manual = _pinnedSession != null && ReferenceEquals(_session, _pinnedSession);
        var manualNcm = _pinnedId == MediaSources.NcmLog;
        var pick = MediaPick.Choose(smtc is null ? null : new MediaCandidate(smtc.Playing, smtc.AppSource == "music"),
            n is null ? null : new MediaCandidate(n.Playing, true), _ncmSmtc, manual, manualNcm);
        if (pick == MediaPickResult.None) return null;
        if (n == null) return smtc;
        if (_ncmSmtc)
        {
            // 网易云自己在报 SMTC：界面以 SMTC 为准，只从日志补 songId（歌词按 id 取）和时长；SMTC 一时读不到才用日志
            if (pick == MediaPickResult.Ncm) return n;
            if (smtc is { AppSource: "music" } && Lrc.Norm(smtc.Title) is var a && Lrc.Norm(n.Title) is var b && a.Length > 0 && (a == b || a.Contains(b) || b.Contains(a)))
                return smtc with { SongId = n.SongId, Duration = smtc.Duration > TimeSpan.Zero ? smtc.Duration : n.Duration };
            return smtc;
        }
        // 来源数 = 系统会话 + 网易云日志伪来源（和 Cycle 用同一份候选）
        var count = SourceCount();
        if (pick == MediaPickResult.Smtc) return smtc! with { Sessions = count };
        return n with { Sessions = count }; // 都没在放时网易云优先（浏览器标签页常年挂着暂停的会话）
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
        // Apple Music（Windows）把「歌手 — 专辑」放在 Artist 里
        if (src == "applemusic") artist = MediaText.SplitAppleArtist(artist, props.AlbumTitle).artist;
        // 个别播放器不报歌手，把「歌名 - 歌手」塞在标题里
        if (!video && artist.Length == 0 && src is not ("chrome" or "edge" or "firefox" or "browser" or "bilibili" or "potplayer"))
            (title, artist) = MediaText.SplitTitleArtist(title, artist);
        if (src is "chrome" or "edge" or "firefox" or "browser")
        {
            var (clean, site) = CleanWebTitle(title);
            title = clean;
            if (site != null) app = $"{site} · {app}";
            video |= site != null;
        }

        var key = aumid + "|" + props.Title + "|" + props.Artist + "|" + props.AlbumTitle;
        if (key != _artKey)
        {
            _artKey = key;
            if (_art != null) { _prevHash = _artHash; _prevAlbum = _artAlbum; }
            _art = null; _artHash = 0; _artTries = 0;
            _artAlbum = props.AlbumTitle ?? "";
        }
        if (_art == null && _artTries < MaxArtTries)
        {
            _artTries++;
            var art = props.Thumbnail != null ? await LoadArt(props.Thumbnail, src) : null;
            // 专辑不同、封面字节却和上一首一模一样：多半是播放器还没换封面，先不认，稍后再读；最后一次还一样就认（真有共用封面的）
            var stale = art != null && _artHash == 0 && art.Value.hash == _prevHash && _artAlbum != _prevAlbum && _artTries < MaxArtTries;
            if (art != null && !stale) { _art = art.Value.bmp; _artHash = art.Value.hash; }
            else if (!video || props.Thumbnail != null) ArmArtRetry(_artTries);
            if (art == null && _artTries == MaxArtTries) App.Log($"media art: {src} 这首歌没有拿到封面（{(props.Thumbnail == null ? "播放器没提供" : "读取或解码失败")}），显示软件图标");
        }
        var dur = tl.EndTime - tl.StartTime;
        _start = tl.StartTime;
        var canSeek = pb?.Controls?.IsPlaybackPositionEnabled == true && dur > TimeSpan.FromSeconds(1);
        if (dur <= TimeSpan.Zero && LocalLyricCaches.DurationFor(title, artist) is { } hint) dur = hint;   // 酷狗 SMTC 不给时长：用它本机歌词里的 [total]
        return new MediaState(title, artist, app, src, playing, _art,
            tl.Position - tl.StartTime, dur < TimeSpan.Zero ? TimeSpan.Zero : dur, tl.LastUpdatedTime.LocalDateTime,
            video, SourceCount(), canSeek, Aumid: aumid);
    }

    /// <summary>封面没拿到时隔一会儿再读一次（同一时刻只排一个）：第 1 次 0.6 秒后，之后 1.5 秒。</summary>
    private void ArmArtRetry(int tries)
    {
        if (tries >= MaxArtTries || Interlocked.Exchange(ref _artRetryArmed, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(tries <= 1 ? 600 : 1500);
            _artRetryArmed = 0;
            await RefreshAsync();
        });
    }

    private static async Task<(BitmapSource bmp, ulong hash)?> LoadArt(Windows.Storage.Streams.IRandomAccessStreamReference r, string src)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            if (ras.Size == 0) return null;   // 有的播放器先给一个空流，稍后才写进去
            using var net = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            if (ms.Length == 0) return null;
            var hash = Fnv(ms.GetBuffer().AsSpan(0, (int)ms.Length));
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 132; // 44 DIP × 300% 缩放
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, hash);
        }
        catch (Exception ex)
        {
            // 只记来源和错误类型，不记歌名
            App.Log($"media art: {src} 封面读取失败 {ex.GetType().Name} {ex.HResult:X8}");
            return null;
        }
    }

    private static ulong Fnv(ReadOnlySpan<byte> b)
    {
        var h = 14695981039346656037UL;
        foreach (var x in b) { h ^= x; h *= 1099511628211UL; }
        return h;
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
        if (a.Contains("foobar")) return ("foobar2000", "foobar");
        if (a.Contains("toside") || a.Contains("lx-music") || a.Contains("lxmusic")) return ("洛雪音乐", "exe:lx-music-desktop");
        if (a.Contains("sodamusic") || a.Contains("汽水")) return ("汽水音乐", "exe:SodaMusic");
        if (a.Contains("musicbee")) return ("MusicBee", "exe:MusicBee");
        if (a.Contains("aimp")) return ("AIMP", "exe:AIMP");
        if (a.Contains("tidal")) return ("TIDAL", "exe:TIDAL");
        if (a.Contains("zunemusic")) return ("媒体播放器", "player");
        if (a.Contains("potplayer")) return ("PotPlayer", "potplayer");
        if (a.Contains("vlc")) return ("VLC", "potplayer");
        if (a.Contains("bilibili") || a.Contains("哔哩")) return ("哔哩哔哩", "bilibili");
        if (a.Contains("msedge")) return ("Edge", "edge");
        if (a.Contains("chrome")) return ("Chrome", "chrome");
        if (a.Contains("firefox") || a.Contains("308046b0af4a39cb")) return ("Firefox", "firefox");
        if (a.Contains("opera") || a.Contains("brave") || a.Contains("360") || a.Contains("qqbrowser")) return ("浏览器", "browser");
        var n = Path.GetFileNameWithoutExtension(aumid);
        // 其它直接用 exe 名报会话的软件：按进程名找图标
        if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.Length > 0) return (n, "exe:" + n);
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

    /// <summary>每次读到新状态时喂给进度时钟（UI 线程）。</summary>
    private void Clock(MediaState? st)
    {
        if (st == null) { _clock.Reset(); return; }
        var key = $"{st.Aumid}\u0001{st.SongId ?? st.Title}\u0001{st.Artist}";
        _clock.Sample(key, st.Position, st.PositionAt, st.Playing, DateTime.Now);
    }

    /// <summary>
    /// 当前进度。一般用进度时钟；网易云的 SMTC 时间轴从来没动过（只在开始时报一次 0）而日志里有同一首歌时，用日志推算的进度。
    /// </summary>
    public TimeSpan LivePosition()
    {
        if (Current is not { } c) return TimeSpan.Zero;
        if (c is { AppSource: "music", FromSmtc: true, SongId: { } id } && !_clock.Live && _ncmCur is { } n && n.SongId == id)
        {
            var p = n.Playing ? n.Position + (DateTime.Now - n.PositionAt) : n.Position;
            return n.Duration > TimeSpan.Zero && p > n.Duration ? n.Duration : p;
        }
        return _clock.Position(DateTime.Now, c.Duration > TimeSpan.Zero ? c.Duration : null);
    }

    /// <summary>拖进度条：fraction 0~1。</summary>
    public void Seek(double fraction)
    {
        if (Current is not { CanSeek: true } c) return;
        var target = _start + TimeSpan.FromSeconds(c.Duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        Current = c with { Position = target - _start, PositionAt = DateTime.Now };
        _clock.Seek(target - _start, DateTime.Now);
        Run(s => s.TryChangePlaybackPositionAsync(target.Ticks));
    }

    /// <summary>截图演示用：直接塞一个播放状态，不连系统会话。</summary>
    public void ShowDemo(MediaState s) { Current = s; Clock(s); Changed?.Invoke(); }

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
