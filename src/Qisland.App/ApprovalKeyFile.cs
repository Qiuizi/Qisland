using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 每次启动生成新的审批密钥。文件放在 %APPDATA%\Qisland\approval.key，并去掉继承 ACL，
/// 只给当前 Windows 用户完全控制；Hook 和 DSH 桥接用同一文件校验请求、响应。
/// </summary>
public static class ApprovalKeyFile
{
    public const string PathOverrideVariable = "QISLAND_APPROVAL_KEY_FILE";

    public static string PathFor(string dataDir)
    {
        var overridden = Environment.GetEnvironmentVariable(PathOverrideVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(dataDir, "approval.key")
            : Path.GetFullPath(overridden);
    }

    public static string? Read(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            var secret = File.ReadAllText(path, Encoding.UTF8).Trim();
            return ApprovalProtocol.IsValidSecret(secret) ? secret : null;
        }
        catch { return null; }
    }

    public static string? Rotate(string dataDir, out string? error)
    {
        error = null;
        try
        {
            var path = PathFor(dataDir);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var secret = ApprovalProtocol.NewSecret();
            var bytes = new UTF8Encoding(false).GetBytes(secret + "\n");
            using (var stream = FileSystemAclExtensions.Create(
                       new FileInfo(path),
                       FileMode.Create,
                       FileSystemRights.FullControl,
                       FileShare.None,
                       4096,
                       FileOptions.None,
                       SecurityForCurrentUser()))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            Protect(path);
            return secret;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static void Protect(string path)
        => new FileInfo(path).SetAccessControl(SecurityForCurrentUser());

    private static FileSecurity SecurityForCurrentUser()
    {
        var user = WindowsIdentity.GetCurrent().User
                   ?? throw new InvalidOperationException("拿不到当前用户 SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }
}
