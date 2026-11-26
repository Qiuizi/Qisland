using Qisland.Core;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qisland.App;

/// <summary>
/// 同步歌词：先查本地缓存，再查网易云（中文歌最全，还带翻译），最后 LRCLIB（公开免费的歌词库）。
/// - 网易云日志 / SMTC 合并后给了 songId：直接按 id 取，不搜索；
/// - 其它播放器（Apple Music、QQ 音乐、酷狗、Spotify……）按歌名 + 歌手 + 时长搜，时长差 &gt; 3 秒、Live / Remix / 伴奏版一律不要（<see cref="LyricMatch"/>）；
/// - 换歌时旧查询的结果作废（key 不一致就丢），不会串到新歌；
/// - 每首歌可以在歌词上滚滚轮微调 ±0.3 秒，记在 lyrics\offsets.json 里，下次放同一首自动用上；
/// - 另外按播放器整体校准（参考 pyisland）：有的播放器报给系统的进度总是慢 / 快一截，按住 Ctrl 滚滚轮或在设置里调，
///   记在 lyrics\player-offsets.json，这个播放器放的每首歌都加上；和按歌的偏移叠加。
/// 对不上宁可不显示，也不显示错歌词。
/// </summary>
public sealed class LyricsService
{
    public static string Dir => Path.Combine(App.DataDir, "lyrics");
    public static string OffFlag => Path.Combine(App.DataDir, "lyrics.off");
    public static bool Enabled { get => !File.Exists(OffFlag); set { try { if (value) File.Delete(OffFlag); else File.WriteAllText(OffFlag, ""); } catch { } } }
    private static string OffsetsPath => Path.Combine(Dir, "offsets.json");
    private static string PlayerOffsetsPath => Path.Combine(Dir, "player-offsets.json");

    private static readonly HttpClient Http = CreateHttp();
    private string _key = "";
    private string _name = "";
    private Dictionary<string, double>? _offsets;
    public IReadOnlyList<LyricLine>? Lines { get; private set; }
    /// <summary>这首歌用户调过的偏移（正数 = 歌词提前）。</summary>
    public TimeSpan UserOffset { get; private set; }
    public event Action? Changed;

    /// <summary>按播放器的整体校准：键是 MediaState.AppSource（music / qqmusic / spotify / exe:xxx…），Name 是显示名。</summary>
    public sealed record PlayerOffset(string Name, double Seconds);
    private Dictionary<string, PlayerOffset>? _players;
    /// <summary>当前播放器（AppSource）和显示名；没在放时是空串。</summary>
    public string Player { get; private set; } = "";
    public string PlayerName { get; private set; } = "";
    /// <summary>当前播放器的整体校准（开关关掉时为 0）。</summary>
    public TimeSpan PlayerShift => Features.On(Features.PlayerLyricOffset) && Player.Length > 0 && LoadPlayers().TryGetValue(Player, out var p) ? TimeSpan.FromSeconds(p.Seconds) : TimeSpan.Zero;
    public IReadOnlyDictionary<string, PlayerOffset> PlayerOffsets => LoadPlayers();
    public event Action? PlayerShiftChanged;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Qisland (https://github.com/Qiuizi/Qisland)");
        h.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return h;
    }

    public void ShowDemo(IReadOnlyList<LyricLine> lines) { _key = "demo"; Lines = lines; Changed?.Invoke(); }

    /// <summary>缓存文件名：有网易云 songId 用 id，否则用歌名 + 歌手 + 时长（取整到 2 秒，区分不同版本）。</summary>
    private static string CacheName(string title, string artist, TimeSpan dur, string? songId) =>
        songId is { Length: > 0 } ? "ncm-" + songId
        : "v2-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Lrc.Norm(title) + "|" + Lrc.Norm(artist) + "|" + (dur > TimeSpan.FromSeconds(5) ? ((int)dur.TotalSeconds / 2).ToString() : "?"))))[..16];

