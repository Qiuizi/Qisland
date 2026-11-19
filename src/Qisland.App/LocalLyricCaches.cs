using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 酷狗 / 汽水音乐自己下载在本机的歌词缓存（只读，你已同意）：这两个播放器的 SMTC 不给时长、歌词站也常搜不准，
/// 直接读它们已经下好的那份最准。只打开文件读，FileShare 全开，不加锁、不改、不读登录信息。
/// </summary>
public static class LocalLyricCaches
{
    private static readonly ConcurrentDictionary<string, TimeSpan> Durations = new();
    private static string Key(string title, string artist) => Lrc.Norm(title) + "|" + Lrc.Norm(artist);

    /// <summary>从 KRC 头里读到的时长（酷狗 SMTC 不给时长时补上）。</summary>
    public static TimeSpan? DurationFor(string title, string artist) => Durations.TryGetValue(Key(title, artist), out var d) ? d : null;

    public static List<LyricLine>? Find(string appSource, string title, string artist, TimeSpan dur)
    {
        try
        {
            if (appSource == "kugou") return Kugou(title, artist, dur);
            if (appSource.Equals("exe:SodaMusic", StringComparison.OrdinalIgnoreCase)) return Soda(title, artist);
        }
        catch (Exception ex) { App.Log($"本机歌词缓存({appSource}): {ex.GetType().Name}"); }
        return null;
    }

    // ───────────── 酷狗 ─────────────

    private static string? KugouDir()
    {
        var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuGou8", "KuGou.ini");
        if (File.Exists(ini) && new FileInfo(ini).Length < 1_000_000 && Krc.LyricDirFromIni(ReadShared(ini)) is { } d && Directory.Exists(d)) return d;
        foreach (var c in new[] { @"C:\KuGou\Lyric", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KuGou8", "Lyric") })
            if (Directory.Exists(c)) return c;
        return null;
    }

    private static List<LyricLine>? Kugou(string title, string artist, TimeSpan dur)
    {
        if (KugouDir() is not { } dir) return null;
        // 文件名「歌手 - 歌名-哈希.krc」：先按歌名筛（通配符里去掉特殊字符），再逐个核对
        var safe = new string(title.Where(c => !"*?[]\\/:\"<>|".Contains(c)).ToArray()).Trim();
        if (safe.Length == 0) return null;
        var files = new DirectoryInfo(dir).EnumerateFiles("*" + safe + "*.krc").Where(f => f.Length <= Krc.Limit).OrderByDescending(f => f.LastWriteTime).Take(12);
        foreach (var f in files)
        {
            var text = Krc.Decode(ReadShared(f.FullName));
            if (text == null || !Krc.Matches(f.Name, text, title, artist, dur.TotalSeconds)) continue;
            var lines = Krc.Parse(text);
            if (lines.Count(l => l.Text.Length > 0) < 3) continue;
            if (long.TryParse(Krc.Header(text, "total"), out var total) && total > 10_000) Durations[Key(title, artist)] = TimeSpan.FromMilliseconds(total);
            App.Log($"歌词: 酷狗本机缓存 · {title}");
            return lines;
        }
        return null;
    }

    // ───────────── 汽水音乐 ─────────────

    private static (string fp, string key, List<LyricLine>? lines) _sodaLast;
    private const long SodaMax = 256L * 1024 * 1024;

    private static List<LyricLine>? Soda(string title, string artist)
    {
        var db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SodaMusic", "LunaCacheV2", "entries.db");
        if (!File.Exists(db)) return null;
        var fi = new FileInfo(db);
        if (fi.Length > SodaMax) { App.Log("汽水缓存太大，跳过"); return null; }
        var fp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        var key = Key(title, artist);
        if (_sodaLast.fp == fp && _sodaLast.key == key) return _sodaLast.lines;

        // 按 4 MB 一块流式找歌名（MessagePack 字符串），找到后只读附近 ±48 KB，不把整个库读进内存
        var patterns = SodaScan.StringPatterns(title).ToList();
        string? found = null;
        using (var fs = new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            const int block = 4 * 1024 * 1024;
            var overlap = patterns.Max(p => p.Length);
            var buf = new byte[block + overlap];
            long basePos = 0; int carry = 0;
            while (found == null)
            {
                var n = fs.Read(buf, carry, block);
                if (n <= 0) break;
                var span = buf.AsSpan(0, carry + n);
                foreach (var p in patterns)
                {
                    var off = 0;
                    while (found == null && off < span.Length)
                    {
                        var i = span[off..].IndexOf(p);
                        if (i < 0) break;
                        var at = basePos + off + i;
                        found = Around(fs, at, title, artist);
                        off += i + 1;
                    }
                    if (found != null) break;
                }
                carry = Math.Min(overlap, span.Length);
                span[^carry..].CopyTo(buf);
                basePos += span.Length - carry;
                fs.Position = basePos + carry;
            }
        }
        var lines = found != null ? Krc.Parse(found) : null;
        if (lines != null && lines.Count(l => l.Text.Length > 0) < 3) lines = null;
        if (lines != null) App.Log($"歌词: 汽水本机缓存 · {title}");
        _sodaLast = (fp, key, lines);
        return lines;
    }

    private static string? Around(FileStream fs, long at, string title, string artist)
    {
        var keep = fs.Position;
        try
        {
            var start = Math.Max(0, at - SodaScan.Window);
            var len = (int)Math.Min(fs.Length - start, SodaScan.Window * 2);
            var win = new byte[len];
            fs.Position = start;
            var got = 0;
            while (got < len) { var r = fs.Read(win, got, len - got); if (r <= 0) break; got += r; }
            return SodaScan.FindLyric(win.AsSpan(0, got), (int)(at - start), title, artist);
        }
        finally { fs.Position = keep; }
    }

    private static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var b = new byte[fs.Length];
        var got = 0;
        while (got < b.Length) { var r = fs.Read(b, got, b.Length - got); if (r <= 0) break; got += r; }
        return b;
    }
}
