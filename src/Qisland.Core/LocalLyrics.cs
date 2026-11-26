using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>
/// 酷狗 KRC：文件以「krc1」开头，后面每个字节和 16 字节固定表异或，再是 zlib 压缩的文本。
/// 文本里每行「[开始毫秒,时长]&lt;偏移,时长,0&gt;字…」，头部有 [ti:] [ar:] [total:毫秒] [offset:]，
/// [language:base64] 里是翻译（type 1）和罗马音（type 0）。只读本机缓存，不联网。
/// 格式参考公开资料（zengkid 的 KRC 解码 gist）与 MIT 项目岛仔的 kugou_cache；C# 重写。
/// </summary>
public static partial class Krc
{
    private static readonly byte[] Key = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
    public const int Limit = 2 * 1024 * 1024;

    public static string? Decode(byte[] bytes)
    {
        if (bytes.Length < 5 || bytes.Length > Limit || bytes[0] != 'k' || bytes[1] != 'r' || bytes[2] != 'c' || bytes[3] != '1') return null;
        var x = new byte[bytes.Length - 4];
        for (var i = 0; i < x.Length; i++) x[i] = (byte)(bytes[i + 4] ^ Key[i % Key.Length]);
        try
        {
            using var z = new ZLibStream(new MemoryStream(x), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            var buf = new byte[81920]; int n;
            while ((n = z.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, n); if (ms.Length > Limit) return null; }
            return Encoding.UTF8.GetString(ms.ToArray()).TrimStart('\uFEFF');
        }
        catch { return null; }
    }

    /// <summary>测试和工具用：把文本编码成 KRC 文件字节。</summary>
    public static byte[] Encode(string text)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(Encoding.UTF8.GetBytes(text));
        var x = ms.ToArray();
        var res = new byte[x.Length + 4];
        "krc1"u8.CopyTo(res);
        for (var i = 0; i < x.Length; i++) res[i + 4] = (byte)(x[i] ^ Key[i % Key.Length]);
        return res;
    }

    public static string? Header(string raw, string key)
    {
        foreach (var line in raw.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("[" + key + ":", StringComparison.OrdinalIgnoreCase) && l.EndsWith(']')) return l[(key.Length + 2)..^1].Trim();
        }
        return null;
    }

    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)$")] private static partial Regex LineRx();
    [GeneratedRegex(@"<\d+,\d+,\d+>")] private static partial Regex WordRx();
    [GeneratedRegex(@"<(\d+),(\d+),\d+>([^<]*)")] private static partial Regex WordPartRx();

    /// <summary>KRC 文本 → 逐行歌词（带翻译）。也接受普通 LRC（汽水有时存 LRC）。</summary>
    public static List<LyricLine> Parse(string raw)
    {
        if (!raw.Split('\n').Any(l => LineRx().IsMatch(l.Trim()))) return Lrc.Parse(raw);
        var offset = int.TryParse(Header(raw, "offset"), out var o) ? o : 0;
        var lines = new List<LyricLine>();
        foreach (var line in raw.Split('\n'))
        {
            var m = LineRx().Match(line.Trim());
            if (!m.Success) continue;
            var text = WordRx().Replace(m.Groups[3].Value, "").Trim();
            var start = long.Parse(m.Groups[1].Value) - offset;
            var at = TimeSpan.FromMilliseconds(Math.Max(0, start));
            // 逐字：<相对行首的毫秒, 时长, 0>字
            var words = new List<LyricWord>();
            foreach (Match w in WordPartRx().Matches(m.Groups[3].Value))
                if (w.Groups[3].Value.Length > 0)
                    words.Add(new LyricWord(TimeSpan.FromMilliseconds(Math.Max(0, start + long.Parse(w.Groups[1].Value))), TimeSpan.FromMilliseconds(long.Parse(w.Groups[2].Value)), w.Groups[3].Value));
            var ws = Lrc.TrimWords(words);
            if (ws != null && string.Concat(ws.Select(x => x.Text)) != text) ws = null;
            lines.Add(new LyricLine(at, text, null, ws));
        }
        // 翻译：[language:base64(JSON)]，content[].type == 1 的 lyricContent 按行对应
        if (Header(raw, "language") is { Length: > 0 } b64)
        {
            try
            {
                var json = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                foreach (var c in json?["content"] as JsonArray ?? [])
                {
                    if (c?["type"]?.ToString() != "1" || c["lyricContent"] is not JsonArray lc) continue;
                    for (var i = 0; i < lines.Count && i < lc.Count; i++)
                    {
                        var t = string.Concat((lc[i] as JsonArray ?? []).Select(x => x?.ToString() ?? "")).Trim();
                        if (t.Length > 0 && t != lines[i].Text) lines[i] = lines[i] with { Translation = t };
                    }
                }
            }
            catch { }
        }
        return lines.OrderBy(l => l.At).ToList();
    }

    [GeneratedRegex(@"-[0-9a-fA-F]{32}(?:-[0-9a-fA-F]+){0,2}$")] private static partial Regex HashSuffix();

    /// <summary>酷狗缓存文件名「歌手 - 歌名-32位哈希.krc」→ (歌手, 歌名)。</summary>
    public static (string artist, string title)? FileIdentity(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        stem = HashSuffix().Replace(stem, "");
        var i = stem.IndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? (stem[..i].Trim(), stem[(i + 3)..].Trim()) : null;
    }

    /// <summary>这个缓存文件是不是正在放的这首：歌名一致（版本标记也要一致）、歌手对得上、[total] 和时长差 ≤ 2 秒。</summary>
    public static bool Matches(string fileName, string raw, string title, string artist, double durationSec)
    {
        if (FileIdentity(fileName) is not { } id) return false;
        if (Lrc.Norm(id.title) != Lrc.Norm(title) || LyricMatch.IsVariant(id.title) != LyricMatch.IsVariant(title)) return false;
        if (!LyricMatch.ArtistOk(artist, LyricMatch.SplitArtists(id.artist), out _)) return false;
        if (durationSec > 5 && long.TryParse(Header(raw, "total"), out var total) && total > 0 && Math.Abs(total / 1000.0 - durationSec) > 2) return false;
        return true;
    }

    /// <summary>KuGou.ini 里的 LyricPath=（可能是 UTF-16）。</summary>
    public static string? LyricDirFromIni(byte[] bytes)
    {
        string text;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        else text = Encoding.UTF8.GetString(bytes);
        foreach (var line in text.Split('\n'))
        {
            var kv = line.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("LyricPath", StringComparison.OrdinalIgnoreCase) && kv[1].Trim().Length > 0) return kv[1].Trim();
        }
        return null;
    }
}

