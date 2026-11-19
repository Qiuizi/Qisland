using System.Globalization;
using System.Text.Json.Nodes;

namespace Qisland.Core;

/// <summary>
/// 豆包网页会员额度（非官方接口，可能随时失效）：只读解析，思路参考 MIT 项目岛仔（Daozai）的 doubao_quota，C# 重写。
/// 接口：POST https://www.doubao.com/alice/commerce/sale/subscription/quota/summary/ ，body {"product_line":"membership"}。
/// </summary>
public static class DoubaoQuotaParser
{
    public const string Endpoint = "https://www.doubao.com/alice/commerce/sale/subscription/quota/summary/";
    public const string Body = "{\"product_line\":\"membership\"}";
    public const string LoginError = "豆包登录失效了，需要重新连接";
    public const string FormatError = "豆包额度接口变了，暂时读不了";

    public sealed record Window(string Label, double UsedPercent, DateTime? ResetsAt, long? Seconds);
    public sealed record Snapshot(string Plan, IReadOnlyList<Window> Windows);

    public sealed class QuotaException(string message, bool login) : Exception(message) { public bool Login { get; } = login; }

    private static double? Amount(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) && d >= 0 ? d : null;
        if (v.TryGetValue<long>(out var l)) return l >= 0 ? l : null;
        var s = v.ToString().Replace(",", "").Trim();
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p >= 0 ? p : null;
    }

    private static long? Int(JsonNode? n) => Amount(n) is { } d ? (long)d : null;

    private static double? Used(JsonNode limit)
    {
        if (limit["usage_exhausted"] is JsonValue ex && ex.TryGetValue<bool>(out var b) && b) return 100;
        if (Amount(limit["total_amount"]) == 0) return null;
        if (Amount(limit["used_percent"]) is { } p) return Math.Clamp(p, 0, 100);
        if (Amount(limit["used_amount"]) is { } u && Amount(limit["total_amount"]) is { } t && t > 0) return Math.Clamp(u / t * 100, 0, 100);
        return null;
    }

    public static Snapshot Parse(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch { throw new QuotaException(FormatError, false); }
        if (root is not JsonObject o) throw new QuotaException(FormatError, false);
        if (o["code"] is { } codeNode)
        {
            var code = Int(codeNode);
            if (code == 710012001) throw new QuotaException(LoginError, true);
            if (code is null) throw new QuotaException(FormatError, false);
            if (code != 0) throw new QuotaException($"豆包额度服务返回错误（{code}）", false);
        }
        if (o["data"] is not JsonObject data) throw new QuotaException(LoginError, true);
        var windows = new List<Window>();
        foreach (var section in new[] { "window_limit_section", "incentive_quota_section" })
            foreach (var g in data[section]?["window_limit_groups"] as JsonArray ?? [])
                foreach (var lim in g?["window_limits"] as JsonArray ?? [])
                {
                    if (lim == null || Used(lim) is not { } used) continue;
                    long? secs = Int(lim["window_type"]) switch { 1 => 18000, 2 => 604800, 3 => null, _ => -1 };
                    if (secs == -1) continue;
                    var label = secs switch { 18000 => "5 小时", 604800 => "本周", _ => "其他" };
                    if (section == "incentive_quota_section") label = "活动 · " + label;
                    if (g?["feature_group_name"]?.ToString() is { Length: > 0 } fg) label = fg.Trim() + " · " + label;
                    DateTime? reset = null;
                    if (secs != null && Int(lim["end_time"]) is { } end and > 0)
                        reset = DateTimeOffset.FromUnixTimeSeconds(end >= 100_000_000_000 ? end / 1000 : end).LocalDateTime;
                    windows.Add(new Window(label, used, reset, secs));
                }
        if (windows.Count == 0) throw new QuotaException("这个豆包账号没有可读的订阅额度", false);
        // 同一周期多个权益池时，环上显示最紧的那个
        var primary = windows.Where(w => w.Seconds != null && !w.Label.Contains("活动"))
            .GroupBy(w => w.Seconds).Select(gr => gr.MaxBy(w => w.UsedPercent)!).OrderBy(w => w.Seconds).ToList();
        if (primary.Count == 0) primary = windows.Take(2).ToList();
        var plan = data["current_subscription"]?["display"]?["short_name"]?.ToString()
                   ?? data["current_subscription"]?["display"]?["product_name"]?.ToString() ?? "";
        plan = plan.Trim();
        if (plan.StartsWith("豆包")) plan = plan[2..].Trim();
        return new Snapshot(plan, primary);
    }

    /// <summary>用户粘贴的 Cookie 请求头：去掉「Cookie:」前缀，要求有且只有一个 32 位十六进制的 sessionid。</summary>
    public static string? NormalizeCookie(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase)) s = s[7..].Trim();
        if (s.Length == 0 || s.Length > 32768 || s.Any(c => c < 32 || c >= 127)) return null;
        var sessions = s.Split(';').Select(p => p.Trim().Split('=', 2)).Where(p => p.Length == 2 && p[0] == "sessionid").Select(p => p[1]).ToList();
        if (sessions.Count != 1 || sessions[0].Length != 32 || !sessions[0].All(Uri.IsHexDigit)) return null;
        return s;
    }
}
