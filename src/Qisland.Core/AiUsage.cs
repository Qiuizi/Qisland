using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Qisland.Core;

/// <summary>
/// 本机 AI 编程工具的额度：Claude Code / Cursor / Gemini CLI / Kimi Code。
/// 这里只放纯解析（可在 Linux 上单测）：登录文件里取令牌和过期时间、各家用量接口的返回解析。
/// 这些接口都是各家自己客户端在用、但没有公开文档的接口，随时可能变；解析一律宽松，读不到就是「读不到」，绝不估算。
/// 令牌只在内存里用一次，不落盘、不写日志；也从不刷新令牌（刷新会轮换令牌，把正在用的客户端踢下线）。
/// </summary>
public static class AiUsage
{
    public sealed record Window(string Label, double UsedPercent, DateTime? ResetsAt);
    public sealed record Snapshot(string Tool, IReadOnlyList<Window> Windows, string Plan = "");

    /// <summary>登录文件里读出来的令牌。ExpiresAt 为 null 表示文件里没写过期时间。</summary>
    public sealed record Credential(string Token, DateTime? ExpiresAt, string Extra = "")
    {
        public bool Expired(DateTime nowUtc) => ExpiresAt is { } e && e <= nowUtc.AddSeconds(60);
        public override string ToString() => "Credential(***)";   // 防止哪天被谁拼进日志
    }

    public static readonly string[] Tools = ["claude", "cursor", "gemini", "kimi"];

    public static string Name(string tool) => tool switch
    {
        "claude" => "Claude Code",
        "cursor" => "Cursor",
        "gemini" => "Gemini CLI",
        "kimi" => "Kimi Code",
        "codex" => "Codex",
        "doubao" => "豆包",
        _ => tool,
    };

    public static string Short(string tool) => tool switch
    {
        "claude" => "Claude",
        "gemini" => "Gemini",
        "kimi" => "Kimi",
        _ => Name(tool),
    };

    // ───────────── 通用小工具 ─────────────

