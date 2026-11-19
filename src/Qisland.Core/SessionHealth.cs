using System.Globalization;

namespace Qisland.Core;

public enum AuthVerdict
{
    /// <summary>正常返回数据。</summary>
    Ok,
    /// <summary>网络 / 服务器临时问题、门户页、接口自己的业务错误：跟登录无关，过一会儿再试。</summary>
    Transient,
    /// <summary>说不准：雨课堂的 50000 是通用错误码，未登录时会出，接口偶发出错时也会出。</summary>
    Suspect,
    /// <summary>服务器明确说没登录（401/403 且是雨课堂自己的 JSON、已知的未登录错误码、或者下发了删除会话的 Cookie）。</summary>
    Rejected,
}

/// <summary>
/// 判断登录是不是真的失效了。以前只要任何一个接口回一次 401/403/50000 就立刻标记「登录过期」，
/// 而且标记后再也不查，必须重新扫码：校园网门户、半夜睡眠唤醒时的抽风都会把好好的登录判死。
/// 现在：任何一次正常返回清零；要连续两次「确认检查」都说没登录、且两次至少间隔 MinSpan，才算真的过期。
/// </summary>
public sealed class SessionHealth
{
    public TimeSpan MinSpan { get; init; } = TimeSpan.FromSeconds(90);
    public int Strikes { get; private set; }
    public DateTime? FirstStrikeAt { get; private set; }
    public bool Expired { get; private set; }

    /// <summary>记一次确认结果；返回此刻是否判定为过期。</summary>
    public bool Report(AuthVerdict v, DateTime now)
    {
        switch (v)
        {
            case AuthVerdict.Ok:
                Strikes = 0; FirstStrikeAt = null; Expired = false;
                break;
            case AuthVerdict.Rejected:
            case AuthVerdict.Suspect:
                if (Strikes == 0) FirstStrikeAt = now;
                Strikes++;
                if (Strikes >= 2 && FirstStrikeAt is { } f && now - f >= MinSpan) Expired = true;
                break;
            case AuthVerdict.Transient:
                break; // 不加也不减
        }
        return Expired;
    }

    public void Reset() { Strikes = 0; FirstStrikeAt = null; Expired = false; }

    /// <summary>
    /// 雨课堂接口返回分类。status：HTTP 状态码；isJson：响应体是不是 JSON（门户页、网关错误页是 HTML）；
    /// code：JSON 里的 code / errcode（没有就 null）；sessionDeleted：响应里下发了清空 sessionid 的 Set-Cookie。
    /// </summary>
    public static AuthVerdict ClassifyYuketang(int status, bool isJson, string? code, bool sessionDeleted = false)
    {
        if (sessionDeleted) return AuthVerdict.Rejected;
        if (status is 408 or 429 || status >= 500) return AuthVerdict.Transient;
        if (!isJson) return AuthVerdict.Transient;
        if (status is 401 or 403) return AuthVerdict.Rejected;
        if (status >= 300 && status != 304) return AuthVerdict.Transient;
        code = code?.Trim();
        if (string.IsNullOrEmpty(code) || code == "0") return AuthVerdict.Ok;
        if (code is "401002" or "10005" or "401" or "40100" or "40101") return AuthVerdict.Rejected;
        if (code == "50000") return AuthVerdict.Suspect;
        return AuthVerdict.Transient; // 其它业务错误（某门课没权限、参数不对……）跟登录无关
    }
}

/// <summary>
/// 一小罐 Cookie：从扫码登录拿到的全部 Cookie 起步，之后每次响应的 Set-Cookie 都合并进来（服务器续期、换 sessionid）。
/// 只存名字、值和过期时间；不打印值。
/// </summary>
public sealed class CookieSet
{
    public sealed record Entry(string Value, DateTimeOffset? Expires);
    private readonly Dictionary<string, Entry> _c = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Entry> All => _c;
    public string? this[string name] => _c.TryGetValue(name, out var e) ? e.Value : null;
    public DateTimeOffset? ExpiresOf(string name) => _c.TryGetValue(name, out var e) ? e.Expires : null;

    public void Set(string name, string value, DateTimeOffset? expires = null) => _c[name] = new Entry(value, expires);

    /// <summary>Cookie 请求头；过期的不带。</summary>
    public string Header(DateTimeOffset now) =>
        string.Join("; ", _c.Where(kv => kv.Value.Expires is not { } e || e > now).Select(kv => kv.Key + "=" + kv.Value.Value));

    /// <summary>合并一个 Set-Cookie 头。返回 (changed：值或过期时间有变化, deleted：服务器要删掉它)。</summary>
    public (bool changed, bool deleted) Apply(string setCookie, DateTimeOffset now)
    {
        var parts = setCookie.Split(';');
        var nv = parts[0];
        var eq = nv.IndexOf('=');
        if (eq <= 0) return (false, false);
        var name = nv[..eq].Trim();
        var value = nv[(eq + 1)..].Trim().Trim('"');
        DateTimeOffset? expires = null;
        foreach (var raw in parts.Skip(1))
        {
            var kv = raw.Split('=', 2);
            var k = kv[0].Trim().ToLowerInvariant();
            var v = kv.Length > 1 ? kv[1].Trim() : "";
            if (k == "max-age" && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var sec))
                expires = sec <= 0 ? now.AddSeconds(-1) : now.AddSeconds(sec);   // Max-Age 优先于 Expires
            else if (k == "expires" && !parts.Any(x => x.TrimStart().StartsWith("max-age", StringComparison.OrdinalIgnoreCase))
                     && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ex))
                expires = ex;
        }
        var deleted = value.Length == 0 || value is "deleted" or "\"\"" || expires is { } e0 && e0 <= now;
        if (deleted)
        {
            var had = _c.Remove(name);
            return (had, true);
        }
        if (_c.TryGetValue(name, out var old) && old.Value == value
            && (old.Expires == expires || (old.Expires is { } a && expires is { } b && Math.Abs((a - b).TotalSeconds) < 60)))
            return (false, false);
        _c[name] = new Entry(value, expires);
        return (true, false);
    }
}
