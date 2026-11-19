using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>歌词搜索结果里的一首候选歌。DurationSec 不知道时填 0。</summary>
public sealed record LyricCandidate(string Id, string Title, IReadOnlyList<string> Artists, double DurationSec);

/// <summary>
/// 按「歌名 + 歌手 + 时长」挑歌词候选：宁可不显示，也不显示另一个版本的歌词。
/// - 两边时长都知道时，差超过 3 秒就不要（Live、加长版、剪辑版的时间轴整体对不上）；
/// - 候选是 Live / Remix / 伴奏 / DJ 版之类、而正在放的不是，就不要（反过来只扣分）；
/// - 分数越小越好：时长差 × 2，歌名不完全一致 +3，歌手不完全一致 +1。
/// </summary>
public static partial class LyricMatch
{
    public const double MaxDurationDiff = 3.0;

    [GeneratedRegex(@"(\blive\b|现场|演唱会|\bremix\b|伴奏|\binst(?:rumental)?\b|纯音乐|karaoke|卡拉ok|\bdj\b|\bcover\b|翻唱|\bacoustic\b|钢琴版|吉他版|加速版|降速版|\bsped\s*up\b|\bslowed\b|\bnightcore\b|女声版|男声版|铃声版|\bdemo\b|片段)", RegexOptions.IgnoreCase)]
    private static partial Regex Variant();

    /// <summary>歌名里带版本标记（Live、Remix、伴奏……）。</summary>
    public static bool IsVariant(string title) => Variant().IsMatch(title ?? "");

    /// <summary>null 表示不能用；否则分数越小越像。wantDurationSec ≤ 5 表示时长未知（比如酷狗不报时长）。</summary>
    public static double? Score(string wantTitle, string wantArtist, double wantDurationSec, LyricCandidate c)
    {
        var wt = Lrc.Norm(wantTitle); var ct = Lrc.Norm(c.Title);
        if (wt.Length == 0 || ct.Length == 0) return null;
        if (!(wt == ct || wt.Contains(ct) || ct.Contains(wt))) return null;
        if (!ArtistOk(wantArtist, c.Artists, out var artistExact)) return null;
        var wantVar = IsVariant(wantTitle); var candVar = IsVariant(c.Title);
        if (candVar && !wantVar) return null;
        double score = 0;
        if (wantVar && !candVar) score += 5;
        if (wantDurationSec > 5 && c.DurationSec > 5)
        {
            var diff = Math.Abs(wantDurationSec - c.DurationSec);
            if (diff > MaxDurationDiff) return null;
            score += diff * 2;
        }
        else score += 2; // 时长对不了，稍微不那么放心
        if (wt != ct) score += 3;
        if (!artistExact) score += 1;
        return score;
    }

    public static LyricCandidate? Best(string wantTitle, string wantArtist, double wantDurationSec, IEnumerable<LyricCandidate> cands)
    {
        LyricCandidate? best = null; var bestScore = double.MaxValue;
        foreach (var c in cands)
            if (Score(wantTitle, wantArtist, wantDurationSec, c) is { } s && s < bestScore) { bestScore = s; best = c; }
        return best;
    }

    /// <summary>歌手匹配：任何一边包含另一边即可（合唱、feat.、「A / B」「A、B」都算）。没给歌手时不限制。</summary>
    public static bool ArtistOk(string want, IEnumerable<string> have, out bool exact)
    {
        exact = false;
        if (string.IsNullOrWhiteSpace(want)) return true;
        var w = Lrc.Norm(want);
        var parts = SplitArtists(want).Select(Lrc.Norm).Where(x => x.Length > 0).ToList();
        foreach (var h in have)
        {
            var n = Lrc.Norm(h);
            if (n.Length == 0) continue;
            if (n == w) { exact = true; return true; }
            if (w.Contains(n) || n.Contains(w) || parts.Any(p => p == n || n.Contains(p) || p.Contains(n))) return true;
        }
        return false;
    }

    public static IEnumerable<string> SplitArtists(string s) =>
        Regex.Split(s ?? "", @"\s*(?:/|、|,|，|&|;|；|\bfeat\.?|\bft\.?|\bwith\b|\bx\b)\s*", RegexOptions.IgnoreCase).Where(x => x.Trim().Length > 0);
}

/// <summary>各播放器在系统媒体会话里报的歌名 / 歌手有自己的写法，这里统一成「歌名」「歌手」。</summary>
public static class MediaText
{
    /// <summary>
    /// Apple Music（Windows 版）把「歌手 — 专辑」塞在 Artist 字段里（中间是长破折号），专辑字段反而空着。
    /// 拆出前半段当歌手；拆不出就原样返回。
    /// </summary>
    public static (string artist, string? album) SplitAppleArtist(string artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(artist)) return (artist ?? "", album);
        foreach (var sep in new[] { " \u2014 ", " \u2013 ", "\u2014" })
        {
            var i = artist.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0)
            {
                var a = artist[..i].Trim();
                var b = artist[(i + sep.Length)..].Trim();
                return (a, string.IsNullOrWhiteSpace(album) ? (b.Length > 0 ? b : null) : album);
            }
        }
        return (artist.Trim(), album);
    }

    /// <summary>有的播放器（部分酷狗 / 酷我版本、网页播放器）不报歌手，把「歌名 - 歌手」整个塞在标题里。</summary>
    public static (string title, string artist) SplitTitleArtist(string title, string artist)
    {
        title = (title ?? "").Trim(); artist = (artist ?? "").Trim();
        if (artist.Length > 0) return (title, artist);
        var i = title.LastIndexOf(" - ", StringComparison.Ordinal);
        return i > 0 && i < title.Length - 3 ? (title[..i].Trim(), title[(i + 3)..].Trim()) : (title, artist);
    }
}