    /// <summary>媒体变化时调用；同一首歌只查一次。</summary>
    public void OnTrack(MediaState? m)
    {
        if (_key == "demo") return;
        if (m is { AppSource.Length: > 0 }) { Player = m.AppSource; PlayerName = m.App.Length > 0 ? m.App : m.AppSource; }
        // 没有歌手、也不是网易云日志里的歌：多半是浏览器标签页 / 网页视频的标题，不拿它去歌词站搜（搜不到，还把网页标题发了出去）
        if (m == null || m.Video || !Enabled || string.IsNullOrWhiteSpace(m.Title) || (string.IsNullOrWhiteSpace(m.Artist) && m.SongId == null))
        {
            if (Lines != null) { Lines = null; _key = ""; Changed?.Invoke(); }
            return;
        }
        var key = $"{m.Title}\u0001{m.Artist}\u0001{m.SongId}";
        if (key == _key) return;
        // 同一首歌只是后来才知道了 songId（网易云 SMTC + 日志合并）：按 id 重取（更准），但取到之前先留着现在的，免得闪一下
        var sameSong = _key.StartsWith($"{m.Title}\u0001{m.Artist}\u0001", StringComparison.Ordinal) && Lines != null;
        _key = key;
        _name = CacheName(m.Title, m.Artist, m.Duration, m.SongId);
        UserOffset = TimeSpan.FromSeconds(LoadOffsets().GetValueOrDefault(_name));
        if (!sameSong) { Lines = null; Changed?.Invoke(); }
        _ = LoadAsync(key, m.Title, m.Artist, m.Duration, m.SongId, keepIfNone: sameSong, app: m.AppSource);
    }