    internal static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return double.IsFinite(d) ? d : null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p)) return p;
        return null;
    }

    internal static DateTime? Time(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto.UtcDateTime;
            if (long.TryParse(s, out var l)) return Epoch(l);
            return null;
        }
        return Num(n) is { } d ? Epoch((long)d) : null;
    }

    /// <summary>秒或毫秒的 Unix 时间（大于 1e11 当毫秒）。</summary>
    internal static DateTime? Epoch(long v)
    {
        if (v <= 0) return null;
        try { return v > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(v).UtcDateTime : DateTimeOffset.FromUnixTimeSeconds(v).UtcDateTime; }
        catch { return null; }
    }

    private static JsonObject? Obj(string json)
    {
        try { return JsonNode.Parse(json) as JsonObject; } catch { return null; }
    }

    private static Window? W(string label, double? used, DateTime? reset) =>
        used is { } u ? new Window(label, Math.Clamp(u, 0, 100), reset) : null;

    // ───────────── Claude Code ─────────────

    /// <summary>~/.claude/.credentials.json：{"claudeAiOauth":{"accessToken","expiresAt"(毫秒),"subscriptionType"}}。</summary>
    public static Credential? ClaudeCredential(string json)
    {
        var o = Obj(json)?["claudeAiOauth"] as JsonObject;
        if (o?["accessToken"] is not JsonValue t || !t.TryGetValue<string>(out var tok) || string.IsNullOrWhiteSpace(tok)) return null;
        var exp = Num(o["expiresAt"]) is { } e ? Epoch((long)e) : null;
        var plan = o["subscriptionType"] is JsonValue p && p.TryGetValue<string>(out var ps) ? ps : "";
        return new Credential(tok.Trim(), exp, plan);
    }

    /// <summary>GET https://api.anthropic.com/api/oauth/usage：five_hour / seven_day（utilization 0–100，resets_at ISO）。</summary>
    public static Snapshot? ParseClaude(string json, string plan = "")
    {
        var o = Obj(json);
        if (o == null) return null;
        var list = new List<Window>();
        foreach (var (key, label) in new[] { ("five_hour", "5 小时"), ("seven_day", "本周"), ("seven_day_opus", "本周 Opus") })
        {
            if (o[key] is not JsonObject w) continue;
            if (W(label, Num(w["utilization"]), Time(w["resets_at"])) is { } win) list.Add(win);
        }
        return list.Count > 0 ? new Snapshot("claude", list, plan) : null;
    }

    // ───────────── Cursor ─────────────

    /// <summary>JWT 中段的 sub（形如 github|user_01…）。不验签，只拿来拼 Cursor 网页用的会话 Cookie。</summary>
    public static string? JwtSub(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var s = parts[1].Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(s));
            return (JsonNode.Parse(payload) as JsonObject)?["sub"] is JsonValue v && v.TryGetValue<string>(out var sub) && sub.Length > 0 ? sub : null;
        }
        catch { return null; }
    }

    /// <summary>JWT 的 exp（秒）。</summary>
    public static DateTime? JwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var s = parts[1].Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            var o = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(s))) as JsonObject;
            return Num(o?["exp"]) is { } e ? Epoch((long)e) : null;
        }
        catch { return null; }
    }

    /// <summary>Cursor 网页的会话 Cookie 值：「sub::jwt」，:: 要 URL 编码。</summary>
    public static string? CursorCookie(string jwt) => JwtSub(jwt) is { } sub ? Uri.EscapeDataString(sub) + "%3A%3A" + jwt : null;

    /// <summary>
    /// GET https://cursor.com/api/usage-summary：individualUsage.plan 里有 totalPercentUsed / autoPercentUsed / apiPercentUsed（0–100），
    /// 老格式只有 used / limit（美分）。周期结束时间 billingCycleEnd。
    /// </summary>
    public static Snapshot? ParseCursor(string json)
    {
        var o = Obj(json);
        if (o == null) return null;
        var reset = Time(o["billingCycleEnd"]);
        var plan = o["membershipType"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : "";
        var p = o["individualUsage"]?["plan"] as JsonObject;
        var list = new List<Window>();
        if (p != null)
        {
            var total = Num(p["totalPercentUsed"]);
            if (total == null && Num(p["limit"]) is { } lim && lim > 0 && Num(p["used"]) is { } used) total = used / lim * 100;
            if (W("本月", total, reset) is { } t) list.Add(t);
            var auto = Num(p["autoPercentUsed"]); var api = Num(p["apiPercentUsed"]);
            // 两池拆开时（Auto 和指定模型各有额度）补两条，只有其中一池在用时不重复显示
            if (auto is { } a && api is { } b && Math.Abs(a - b) > 0.5)
            {
                if (W("Auto", a, reset) is { } wa) list.Add(wa);
                if (W("指定模型", b, reset) is { } wb) list.Add(wb);
            }
        }
        return list.Count > 0 ? new Snapshot("cursor", list, plan) : null;
    }

    // ───────────── Gemini CLI ─────────────

    /// <summary>~/.gemini/oauth_creds.json：{"access_token","expiry_date"(毫秒)}。</summary>
    public static Credential? GeminiCredential(string json)
    {
        var o = Obj(json);
        if (o?["access_token"] is not JsonValue t || !t.TryGetValue<string>(out var tok) || string.IsNullOrWhiteSpace(tok)) return null;
        var exp = Num(o["expiry_date"]) is { } e ? Epoch((long)e) : null;
        return new Credential(tok.Trim(), exp);
    }

    /// <summary>loadCodeAssist 返回里的 cloudaicompanionProject（字符串或 {id}）和当前档位。</summary>
    public static (string? Project, string Tier) ParseGeminiLoad(string json)
    {
        var o = Obj(json);
        if (o == null) return (null, "");
        string? project = o["cloudaicompanionProject"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonObject po when po["id"] is JsonValue iv && iv.TryGetValue<string>(out var id) => id,
            _ => null,
        };
        var tier = o["currentTier"]?["name"] is JsonValue tv && tv.TryGetValue<string>(out var tn) ? tn : "";
        return (string.IsNullOrWhiteSpace(project) ? null : project, tier);
    }

    /// <summary>
    /// retrieveUserQuota：buckets[]{modelId, remainingFraction(0–1), resetTime}。同一模型取剩得最少的一桶；
    /// 显示 Pro 和 Flash 两档里各自最紧的那个（模型多了岛上放不下）。
    /// </summary>
    public static Snapshot? ParseGemini(string json, string tier = "")
    {
        var o = Obj(json);
        if (o?["buckets"] is not JsonArray arr) return null;
        var best = new Dictionary<string, (double Used, DateTime? Reset)>();
        foreach (var b in arr)
        {
            if (b is not JsonObject bo || Num(bo["remainingFraction"]) is not { } frac) continue;
            var model = bo["modelId"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "";
            var group = model.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "Flash" : model.Contains("pro", StringComparison.OrdinalIgnoreCase) ? "Pro" : "其他";
            var used = Math.Clamp((1 - frac) * 100, 0, 100);
            var reset = Time(bo["resetTime"]);
            if (!best.TryGetValue(group, out var cur) || used > cur.Used) best[group] = (used, reset);
        }
        var list = new List<Window>();
        foreach (var g in new[] { "Pro", "Flash", "其他" })
            if (best.TryGetValue(g, out var v)) list.Add(new Window(g == "其他" ? "今日" : g + " 今日", v.Used, v.Reset));
        if (list.Count > 2) list.RemoveAt(2);
        return list.Count > 0 ? new Snapshot("gemini", list, tier) : null;
    }

    // ───────────── Kimi Code ─────────────

    /// <summary>~/.kimi-code/credentials/kimi-code.json：{"access_token","expires_at"(秒)}。</summary>
    public static Credential? KimiCredential(string json)
    {
        var o = Obj(json);
        if (o?["access_token"] is not JsonValue t || !t.TryGetValue<string>(out var tok) || string.IsNullOrWhiteSpace(tok)) return null;
        var exp = Num(o["expires_at"]) is { } e ? Epoch((long)e) : null;
        return new Credential(tok.Trim(), exp);
    }

    /// <summary>GET https://api.kimi.com/coding/v1/usages：usage（本周）+ limits[]（5 小时窗口），数字是字符串。</summary>
    public static Snapshot? ParseKimi(string json)
    {
        var o = Obj(json);
        if (o == null) return null;
        static double? Pct(JsonNode? d)
        {
            if (d is not JsonObject x) return null;
            var lim = Num(x["limit"]);
            if (lim is not > 0) return null;
            if (Num(x["used"]) is { } u) return u / lim.Value * 100;
            if (Num(x["remaining"]) is { } r) return (lim.Value - r) / lim.Value * 100;
            return null;
        }
        var list = new List<Window>();
        foreach (var l in o["limits"] as JsonArray ?? [])
        {
            if (l is not JsonObject lo) continue;
            var dur = Num(lo["window"]?["duration"]);
            var unit = lo["window"]?["timeUnit"] is JsonValue uv && uv.TryGetValue<string>(out var us) ? us : "";
            var minutes = unit.Contains("HOUR") ? dur * 60 : unit.Contains("DAY") ? dur * 1440 : dur;
            var label = minutes is { } mm && mm > 0 ? (mm % 60 == 0 ? $"{mm / 60:0} 小时" : $"{mm:0} 分钟") : "短时";
            if (W(label, Pct(lo["detail"]), Time(lo["detail"]?["resetTime"])) is { } w) { list.Add(w); break; }
        }
        if (W("本周", Pct(o["usage"]), Time(o["usage"]?["resetTime"])) is { } week) list.Add(week);
        var level = o["user"]?["membership"]?["level"] is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : "";
        return list.Count > 0 ? new Snapshot("kimi", list, level) : null;
    }
}

/// <summary>
/// 额度阈值提醒（70% / 90%）防抖：同一个工具的同一个窗口，在同一个重置周期里每档只提醒一次；
/// 重置后（重置时间变了，或用量掉回阈值以下 10 个点）才重新计。状态可序列化，重启后不重复轰炸。
/// </summary>
public sealed class QuotaAlerts
{
    public const double Warn = 70, Critical = 90;

    // 键：工具|窗口 → (已提醒到的档位, 那时的重置时间)
    private readonly Dictionary<string, (int Level, DateTime? Reset)> _state = new(StringComparer.Ordinal);

    /// <summary>返回这次该提醒的档位（70 / 90），不该提醒返回 0。</summary>
    public int Check(string tool, string window, double usedPercent, DateTime? resetsAt)
    {
        var key = tool + "|" + window;
        var level = usedPercent >= Critical ? 90 : usedPercent >= Warn ? 70 : 0;
        if (_state.TryGetValue(key, out var s))
        {
            var rolled = s.Reset is { } a && resetsAt is { } b && Math.Abs((b - a).TotalMinutes) > 30;
            var dropped = s.Level > 0 && usedPercent < s.Level - 10;
            if (rolled || dropped) { _state.Remove(key); s = default; }
        }
        if (level == 0) return 0;
        if (s.Level >= level) return 0;
        _state[key] = (level, resetsAt);
        return level;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in _state.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.Append(k).Append('\t').Append(v.Level.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(v.Reset is { } r ? r.Ticks.ToString(CultureInfo.InvariantCulture) : "").Append('\n');   // 只拿来比差值，存原始刻度不做时区换算
        return sb.ToString();
    }

    public void Load(string? text)
    {
        _state.Clear();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length < 3 || !int.TryParse(p[1], out var lv) || lv is not (70 or 90)) continue;
            DateTime? r = long.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 && t < DateTime.MaxValue.Ticks ? new DateTime(t) : null;
            _state[p[0]] = (lv, r);
        }
    }
}
