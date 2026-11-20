using System.Security.Cryptography;
using System.Text;

namespace Qisland.Core;

/// <summary>
/// 账号身份：平台 + 账号原始标识（学习通 uid、雨课堂用户 id）的 SHA-256 前 16 位十六进制。
/// 缓存和记录里只存这个，不存原始账号；没登录 / 不知道是谁 = 空串。
/// </summary>
public static class AccountId
{
    public static string Of(string platform, string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(platform + "\n" + raw.Trim())))[..16].ToLowerInvariant();
}