/// <summary>
/// 汽水音乐（%APPDATA%\SodaMusic\LunaCacheV2\entries.db）的本机缓存：LMDB 里存的是 MessagePack。
/// 这里不完整解析数据库，只在原始字节里找「歌名字符串」附近的歌词字符串（KRC / LRC 文本），
/// 并要求同一窗口里有歌手名、歌词头部的 [ti:] 不冲突，尽量不张冠李戴。只读，不碰登录信息和音频。
/// </summary>
public static class SodaScan
{
    public const int Window = 48 * 1024;

    /// <summary>MessagePack 字符串的几种头 + UTF-8 内容。</summary>
    public static IEnumerable<byte[]> StringPatterns(string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        if (b.Length < 32) yield return [(byte)(0xa0 | b.Length), .. b];
        if (b.Length < 256) yield return [0xd9, (byte)b.Length, .. b];
        if (b.Length < 65536) yield return [0xda, (byte)(b.Length >> 8), (byte)b.Length, .. b];
    }

    /// <summary>在窗口里找离 anchor 最近的、像歌词的 MessagePack 字符串。</summary>
    public static string? FindLyric(ReadOnlySpan<byte> data, int anchor, string title, string artist)
    {
        var artistParts = LyricMatch.SplitArtists(artist).Select(a => Encoding.UTF8.GetBytes(a.Trim())).Where(a => a.Length > 0).ToList();
        var artistSeen = false;
        foreach (var a in artistParts) if (data.IndexOf(a) >= 0) { artistSeen = true; break; }
        if (!artistSeen) return null;
        string? best = null; var bestDist = int.MaxValue;
        for (var i = 0; i < data.Length - 3; i++)
        {
            int len, head;
            switch (data[i])
            {
                case 0xd9: len = data[i + 1]; head = 2; break;
                case 0xda: len = data[i + 1] << 8 | data[i + 2]; head = 3; break;
                case 0xdb when i + 5 < data.Length: len = data[i + 1] << 24 | data[i + 2] << 16 | data[i + 3] << 8 | data[i + 4]; head = 5; break;
                default: continue;
            }
            if (len < 60 || len > Krc.Limit || i + head + len > data.Length || data[i + head] != (byte)'[') continue;
            string text;
            try { text = new UTF8Encoding(false, true).GetString(data.Slice(i + head, len)); } catch { continue; }
            if (!text.Contains('\n')) continue;
            var lines = Krc.Parse(text);
            if (lines.Count(l => l.Text.Length > 0) < 3) continue;
            if (Krc.Header(text, "ti") is { Length: > 0 } ti && Lrc.Norm(ti) != Lrc.Norm(title)) continue;
            var dist = Math.Abs(i - anchor);
            if (dist < bestDist) { bestDist = dist; best = text; }
            i += head + len - 1;
        }
        return best;
    }
}
