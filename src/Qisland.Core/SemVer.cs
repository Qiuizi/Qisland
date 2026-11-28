namespace Qisland.Core;

/// <summary>够用的语义化版本比较：1.0.0-preview &lt; 1.0.0-preview.2 &lt; 1.0.0 &lt; 1.0.1。</summary>
public static class SemVer
{
    /// <summary>形如 1.2.3 或 1.2.3-preview.4（可带前缀 v）。</summary>
    public static bool IsValid(string? v) => v != null && System.Text.RegularExpressions.Regex.IsMatch(v.Trim(), @"^v?\d{1,5}(\.\d{1,5}){0,3}(-[0-9A-Za-z.-]{1,32})?$");

    public static int Compare(string a, string b)
    {
        static (int[] core, string[] pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var core = (dash >= 0 ? v[..dash] : v).Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
            var pre = dash >= 0 ? v[(dash + 1)..].Split('.') : [];
            return (core, pre);
        }
        var (ca, pa) = Split(a); var (cb, pb) = Split(b);
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var x = i < ca.Length ? ca[i] : 0; var y = i < cb.Length ? cb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;   // 正式版比同号预览版新
        if (pb.Length == 0) return -1;
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var ia); var nb = int.TryParse(pb[i], out var ib);
            var c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
