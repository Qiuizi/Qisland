using System.IO;
using System.Text;

namespace Qisland.App;

/// <summary>
/// 本机推送接口的 Token（docs/push-api.md）：写在 %APPDATA%\Qisland\api-token.txt 里就启用，重启 Qisland 生效。
/// 设了以后普通 HTTP 请求都要带 X-Qisland-Token（或旧名 X-WinIslands-Token）；Hook、推送脚本和 SDK 示例会自己读这个文件。
/// 测试和多实例可用 QISLAND_API_TOKEN_FILE 指向别的文件。
/// </summary>
public static class ApiTokenFile
{
    public const string FileName = "api-token.txt";
    public const string PathOverrideVariable = "QISLAND_API_TOKEN_FILE";

    public static string PathFor(string dataDir)
    {
        var overridden = Environment.GetEnvironmentVariable(PathOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden) ? Path.Combine(dataDir, FileName) : Path.GetFullPath(overridden);
    }

    /// <summary>只有 Token 文件有效时才返回 Token；没配置、读不了、内容无效都返回 null（调用方因此不会带 Token，服务端会按状态处理）。</summary>
    public static string? Read(string dataDir) => Load(dataDir) is { State: ApiTokenState.Enabled } c ? c.Token : null;

    /// <summary>
    /// 区分三种状态：
    /// 文件不存在 → NotConfigured（不校验，和以前一样）；
    /// 文件有效 → Enabled；
    /// 文件存在但读不了（被占用、没权限、路径是目录）、为空、或内容不是一段不含空白的可见 ASCII → Invalid。
    /// Invalid 时服务端拒绝所有普通读写（健康检查除外），绝不退化成「不校验」。错误信息里只有路径和原因，不含文件内容。
    /// </summary>
    public static ApiTokenConfig Load(string dataDir)
    {
        string path;
        try { path = PathFor(dataDir); }
        catch (Exception ex) { return ApiTokenConfig.Invalid($"Token 文件路径无效（{ex.GetType().Name}）"); }
        if (Directory.Exists(path)) return ApiTokenConfig.Invalid($"{path} 是一个目录，不是 Token 文件");
        if (!File.Exists(path)) return ApiTokenConfig.None;
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (Exception ex) { return ApiTokenConfig.Invalid($"读不了 Token 文件 {path}（{ex.GetType().Name}）"); }
        var token = text.Trim();
        if (token.Length == 0) return ApiTokenConfig.Invalid($"Token 文件 {path} 是空的");
        if (!token.All(c => c > ' ' && c < '\x7f'))
            return ApiTokenConfig.Invalid($"Token 文件 {path} 的内容无效：只能是一段不含空白的可见 ASCII 字符");
        return new ApiTokenConfig(ApiTokenState.Enabled, token, null);
    }

    /// <summary>调用方（Hook）用：环境变量 QISLAND_TOKEN 优先，其次是 Token 文件。</summary>
    public static string? ForClient(string dataDir)
    {
        var env = Environment.GetEnvironmentVariable("QISLAND_TOKEN");
        return string.IsNullOrWhiteSpace(env) ? Read(dataDir) : env.Trim();
    }
}

public enum ApiTokenState { NotConfigured, Enabled, Invalid }

public sealed record ApiTokenConfig(ApiTokenState State, string? Token, string? Error)
{
    public static ApiTokenConfig None { get; } = new(ApiTokenState.NotConfigured, null, null);
    public static ApiTokenConfig Invalid(string error) => new(ApiTokenState.Invalid, null, error);
}