    /// <summary>在歌词上滚滚轮：±0.3 秒一档。正数 = 歌词提前（觉得歌词慢了就往上滚）。</summary>
    public void Nudge(double seconds)
    {
        if (Lines == null || _name.Length == 0) return;
        var v = Math.Round(Math.Clamp(UserOffset.TotalSeconds + seconds, -10, 10), 1);
        UserOffset = TimeSpan.FromSeconds(v);
        var d = LoadOffsets();
        if (Math.Abs(v) < 0.05) d.Remove(_name); else d[_name] = v;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(OffsetsPath, JsonSerializer.Serialize(d)); } catch { }
        Changed?.Invoke();
    }

    /// <summary>Ctrl + 滚轮：当前播放器整体 ±0.1 秒。</summary>
    public void NudgePlayer(double seconds)
    {
        if (Player.Length == 0) return;
        var cur = LoadPlayers().TryGetValue(Player, out var p) ? p.Seconds : 0;
        SetPlayerOffset(Player, PlayerName, cur + seconds);
    }

    /// <summary>设置页 / 滚轮改某个播放器的校准；0 就删掉这条（名单里保留显示名，方便再调回来时认得）。</summary>
    public void SetPlayerOffset(string player, string name, double seconds)
    {
        if (player.Length == 0) return;
        var v = Math.Round(Math.Clamp(seconds, -5, 5), 1);
        if (Math.Abs(v) < 0.05) v = 0;
        var d = LoadPlayers();
        if (d.TryGetValue(player, out var same) && same.Seconds == v && (name.Length == 0 || same.Name == name)) return;   // 已经到头（±5 秒）或没变：不重复写
        d[player] = new PlayerOffset(name.Length > 0 ? name : d.TryGetValue(player, out var old) ? old.Name : player, v);
        try { Directory.CreateDirectory(Dir); File.WriteAllText(PlayerOffsetsPath, JsonSerializer.Serialize(d, Readable)); }
        catch (Exception ex) { App.Log("保存播放器歌词校准: " + ex.Message); }
        App.Log($"歌词校准 {d[player].Name} {d[player].Seconds:+0.0;-0.0;0} 秒");
        if (player == Player) PlayerShiftChanged?.Invoke();
    }

    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };   // 播放器名照原样写中文，手动打开也看得懂

    private Dictionary<string, PlayerOffset> LoadPlayers()
    {
        if (_players != null) return _players;
        try { _players = File.Exists(PlayerOffsetsPath) ? JsonSerializer.Deserialize<Dictionary<string, PlayerOffset>>(File.ReadAllText(PlayerOffsetsPath)) : null; }
        catch (Exception ex) { App.Log("读播放器歌词校准: " + ex.Message); }
        return _players ??= [];
    }

    private Dictionary<string, double> LoadOffsets()
    {
        if (_offsets != null) return _offsets;
        try { _offsets = File.Exists(OffsetsPath) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(OffsetsPath)) : null; } catch { }
        return _offsets ??= [];
    }

    private async Task LoadAsync(string key, string title, string artist, TimeSpan dur, string? songId, bool keepIfNone = false, string app = "")
    {
        List<LyricLine>? lines = null;
        // 酷狗 / 汽水：先读它们自己下好的本机缓存（只读），没有再上网查
        try { lines = await Task.Run(() => LocalLyricCaches.Find(app, title, artist, dur)); }
        catch (Exception ex) { App.Log("本机歌词: " + ex.GetType().Name); }
        if (key != _key) return;
        if (lines is { Count: > 0 }) { Lines = lines; Changed?.Invoke(); return; }
        try { lines = await Task.Run(() => Fetch(title, artist, dur, songId)); }
        catch (Exception ex) { App.Log("歌词: " + ex.Message); }
        if (key != _key) return;      // 已经切歌了：迟到的结果丢掉
        if (lines is not { Count: > 0 } && keepIfNone) return;
        Lines = lines is { Count: > 0 } ? lines : null;
        Changed?.Invoke();
    }

    private static async Task<List<LyricLine>?> Fetch(string title, string artist, TimeSpan dur, string? songId = null)
    {
        Directory.CreateDirectory(Dir);
        var name = CacheName(title, artist, dur, songId);
        var file = Path.Combine(Dir, name + ".lrc");
        var tfile = Path.Combine(Dir, name + ".tr.lrc");
        var none = Path.Combine(Dir, name + ".none");
        if (File.Exists(file)) return Lrc.Parse(File.ReadAllText(file), File.Exists(tfile) ? File.ReadAllText(tfile) : null);
        if (File.Exists(none) && DateTime.Now - File.GetLastWriteTime(none) < TimeSpan.FromDays(songId != null ? 3 : 1)) return null;

        var (lrc, tr) = await NetEase(title, artist, dur, songId);
        var from = songId != null ? "网易云(id)" : "网易云";
        if (lrc == null && songId == null) { (lrc, tr) = (await LrcLib(title, artist, dur), null); from = "LRCLIB"; }
        if (lrc == null || Lrc.Parse(lrc).Count(l => l.Text.Length > 0) < 3)
        {
            File.WriteAllText(none, "");
            App.Log($"歌词: 没找到 {title} - {artist}");
            return null;
        }
        File.WriteAllText(file, lrc);
        if (tr != null) File.WriteAllText(tfile, tr);
        App.Log($"歌词: {from} · {title}");
        return Lrc.Parse(lrc, tr);
    }

    private static async Task<(string?, string?)> NetEase(string title, string artist, TimeSpan dur, string? songId = null)
    {
        try
        {
            long id = 0;
            if (!long.TryParse(songId, out id)) // 网易云日志直接给了歌曲 id 就不用搜
            {
                var q = Uri.EscapeDataString($"{title} {artist}".Trim());
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://music.163.com/api/search/get?s={q}&type=1&limit=15"));
                if (!doc.RootElement.TryGetProperty("result", out var res) || !res.TryGetProperty("songs", out var songs)) return (null, null);
                var cands = songs.EnumerateArray().Select(s => new LyricCandidate(
                    s.GetProperty("id").GetInt64().ToString(),
                    s.GetProperty("name").GetString() ?? "",
                    s.TryGetProperty("artists", out var a) ? a.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList() : [],
                    s.TryGetProperty("duration", out var dd) && dd.TryGetInt64(out var ms) ? ms / 1000.0 : 0)).ToList();
                if (LyricMatch.Best(title, artist, dur.TotalSeconds, cands) is not { } best) return (null, null);
                id = long.Parse(best.Id);
            }
            using var ly = JsonDocument.Parse(await Http.GetStringAsync($"https://music.163.com/api/song/lyric?id={id}&lv=1&tv=-1"));
            string? Get(string k) => ly.RootElement.TryGetProperty(k, out var o) && o.TryGetProperty("lyric", out var l) ? l.GetString() : null;
            var lrc = Get("lrc");
            return string.IsNullOrWhiteSpace(lrc) || !lrc.Contains('[') ? (null, null) : (lrc, Get("tlyric") is { Length: > 10 } t ? t : null);
        }
        catch (Exception ex) { App.Log("歌词(网易云): " + ex.Message); return (null, null); }
    }

    private static async Task<string?> LrcLib(string title, string artist, TimeSpan dur)
    {
        try
        {
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var synced = new Dictionary<string, string>();
            var cands = new List<LyricCandidate>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var lrc = s.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() : null;
                if (string.IsNullOrWhiteSpace(lrc)) continue;
                var id = s.TryGetProperty("id", out var i) ? i.ToString() : cands.Count.ToString();
                synced[id] = lrc;
                cands.Add(new LyricCandidate(id,
                    s.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : title,
                    [s.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : artist],
                    s.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0));
            }
            return LyricMatch.Best(title, artist, dur.TotalSeconds, cands) is { } best ? synced[best.Id] : null;
        }
        catch (Exception ex) { App.Log("歌词(LRCLIB): " + ex.Message); }
        return null;
    }
}
