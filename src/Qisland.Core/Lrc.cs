using System.Globalization;
using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>一句歌词。Words 只有逐字歌词（酷狗 KRC、带 &lt;mm:ss.xx&gt; 的增强 LRC）才有，时间都是整首歌里的绝对时间。</summary>
public sealed record LyricLine(TimeSpan At, string Text, string? Translation = null, IReadOnlyList<LyricWord>? Words = null);

/// <summary>逐字歌词里的一个字 / 词：开始时间、唱多久、文字（拼起来就是整句）。</summary>
public sealed record LyricWord(TimeSpan At, TimeSpan Duration, string Text);

/// <summary>LRC 歌词解析：[mm:ss.xx] 时间标签（一行可多个）、[offset:±ms]，可合并翻译歌词。</summary>
public static partial class Lrc
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Tag();
    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTag();
    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
    // 网易云在歌词开头塞的「作词 : xxx」「作曲 : xxx」之类的信息行
    [GeneratedRegex(@"^\s*(作词|作曲|编曲|制作人|混音|母带|和声|吉他|贝斯|鼓|录音|监制|出品|发行|企划|统筹|OP|SP|Lyricist|Composer|Arranger|Producer)\s*[:：]", RegexOptions.IgnoreCase)]
    private static partial Regex Credit();

    public static List<LyricLine> Parse(string lrc, string? translation = null)
    {
        var main = ParseRaw(lrc);
        if (main.Count == 0) return [];
        // 没有单独的翻译歌词时，有的 LRC（LRCLIB 上常见）把原文和译文写成同一时间戳的两行：
        // 以前排序后取「最后一行」，岛上显示的就成了译文、下一句也错位。这里把同一时刻的第二行并成翻译。
        if (string.IsNullOrWhiteSpace(translation)) main = MergeSameTime(main);
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
        var lines = lrc.Replace("\r", "").Split('\n');
        // [offset:+500] 表示歌词整体提前 0.5 秒。它可能出现在文件任意位置（不一定在第一句之前），对整首都生效
        var offset = TimeSpan.Zero;
        foreach (var raw in lines)
            if (Offset().Match(raw.Trim()) is { Success: true } om && int.TryParse(om.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ofs))
                offset = TimeSpan.FromMilliseconds(-ofs);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Offset().IsMatch(line)) continue;
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
            var body = line[pos..];
            // 增强 LRC 的逐字时间（<00:12.34>）：只有一个行时间标签时才当逐字用（同一句重复出现的行没法对上）
            var words = tags.Count == 1 ? EnhancedWords(body, offset) : null;
            var text = WordTag().Replace(body, "").Trim();
            if (Credit().IsMatch(text)) continue;
            if (words != null && string.Concat(words.Select(w => w.Text)) != text) words = null;
            foreach (var t in tags) list.Add(new LyricLine(t + offset < TimeSpan.Zero ? TimeSpan.Zero : t + offset, text, null, words));
        }
        // 稳定排序：同一时间戳的行保持文件里的先后（List.Sort 不稳定，原文和译文会随机颠倒）
        return FillWordEnds(list.OrderBy(l => l.At).ToList());
    }

    private static TimeSpan Ts(Match m)
    {
        var ms = m.Groups[3].Success ? m.Groups[3].Value : "0";
        var frac = ms.Length switch { 1 => int.Parse(ms) * 100, 2 => int.Parse(ms) * 10, _ => int.Parse(ms[..3]) };
        return new TimeSpan(0, 0, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), frac);
    }

    /// <summary>「&lt;t1&gt;字&lt;t2&gt;字&lt;t3&gt;」→ 逐字；最后一个字没有结束时间时先记 0，等 FillWordEnds 用下一句的开始补上。</summary>
    private static List<LyricWord>? EnhancedWords(string body, TimeSpan offset)
    {
        var ms = WordTag().Matches(body);
        if (ms.Count == 0) return null;
        var words = new List<LyricWord>();
        for (var i = 0; i < ms.Count; i++)
        {
            var start = ms[i].Index + ms[i].Length;
            var end = i + 1 < ms.Count ? ms[i + 1].Index : body.Length;
            var txt = body[start..end];
            var at = Ts(ms[i]) + offset;
            if (at < TimeSpan.Zero) at = TimeSpan.Zero;
            var dur = i + 1 < ms.Count ? Ts(ms[i + 1]) + offset - at : TimeSpan.Zero;
            if (txt.Length == 0) continue;
            words.Add(new LyricWord(at, dur < TimeSpan.Zero ? TimeSpan.Zero : dur, txt));
        }
        return TrimWords(words);
    }

    /// <summary>首尾空白去掉，保证逐字拼起来和整句 Text（Trim 过）一致。</summary>
    public static List<LyricWord>? TrimWords(List<LyricWord> words)
    {
        if (words.Count == 0) return null;
        words[0] = words[0] with { Text = words[0].Text.TrimStart() };
        words[^1] = words[^1] with { Text = words[^1].Text.TrimEnd() };
        words.RemoveAll(w => w.Text.Length == 0);
        return words.Count > 0 ? words : null;
    }

    /// <summary>没有结束时间的最后一个字：唱到下一句开始（最多 3 秒，最少 0.2 秒）。</summary>
    public static List<LyricLine> FillWordEnds(List<LyricLine> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Words is not { Count: > 0 } ws || ws[^1].Duration > TimeSpan.Zero) continue;
            var next = i + 1 < lines.Count ? lines[i + 1].At : ws[^1].At + TimeSpan.FromSeconds(1);
            var d = TimeSpan.FromMilliseconds(Math.Clamp((next - ws[^1].At).TotalMilliseconds, 200, 3000));
            var copy = ws.ToList();
            copy[^1] = copy[^1] with { Duration = d };
            lines[i] = lines[i] with { Words = copy };
        }
        return lines;
    }

    /// <summary>逐字进度：(当前唱到第几个字, 这个字唱了几成)。还没开始 = (-1, 0)，唱完 = (Count, 0)。</summary>
    public static (int Index, double Frac) WordAt(IReadOnlyList<LyricWord> words, TimeSpan pos)
    {
        if (words.Count == 0 || pos < words[0].At) return (-1, 0);
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var end = w.At + w.Duration;
            if (pos < w.At) return (i, 0);          // 两个字之间的空档：停在下一个字开头
            if (pos < end) return (i, w.Duration.TotalMilliseconds <= 0 ? 1 : (pos - w.At).TotalMilliseconds / w.Duration.TotalMilliseconds);
        }
        return (words.Count, 0);
    }

    /// <summary>按字数算的整句进度（0–1）：自检和没有字宽信息时用。</summary>
    public static double CharProgress(IReadOnlyList<LyricWord> words, TimeSpan pos)
    {
        var total = words.Sum(w => w.Text.Length);
        if (total == 0) return 0;
        var (i, f) = WordAt(words, pos);
        if (i < 0) return 0;
        if (i >= words.Count) return 1;
        var before = words.Take(i).Sum(w => w.Text.Length);
        return Math.Clamp((before + f * words[i].Text.Length) / total, 0, 1);
    }

    private static List<LyricLine> MergeSameTime(List<LyricLine> list)
    {
        var res = new List<LyricLine>(list.Count);
        foreach (var l in list)
        {
            if (res.Count > 0 && res[^1] is var prev && prev.At == l.At && prev.Translation == null
                && prev.Text.Length > 0 && l.Text.Length > 0 && prev.Text != l.Text)
            {
                res[^1] = prev with { Translation = l.Text };
                continue;
            }
            if (res.Count > 0 && res[^1].At == l.At && res[^1].Text == l.Text) continue; // 完全重复的行
            res.Add(l);
        }
        return res;
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
