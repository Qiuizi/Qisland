using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Qisland.App;

/// <summary>
/// 网易云 3.x 不向系统注册媒体会话（SMTC），系统层看不到它在放什么。
/// 但它会把播放事件写进 %LOCALAPPDATA%\NetEase\CloudMusic\cloudmusic.elog：
/// 每个字节做了一次固定替换（不是加密），解开后是带“开机毫秒”时间戳的文本行。
/// 这里只读这个文件（共享读，不改它、不注入、不碰账号），回放事件算出：
///   songId / 歌名 / 歌手 / 时长 / 播放或暂停 / 当前位置（毫秒级）。
/// 思路参考 MIT 项目 jx70/dsh-cloudmusic-player 的 lib/elog.mjs，这里是自己的 C# 实现。
/// 日志里的 musicurl（带签名的播放地址）只是跟着行一起被解码，不提取、不保存。
/// </summary>
public sealed class NcmElog
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetEase", "CloudMusic", "cloudmusic.elog");

    private static readonly byte[] Decode = BuildTable();
    private static readonly byte EncodedNewline = (byte)Array.IndexOf(Decode, (byte)0x0A);

    private static byte[] BuildTable()
    {
        var t = new byte[256];
        for (var b = 0; b < 256; b++)
        {
            int high = b >> 4, low = b & 0x0F;
            var hex = (high ^ ((low + 8) % 16)) % 16;
            t[b] = (byte)((hex * 16 + (b >> 6) * 4 + (~high & 3)) & 0xFF);
        }
        return t;
    }

    private static string DecodeBytes(ReadOnlySpan<byte> src)
    {
        var buf = new byte[src.Length];
        for (var i = 0; i < src.Length; i++) buf[i] = Decode[src[i]];
        return Encoding.UTF8.GetString(buf);
    }

    public sealed record Track(string SongId, string Title, string Artist, string Album, double DurationMs);
    public sealed record State(Track Track, bool Playing, TimeSpan Position, TimeSpan Duration);

    private readonly string _path;
    private long _offset = -1;
    private byte[] _carry = [];
    private Track? _track, _pendingMeta;
    private double _anchorAt, _anchorPos;   // 锚点：Unix 毫秒 / 位置毫秒
    private bool _paused = true, _ended;
    private double _lastTrackAt;
    private int _appPid;

    public NcmElog(string? path = null) => _path = path ?? DefaultPath;

    // [10424:41416:1116/112320:85108296:INFO:app.cpp(1231)] [2026-11-16 11:23:20] 【playing】,...
    private static readonly Regex Header = new(@"^\[(\d+):(\d+):(\d{4})/(\d{6}):(\d+):([A-Z]+):([\w.-]+)\((\d+)\)\](?:\s+\[(\d{4})-(\d{2})-(\d{2}) (\d{2}):(\d{2}):(\d{2})\])?", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"^\s*(?:【[^】]*】)?\s*", RegexOptions.Compiled);

    private const string MTrackPlay = "\"playOneTrackInPlayingList\"", MTrackPriv = "\"checkPlayPrivilege\"";
    private const string MMetaPlay = "\"nativePlay\"", MMetaCur = "\"refreshCurPlaying\"";
    private const string MStarted = "native播放资源load完成，开始播放", MState = "\"native播放state\"";
    private const string MSeek = "\"setPlayingPosition\"", MEnd = "\"onPlayEnd handle reason\"", MExitApp = "exitApp";
    private static readonly Regex ProcExit = new(@"Process\s+(\d+)\s+exited", RegexOptions.Compiled);

    private static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>读新增的部分；第一次调用时从尾部往前找最近一首歌。任何异常都不抛。</summary>
    public void Poll()
    {
        try
        {
            var fi = new FileInfo(_path);
            if (!fi.Exists) { Reset(); _offset = -1; return; }
            if (_offset < 0 || fi.Length < _offset) { Preload(fi.Length); return; }
            if (fi.Length == _offset) return;
            var chunk = Read(_offset, (int)Math.Min(fi.Length - _offset, 8 << 20));
            _offset += chunk.Length;
            var merged = _carry.Length == 0 ? chunk : [.. _carry, .. chunk];
            var last = Array.LastIndexOf(merged, EncodedNewline);
            if (last < 0) { _carry = merged.Length > (1 << 20) ? [] : merged; return; }
            _carry = merged[(last + 1)..];
            Replay(DecodeBytes(merged.AsSpan(0, last + 1)));
        }
        catch (Exception ex) { App.Log("网易云日志: " + ex.Message); }
    }

    private byte[] Read(long start, int len)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[len];
        var n = 0;
        while (n < len) { var r = fs.Read(buf, n, len - n); if (r <= 0) break; n += r; }
        return n == len ? buf : buf[..n];
    }

    private void Preload(long size)
    {
        Reset();
        _carry = [];
        const int chunk = 1 << 20;
        var limit = Math.Min(size, 16L << 20);
        var text = "";
        long scanned = 0;
        var fromZero = false;
        var found = false;
        while (scanned < limit)
        {
            var len = (int)Math.Min(chunk, limit - scanned);
            var start = size - scanned - len;
            if (start <= 0) fromZero = true;
            text = DecodeBytes(Read(start, len)) + text;
            scanned += len;
            if (text.Contains(MTrackPlay) || text.Contains(MTrackPriv) || text.Contains(MStarted)) { found = true; break; }
        }
        if (!fromZero) { var nl = text.IndexOf('\n'); text = nl >= 0 ? text[(nl + 1)..] : ""; }
        if (found)
        {
            // 从最近一次“起播”所在行开始回放（再往前的都是历史曲目）。起播前几秒的元数据行也带上。
            var cut = new[] { text.LastIndexOf(MTrackPlay), text.LastIndexOf(MTrackPriv), text.LastIndexOf(MStarted) }.Max();
            var ls = cut > 0 ? text.LastIndexOf('\n', cut) : -1;
            if (ls >= 0)
            {
                var back = ls;
                for (var i = 0; i < 40 && back > 0; i++) { var p = text.LastIndexOf('\n', back - 1); if (p < 0) break; back = p; }
                text = text[(back + 1)..];
            }
        }
        _offset = size;
        Replay(text);
    }

    private void Replay(string text)
    {
        var nowMs = NowMs;
        var upNow = Environment.TickCount64;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 20 || line[0] != '[') continue;
            var m = Header.Match(line);
            if (!m.Success) continue;
            if (!long.TryParse(m.Groups[5].Value, out var up)) continue;
            if (int.TryParse(m.Groups[1].Value, out var pid)) _appPid = pid;
            // 开机毫秒换算成墙钟；与行里的秒级墙钟差超过 5 秒（中间睡眠过）就用墙钟
            var t = nowMs - (upNow - up);
            if (m.Groups[9].Success)
            {
                try
                {
                    var wall = new DateTimeOffset(new DateTime(int.Parse(m.Groups[9].Value), int.Parse(m.Groups[10].Value), int.Parse(m.Groups[11].Value),
                        int.Parse(m.Groups[12].Value), int.Parse(m.Groups[13].Value), int.Parse(m.Groups[14].Value), DateTimeKind.Local)).ToUnixTimeMilliseconds();
                    if (Math.Abs(wall - t) > 5000) t = wall;
                }
                catch { }
            }
            var rest = Tag.Replace(line[m.Length..], "", 1);
            Apply(rest, t);
        }
    }

    private void Apply(string rest, double t)
    {
        if (rest.Contains(MTrackPlay) || rest.Contains(MTrackPriv))
        {
            if (Extract(rest) is not { } tr) return;
            var dup = _track?.SongId == tr.SongId && _lastTrackAt > 0 && Math.Abs(t - _lastTrackAt) < 1500;
            _track = tr;
            if (!dup) { _anchorAt = t; _anchorPos = 0; _paused = false; _ended = false; }
            _lastTrackAt = t;
        }
        else if (rest.Contains(MStarted))
        {
            var id = Regex.Match(rest, "\"songId\"\\s*:\\s*\"(\\d+)\"").Groups[1].Value;
            var dur = Regex.Match(rest, "\"songDuration\"\\s*:\\s*\"?(\\d+)");
            var pm = _pendingMeta;
            if (id.Length > 0 && _track?.SongId != id)
                _track = pm?.SongId == id ? pm : new Track(id, "", "", "", 0);
            if (_track != null && pm?.SongId == _track.SongId) _track = Merge(_track, pm);
            if (_track is { DurationMs: <= 0 } && dur.Success && double.TryParse(dur.Groups[1].Value, out var d)) _track = _track with { DurationMs = d };
            _pendingMeta = null;
            _anchorAt = t; _anchorPos = 0; _paused = false; _ended = false;
        }
        else if (rest.Contains(MMetaPlay) || rest.Contains(MMetaCur))
        {
            if (Extract(rest) is not { } meta) return;
            if (_track?.SongId == meta.SongId) _track = Merge(_track, meta);
            else _pendingMeta = meta;   // 新歌的详情可能比真正出声早几秒，先存着
        }
        else if (rest.Contains(MState))
        {
            var after = rest[(rest.IndexOf(MState) + MState.Length)..];
            var m = Regex.Match(after, @"^\s*,\s*(\d+)");
            if (!m.Success) return;
            _anchorPos = PositionAt(t); _anchorAt = t;
            _paused = m.Groups[1].Value == "2";
            if (!_paused) _ended = false;
            var idm = Regex.Match(after, "\"(\\d+)_[^\"]*\"");
            if (idm.Success && _track == null) _track = new Track(idm.Groups[1].Value, "", "", "", 0);
        }
        else if (rest.Contains(MSeek))
        {
            var m = Regex.Match(rest[(rest.IndexOf(MSeek) + MSeek.Length)..], @"^\s*,\s*(-?\d+(?:\.\d+)?)");
            if (!m.Success || !double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) || sec < 0) return;
            _anchorAt = t; _anchorPos = sec * 1000; _ended = false;
        }
        else if (rest.Contains(MEnd))
        {
            _anchorAt = t; _anchorPos = _track?.DurationMs ?? 0; _paused = true; _ended = true;
        }
        else if (rest.Contains(MExitApp)) Reset();
        else if (ProcExit.Match(rest) is { Success: true } pm && _appPid != 0 && pm.Groups[1].Value == _appPid.ToString()) Reset();
    }

    private static Track Merge(Track a, Track b) => a with
    {
        Title = a.Title.Length > 0 ? a.Title : b.Title,
        Artist = a.Artist.Length > 0 ? a.Artist : b.Artist,
        Album = a.Album.Length > 0 ? a.Album : b.Album,
        DurationMs = a.DurationMs > 0 ? a.DurationMs : b.DurationMs,
    };

    // 曲目 JSON 经常被客户端截断，不能整段 JSON 解析，用正则取字段
    private static Track? Extract(string rest)
    {
        var i = rest.IndexOf('{');
        if (i < 0) return null;
        var head = rest.Substring(i, Math.Min(6000, rest.Length - i));
        var id = Str(head, "id");
        var name = Str(head, "name");
        if (id.Length == 0 || name.Length == 0) return null;
        var am = Regex.Match(head, "\"artists\"\\s*:\\s*\\[\\s*\\{[\\s\\S]{0,400}?\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        var bm = Regex.Match(head, "\"album\"\\s*:\\s*\\{[\\s\\S]{0,300}?\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        var dm = Regex.Match(head, "\"duration\"\\s*:\\s*(\\d+(?:\\.\\d+)?)");
        return new Track(id, Unescape(name), am.Success ? Unescape(am.Groups[1].Value) : "", bm.Success ? Unescape(bm.Groups[1].Value) : "",
            dm.Success && double.TryParse(dm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0);
    }

    private static string Str(string s, string key)
    {
        var m = Regex.Match(s, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        try { return System.Text.Json.JsonSerializer.Deserialize<string>("\"" + s + "\"") ?? s; } catch { return s; }
    }

    private double PositionAt(double now)
    {
        var pos = _anchorPos + (_paused ? 0 : Math.Max(0, now - _anchorAt));
        var dur = _track?.DurationMs ?? 0;
        return dur > 0 ? Math.Clamp(pos, 0, dur) : Math.Max(0, pos);
    }

    private void Reset() { _track = null; _pendingMeta = null; _paused = true; _ended = false; _anchorPos = 0; }

    /// <summary>当前状态；没在放（或客户端退出了）返回 null。</summary>
    public State? Current()
    {
        if (_track == null) return null;
        var now = NowMs;
        return new State(_track, !_paused && !_ended, TimeSpan.FromMilliseconds(PositionAt(now)), TimeSpan.FromMilliseconds(_track.DurationMs));
    }

    public static bool ClientRunning()
    {
        var ps = Process.GetProcessesByName("cloudmusic");
        var any = ps.Length > 0;
        foreach (var p in ps) p.Dispose();
        return any;
    }

    /// <summary>网易云主窗口标题“歌名 - 歌手”，日志里没歌名时兜底。</summary>
    public static (string title, string artist)? WindowTitle()
    {
        foreach (var p in Process.GetProcessesByName("cloudmusic"))
        {
            using (p)
            {
                string t;
                try { t = p.MainWindowTitle; } catch { continue; }
                if (string.IsNullOrWhiteSpace(t) || t == "网易云音乐") continue;
                var i = t.LastIndexOf(" - ", StringComparison.Ordinal);
                return i > 0 ? (t[..i].Trim(), t[(i + 3)..].Trim()) : (t.Trim(), "");
            }
        }
        return null;
    }
}
