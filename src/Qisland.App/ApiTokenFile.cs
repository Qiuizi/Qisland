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

    /// <summary>读不到、为空都返回 null（不启用 Token）。</summary>
    public static string? Read(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path, Encoding.UTF8).Trim();
            return token.Length > 0 ? token : null;
        }
        catch { return null; }
    }

    /// <summary>调用方（Hook）用：环境变量 QISLAND_TOKEN 优先，其次是 Token 文件。</summary>
    public static string? ForClient(string dataDir)
    {
        var env = Environment.GetEnvironmentVariable("QISLAND_TOKEN");
        return string.IsNullOrWhiteSpace(env) ? Read(dataDir) : env.Trim();
    }
}
