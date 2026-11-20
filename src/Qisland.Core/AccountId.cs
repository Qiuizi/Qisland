using System.Security.Cryptography;
using System.Text;

namespace Qisland.Core;

/// <summary>
/// 账号身份：平台 + 账号原始标识（学习通 uid、雨课堂用户 id）的 SHA-256 前 16 位十六进制。
/// 缓存和记录里只存这个，不存原始账号；没登录 / 不知道是谁 = 空串（空串永远认领不到缓存）。
/// </summary>
public static class AccountId
{
    public static string Of(string platform, string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(platform + "\n" + raw.Trim())))[..16].ToLowerInvariant();

    /// <summary>
    /// 学习通账号身份：只认 UID（登录 Cookie 的 _uid / UID）。昵称会重名、会改，不能当身份：
    /// 没有 UID 就是未知（空串）——未知身份登录时撤掉旧作业、清缓存，未知身份写的缓存也不会被认领。
    /// </summary>
    public static string Xuexitong(string? uid, string? nickname) => Of("学习通", uid);

    /// <summary>雨课堂账号身份：学校域名 + 用户 id；拿不到用户 id 就是未知（不用用户名）。</summary>
    public static string Yuketang(string host, string? userId, string? name) =>
        string.IsNullOrWhiteSpace(userId) ? "" : Of("雨课堂", host + "|" + userId.Trim());
}
