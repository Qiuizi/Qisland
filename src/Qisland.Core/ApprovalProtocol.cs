using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qisland.Core;

public enum ApprovalAnswer
{
    Fallback,
    Allow,
    Deny,
}

/// <summary>
/// Qisland 审批接口的签名协议。请求、响应都只认当前用户密钥签出的 HMAC；
/// 响应还绑定请求 id 和 nonce，任何缺项都不等于允许。
/// </summary>
public static class ApprovalProtocol
{
    public const string Version = "1";
    public const string CanonicalPrefix = "qisland-approval-v1";
    public const string VersionHeader = "X-Qisland-Approval-Version";
    public const string RequestIdHeader = "X-Qisland-Request-Id";
    public const string NonceHeader = "X-Qisland-Nonce";
    public const string SignatureHeader = "X-Qisland-Signature";
    public const int MaxResponseBytes = 64 * 1024;

    public static string ApprovalPath(string source)
        => "/v1/approval/" + Uri.EscapeDataString(AgentEventParser.NormalizeSource(source));

    public static string NewSecret()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string NewToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsValidSecret(string? secret)
        => secret is { Length: 64 } && secret.All(Uri.IsHexDigit);

    public static string RequestSignature(string secret, string path, string requestId, string nonce, string body)
        => Hmac(secret, $"{CanonicalPrefix}\nPOST\n{path}\n{requestId}\n{nonce}\n{body}");

    public static string ResponseSignature(string secret, string requestId, string nonce, string decision)
        => Hmac(secret, $"{CanonicalPrefix}\n{requestId}\n{nonce}\n{decision}");

    public static bool VerifyRequest(
        string? secret,
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        string body,
        out string requestId,
        out string nonce)
    {
        requestId = "";
        nonce = "";
        if (!IsValidSecret(secret) || method != "POST") return false;
        if (!headers.TryGetValue(VersionHeader, out var version) || version != Version) return false;
        if (!headers.TryGetValue(RequestIdHeader, out var id) || !IsToken(id)) return false;
        if (!headers.TryGetValue(NonceHeader, out var requestNonce) || !IsToken(requestNonce)) return false;
        if (!headers.TryGetValue(SignatureHeader, out var signature) || !IsSignature(signature)) return false;
        if (!FixedHexEquals(signature, RequestSignature(secret!, path, id, requestNonce, body))) return false;
        requestId = id;
        nonce = requestNonce;
        return true;
    }

    public static string BuildResponse(string secret, string requestId, string nonce, string decision)
    {
        if (decision is not ("allow" or "deny")) throw new ArgumentException("决定只能是 allow 或 deny", nameof(decision));
        if (!IsToken(requestId) || !IsToken(nonce)) throw new ArgumentException("请求 id 或 nonce 不合法");
        var response = new ApprovalResponseDocument(
            int.Parse(Version),
            requestId,
            nonce,
            decision,
            ResponseSignature(secret, requestId, nonce, decision));
        return JsonSerializer.Serialize(response);
    }

