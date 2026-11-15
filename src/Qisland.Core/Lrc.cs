using System.Globalization;
using System.Text.RegularExpressions;

namespace Qisland.Core;

public sealed record LyricLine(TimeSpan At, string Text, string? Translation = null);

/// <summary>LRC 歌词解析：[mm:ss.xx] 时间标签（一行可多个）、[offset:±ms]，可合并翻译歌词。</summary>
public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Tag();
    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
    // 网易云在歌词开头塞的「作词 : xxx」「作曲 : xxx」之类的信息行
    [GeneratedRegex(@"^\s*(作词|作曲|编曲|制作人|混音|母带|和声|吉他|贝斯|鼓|录音|监制|出品|发行|企划|统筹|OP|SP|Lyricist|Composer|Arranger|Producer)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex Credit();

    public static List<LyricLine> Parse(string lrc, string? translation = null)
    {
        var main = ParseRaw(lrc);
        if (main.Count == 0) return [];
        if (!string.IsNullOrWhiteSpace(translation))
        {
            var tr = ParseRaw(translation).Where(t => t.Text.Length > 0).ToList();
            if (tr.Count > 0)
            {
                // 翻译行和原文时间戳往往差几十毫秒：取 300 ms 内最近的一条
                for (var i = 0; i < main.Count; i++)
                {
                    var m = main[i];
                    var best = tr.MinBy(t => Math.Abs((t.At - m.At).TotalMilliseconds));
                    if (best != null && Math.Abs((best.At - m.At).TotalMilliseconds) <= 300 && best.Text != m.Text)
                        main[i] = m with { Translation = best.Text };
                }
            }
        }
        return main;
    }

    private static List<LyricLine> ParseRaw(string lrc)
    {
        var list = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Offset().Match(line) is { Success: true } om) { offset = TimeSpan.FromMilliseconds(-int.Parse(om.Groups[1].Value, CultureInfo.InvariantCulture)); continue; }
            var tags = new List<TimeSpan>();
            var pos = 0;
            Match mt;
            while ((mt = Tag().Match(line, pos)).Success && mt.Index == pos)
            {
                var ms = mt.Groups[3].Success ? mt.Groups[3].Value : "0";
                var frac = ms.Length switch { 1 => int.Parse(ms) * 100, 2 => int.Parse(ms) * 10, _ => int.Parse(ms[..3]) };
                tags.Add(new TimeSpan(0, 0, int.Parse(mt.Groups[1].Value), int.Parse(mt.Groups[2].Value), frac));
                pos = mt.Index + mt.Length;
            }
            if (tags.Count == 0) continue;
            var text = line[pos..].Trim();
            // 逐字歌词（<00:12.34>）去掉内嵌时间
            text = Regex.Replace(text, @"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", "").Trim();
            if (Credit().IsMatch(text)) continue;
            foreach (var t in tags) list.Add(new LyricLine(t + offset < TimeSpan.Zero ? TimeSpan.Zero : t + offset, text));
        }
        list.Sort((a, b) => a.At.CompareTo(b.At));
        return list;
    }

    /// <summary>当前应显示的行号（-1 表示还没到第一句）。</summary>
    public static int IndexAt(IReadOnlyList<LyricLine> lines, TimeSpan pos)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].At <= pos) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>歌名归一化：去掉括号里的 Live / feat. / 伴奏说明等，比较时忽略大小写和空白。</summary>
    public static string Norm(string s)
    {
        s = Regex.Replace(s, @"[\(（\[【].*?[\)）\]】]", "");
        s = Regex.Replace(s, @"\s+-\s+.*$", "");
        return Regex.Replace(s, @"[\s·・'’""“”!！?？,，.。]", "").ToLowerInvariant();
    }
}
