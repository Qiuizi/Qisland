using System.Text.RegularExpressions;

namespace Qisland.Core;

/// <summary>从通知标题 / 预览里认出验证码（4–8 位数字）。只在内存里用，不落盘。</summary>
public static partial class Otp
{
    [GeneratedRegex(@"验证码|校验码|动态码|动态密码|确认码|安全码|驗證碼|verification code|security code|one[- ]time|\bOTP\b|\bcode\b", RegexOptions.IgnoreCase)]
    private static partial Regex Keyword();

    // 前后不能紧挨着别的数字、字母或小数点 / 横杠（排除金额、日期、尾号、订单号片段）
    [GeneratedRegex(@"(?<![0-9A-Za-z.\-:/])([0-9]{4,8})(?![0-9A-Za-z.\-:/])")]
    private static partial Regex Digits();

    /// <summary>有验证码关键字时，返回离关键字最近的 4–8 位数字；没有返回 null。</summary>
    public static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2000) return null;
        var kws = Keyword().Matches(text);
        if (kws.Count == 0) return null;
        string? best = null;
        var bestDist = int.MaxValue;
        foreach (Match m in Digits().Matches(text))
        {
            var v = m.Groups[1].Value;
            foreach (Match k in kws)
            {
                // 验证码一般写在关键字后面；写在前面的（「123456 是你的验证码」）也认，但同等距离时让给后面的
                var d = m.Index >= k.Index ? m.Index - (k.Index + k.Length) : k.Index - (m.Index + m.Length) + 12;
                if (d < 0) d = 0;
                if (d < bestDist) { bestDist = d; best = v; }
            }
        }
        return bestDist <= 40 ? best : null;
    }
}