    public static ApprovalAnswer ValidateResponse(
        string? secret,
        int statusCode,
        string? body,
        string requestId,
        string nonce)
    {
        if (!IsValidSecret(secret) || statusCode != 200 || string.IsNullOrWhiteSpace(body)) return ApprovalAnswer.Fallback;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5) return ApprovalAnswer.Fallback;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1)
                return ApprovalAnswer.Fallback;
            if (!root.TryGetProperty("request_id", out var id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() != requestId)
                return ApprovalAnswer.Fallback;
            if (!root.TryGetProperty("nonce", out var returnedNonce) || returnedNonce.ValueKind != JsonValueKind.String ||
                returnedNonce.GetString() != nonce)
                return ApprovalAnswer.Fallback;
            if (!root.TryGetProperty("decision", out var decisionElement) || decisionElement.ValueKind != JsonValueKind.String)
                return ApprovalAnswer.Fallback;
            var decision = decisionElement.GetString();
            if (decision is not ("allow" or "deny")) return ApprovalAnswer.Fallback;
            if (!root.TryGetProperty("signature", out var signatureElement) ||
                signatureElement.ValueKind != JsonValueKind.String)
                return ApprovalAnswer.Fallback;
            var signature = signatureElement.GetString() ?? "";
            if (!IsSignature(signature) ||
                !FixedHexEquals(signature, ResponseSignature(secret!, requestId, nonce, decision)))
                return ApprovalAnswer.Fallback;
            return decision == "allow" ? ApprovalAnswer.Allow : ApprovalAnswer.Deny;
        }
        catch (JsonException) { return ApprovalAnswer.Fallback; }
        catch (InvalidOperationException) { return ApprovalAnswer.Fallback; }
        catch (FormatException) { return ApprovalAnswer.Fallback; }
    }

    private static string Hmac(string secret, string text)
    {
        using var mac = new HMACSHA256(Convert.FromHexString(secret));
        return Convert.ToHexString(mac.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static bool FixedHexEquals(string? left, string right)
    {
        if (!IsSignature(left) || !IsSignature(right)) return false;
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left!),
                Convert.FromHexString(right));
        }
        catch (FormatException) { return false; }
    }

    private static bool IsToken(string? value)
        => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSignature(string? value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private sealed record ApprovalResponseDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("v")] int V,
        [property: System.Text.Json.Serialization.JsonPropertyName("request_id")] string RequestId,
        [property: System.Text.Json.Serialization.JsonPropertyName("nonce")] string Nonce,
        [property: System.Text.Json.Serialization.JsonPropertyName("decision")] string Decision,
        [property: System.Text.Json.Serialization.JsonPropertyName("signature")] string Signature);
}

/// <summary>Hook / 桥接使用的严格客户端：只要有一项不满足就返回 Fallback。</summary>
public static class ApprovalClient
{
    public static async Task<ApprovalAnswer> AskAsync(
        string? secret,
        string source,
        int port,
        string body,
        TimeSpan timeout,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        if (!ApprovalProtocol.IsValidSecret(secret) || timeout <= TimeSpan.Zero)
            return ApprovalAnswer.Fallback;
        var path = ApprovalProtocol.ApprovalPath(source);
        var requestId = ApprovalProtocol.NewToken();
        var nonce = ApprovalProtocol.NewToken();
        var signature = ApprovalProtocol.RequestSignature(secret!, path, requestId, nonce, body);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var ownHandler = handler == null
            ? new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(1),
            }
            : null;
        using var client = handler == null
            ? new HttpClient(ownHandler!, disposeHandler: false)
            : new HttpClient(handler, disposeHandler: false);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation(ApprovalProtocol.VersionHeader, ApprovalProtocol.Version);
        request.Headers.TryAddWithoutValidation(ApprovalProtocol.RequestIdHeader, requestId);
        request.Headers.TryAddWithoutValidation(ApprovalProtocol.NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(ApprovalProtocol.SignatureHeader, signature);

        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            if ((int)response.StatusCode != 200 ||
                response.Content.Headers.ContentLength > ApprovalProtocol.MaxResponseBytes)
                return ApprovalAnswer.Fallback;
            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, timeoutCts.Token);
                if (read == 0) break;
                if (buffer.Length + read > ApprovalProtocol.MaxResponseBytes)
                    return ApprovalAnswer.Fallback;
                buffer.Write(chunk, 0, read);
            }
            var responseBody = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return ApprovalProtocol.ValidateResponse(secret, (int)response.StatusCode, responseBody, requestId, nonce);
        }
        catch (OperationCanceledException) { return ApprovalAnswer.Fallback; }
        catch (HttpRequestException) { return ApprovalAnswer.Fallback; }
        catch (IOException) { return ApprovalAnswer.Fallback; }
        catch (Exception) { return ApprovalAnswer.Fallback; }
    }
}
