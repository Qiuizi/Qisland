using System.Text.Json;

namespace Qisland.Core;

/// <summary>退出登录的结果：凭据删掉了 / 删不掉但写好了退出标记（会继续重试删除）/ 两样都没做成（重启后可能恢复登录）。</summary>
public enum LogoutOutcome { Removed, Pending, Failed }

/// <summary>读凭据前的判定：可以用 / 已退出 / 现在判断不了（退出标记读不到）。</summary>
public enum CredentialCheck { Ok, Revoked, Unknown }

/// <summary>
/// 保存登录凭据的文件（学习通 xuexitong.bin、雨课堂 yuketang.bin）的退出登录处理。
/// 每次扫码登录生成一个随机的凭据 id，写在加密的凭据里；退出时凭据文件被占用删不掉，就在旁边写退出标记
/// （.loggedout，内容是被撤销的凭据 id 列表），读凭据前看它的 id 在不在列表里——不比时间，系统时间被调过也不受影响。
/// 标记读不到（被占用）时判定为「未知」：不删任何文件、先不登录，稍后再判。
/// 4.0.4 写的标记只有时间，按「撤销所有没有 id 的旧凭据」处理。
/// </summary>
public sealed class LoginFile(string path, Func<string, bool>? tryDelete = null, Func<string, string, bool>? tryWrite = null, Func<DateTimeOffset>? clock = null, Func<string, string>? read = null)
{
    public string Path => path;
    public string TombPath => path + ".loggedout";

    /// <summary>旧版本存的凭据没有凭据 id，按这个 id 记。</summary>
    public const string LegacyId = "legacy";

    /// <summary>新登录用的凭据 id。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    public long NowMs => (clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();

    private sealed record Marker(int V, List<string>? Revoked);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    /// <summary>退出登录：删凭据；删不掉就把这些凭据 id 追加进退出标记。</summary>
    public LogoutOutcome Logout(IEnumerable<string> credentialIds)
    {
        if (Delete(path)) { Delete(TombPath); return LogoutOutcome.Removed; }
        var ids = new List<string>();
        var state = ReadMarker(out var old);
        if (state == MarkerState.Ids) ids.AddRange(old!);
        else if (state == MarkerState.Legacy) ids.Add(LegacyId);          // 4.0.4 的标记撤销的是没有 id 的旧凭据，保留这层意思
        foreach (var id in credentialIds) if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
        return Write(TombPath, JsonSerializer.Serialize(new Marker(2, ids), Json)) ? LogoutOutcome.Pending : LogoutOutcome.Failed;
    }

    /// <summary>读凭据前：这份凭据（id 为 null 或空 = 旧版本凭据）能不能用。</summary>
    public CredentialCheck Check(string? credentialId)
    {
        var id = string.IsNullOrEmpty(credentialId) ? LegacyId : credentialId;
        return ReadMarker(out var ids) switch
        {
            MarkerState.None => CredentialCheck.Ok,
            MarkerState.Ids => ids!.Contains(id) ? CredentialCheck.Revoked : CredentialCheck.Ok,
            MarkerState.Legacy => id == LegacyId ? CredentialCheck.Revoked : CredentialCheck.Ok,
            _ => CredentialCheck.Unknown,
        };
    }

    private enum MarkerState { None, Ids, Legacy, Unreadable }

    private MarkerState ReadMarker(out List<string>? ids)
    {
        ids = null;
        string text;
        try
        {
            if (!File.Exists(TombPath)) return MarkerState.None;
            text = (read ?? File.ReadAllText)(TombPath).Trim();
        }
        catch (FileNotFoundException) { return MarkerState.None; }
        catch { return MarkerState.Unreadable; }
        if (long.TryParse(text, out _)) return MarkerState.Legacy;          // 4.0.4：只记了退出时间
        try
        {
            ids = JsonSerializer.Deserialize<Marker>(text, Json)?.Revoked;
            if (ids != null) return MarkerState.Ids;
        }
        catch (JsonException) { }
        return MarkerState.Unreadable;                                       // 内容坏了：判断不了，按未知处理
    }

    /// <summary>重试清理 / 确认撤销后清理：删凭据，再删标记。返回是否都删干净了。只在状态明确时调用。</summary>
    public bool Cleanup() => Delete(path) && Delete(TombPath);

    /// <summary>新登录写好凭据之后：旧的退出标记没用了，删掉；删不掉就改写成空列表（新凭据 id 本来也不在里面）。</summary>
    public void Saved()
    {
        if (!Delete(TombPath)) Write(TombPath, JsonSerializer.Serialize(new Marker(2, []), Json));
    }

    private bool Delete(string p)
    {
        try
        {
            if (tryDelete != null) return tryDelete(p);
            if (File.Exists(p)) File.Delete(p);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool Write(string p, string text)
    {
        try
        {
            if (tryWrite != null) return tryWrite(p, text);
            return AtomicFile.TryWrite(p, System.Text.Encoding.UTF8.GetBytes(text), out _);
        }
        catch { return false; }
    }
}
