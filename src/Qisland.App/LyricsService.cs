using Qisland.Core;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qisland.App;

/// <summary>
/// 同步歌词：先查本地缓存，再查网易云（中文歌最全，还带翻译），最后 LRCLIB。
/// 只按歌名 + 歌手 + 时长匹配，对不上宁可不显示，也不显示错歌词。
/// </summary>
public sealed class LyricsService
{
    public static string Dir => Path.Combine(App.DataDir, "lyrics");
    public static string OffFlag => Path.Combine(App.DataDir, "lyrics.off");
    public static bool Enabled { get => !File.Exists(OffFlag); set { try { if (value) File.Delete(OffFlag); else File.WriteAllText(OffFlag, ""); } catch { } } }

    private static readonly HttpClient Http = CreateHttp();
    private string _key = "";
    public IReadOnlyList<LyricLine>? Lines { get; private set; }
    public event Action? Changed;

    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Qisland");
        h.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
        return h;
    }

    public void ShowDemo(IReadOnlyList<LyricLine> lines) { _key = "demo"; Lines = lines; Changed?.Invoke(); }

    /// <summary>媒体变化时调用；同一首歌只查一次。</summary>
    public void OnTrack(MediaState? m)
    {
        if (_key == "demo") return;
        if (m == null || m.Video || !Enabled || string.IsNullOrWhiteSpace(m.Title))
        {
            if (Lines != null) { Lines = null; _key = ""; Changed?.Invoke(); }
            return;
        }
        var key = $"{m.Title}\u0001{m.Artist}";
        if (key == _key) return;
        _key = key;
        Lines = null;
        Changed?.Invoke();
        _ = LoadAsync(key, m.Title, m.Artist, m.Duration);
    }

    private async Task LoadAsync(string key, string title, string artist, TimeSpan dur)
    {
        List<LyricLine>? lines = null;
        try { lines = await Task.Run(() => Fetch(title, artist, dur)); }
        catch (Exception ex) { App.Log("歌词: " + ex.Message); }
        if (key != _key) return;      // 已经切歌了
        Lines = lines is { Count: > 0 } ? lines : null;
        Changed?.Invoke();
    }

    private static async Task<List<LyricLine>?> Fetch(string title, string artist, TimeSpan dur)
    {
        Directory.CreateDirectory(Dir);
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Lrc.Norm(title) + "|" + Lrc.Norm(artist))))[..16];
        var file = Path.Combine(Dir, name + ".lrc");
        var tfile = Path.Combine(Dir, name + ".tr.lrc");
        var none = Path.Combine(Dir, name + ".none");
        if (File.Exists(file)) return Lrc.Parse(File.ReadAllText(file), File.Exists(tfile) ? File.ReadAllText(tfile) : null);
        if (File.Exists(none) && DateTime.Now - File.GetLastWriteTime(none) < TimeSpan.FromDays(3)) return null;

        var (lrc, tr) = await NetEase(title, artist, dur);
        if (lrc == null) (lrc, tr) = (await LrcLib(title, artist, dur), null);
        if (lrc == null || Lrc.Parse(lrc).Count(l => l.Text.Length > 0) < 3)
        {
            File.WriteAllText(none, "");
            App.Log($"歌词: 没找到 {title} - {artist}");
            return null;
        }
        File.WriteAllText(file, lrc);
        if (tr != null) File.WriteAllText(tfile, tr);
        return Lrc.Parse(lrc, tr);
    }

    private static bool NameOk(string a, string b)
    {
        var x = Lrc.Norm(a); var y = Lrc.Norm(b);
        return x.Length > 0 && (x == y || x.Contains(y) || y.Contains(x));
    }

    private static bool ArtistOk(string want, IEnumerable<string> have)
    {
        if (string.IsNullOrWhiteSpace(want)) return true;
        var w = Lrc.Norm(want);
        return have.Any(h => { var n = Lrc.Norm(h); return n.Length > 0 && (w.Contains(n) || n.Contains(w)); });
    }

    private static async Task<(string?, string?)> NetEase(string title, string artist, TimeSpan dur)
    {
        try
        {
            var q = Uri.EscapeDataString($"{title} {artist}".Trim());
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://music.163.com/api/search/get?s={q}&type=1&limit=10"));
            if (!doc.RootElement.TryGetProperty("result", out var res) || !res.TryGetProperty("songs", out var songs)) return (null, null);
            long id = 0; var bestScore = int.MaxValue;
            foreach (var s in songs.EnumerateArray())
            {
                var n = s.GetProperty("name").GetString() ?? "";
                var ars = s.TryGetProperty("artists", out var a) ? a.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").ToList() : [];
                if (!NameOk(n, title) || !ArtistOk(artist, ars)) continue;
                var d = s.TryGetProperty("duration", out var dd) ? dd.GetInt64() / 1000.0 : 0;
                var diff = dur > TimeSpan.FromSeconds(5) && d > 0 ? Math.Abs(d - dur.TotalSeconds) : 0;
                if (diff > 6) continue;
                var score = (int)diff + (Lrc.Norm(n) == Lrc.Norm(title) ? 0 : 3);
                if (score < bestScore) { bestScore = score; id = s.GetProperty("id").GetInt64(); }
            }
            if (id == 0) return (null, null);
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
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var synced = s.TryGetProperty("syncedLyrics", out var sl) ? sl.GetString() : null;
                if (string.IsNullOrWhiteSpace(synced)) continue;
                var d = s.TryGetProperty("duration", out var dd) && dd.ValueKind == JsonValueKind.Number ? dd.GetDouble() : 0;
                if (dur > TimeSpan.FromSeconds(5) && d > 0 && Math.Abs(d - dur.TotalSeconds) > 6) continue;
                return synced;
            }
        }
        catch (Exception ex) { App.Log("歌词(LRCLIB): " + ex.Message); }
        return null;
    }
}
