using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 只监听 127.0.0.1 的极简 HTTP 服务。用 TcpListener 而不是 HttpListener，免去 URL ACL 和管理员权限。
/// 兼容 WinIslands 的 /v1/island/push，外加 /v1/qisland/ingest。
/// </summary>
public sealed class ApiServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Action<Action> _post;
    private readonly ActivityStore _store;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>挂起式审批：hook 连着不放，直到岛上给出决定（allow / deny / 空）。</summary>
    public Func<string, string, Task<string>>? Approval { get; set; }

    public ApiServer(int port, ActivityStore store, Action<Action> postToUi)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _store = store;
        _post = postToUi;
    }

    public void Start()
    {
        _listener.Start();
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Handle(client);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 3000;
                var stream = client.GetStream();
                var (method, path, body) = await ReadRequest(stream);
                var clean = path.Split('?')[0].TrimEnd('/');
                var (code, json) = method == "POST" && clean.StartsWith("/v1/approval/", StringComparison.Ordinal) && Approval != null
                    ? (200, $"{{\"decision\":{JsonSerializer.Serialize(await Approval(clean["/v1/approval/".Length..], body))}}}")
                    : Route(method, path, body);
                var payload = Encoding.UTF8.GetBytes(json);
                var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
            catch { /* 客户端断开就算了 */ }
        }
    }

    private static async Task<(string, string, string)> ReadRequest(NetworkStream s)
    {
        var buf = new byte[64 * 1024];
        var total = 0;
        int headerEnd = -1;
        while (headerEnd < 0 && total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total));
            if (n == 0) break;
            total += n;
            headerEnd = IndexOf(buf, total, "\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", "");
        var header = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = header.Split("\r\n");
        var first = lines[0].Split(' ');
        var len = 0;
        foreach (var l in lines)
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(l[15..].Trim(), out len);
        len = Math.Clamp(len, 0, 1024 * 1024);
        var bodyBytes = new byte[len];
        var have = Math.Min(total - headerEnd - 4, len);
        Array.Copy(buf, headerEnd + 4, bodyBytes, 0, have);
        while (have < len)
        {
            var n = await s.ReadAsync(bodyBytes.AsMemory(have));
            if (n == 0) break;
            have += n;
        }
        return (first[0].ToUpperInvariant(), first.Length > 1 ? first[1] : "/", Encoding.UTF8.GetString(bodyBytes, 0, have));
    }

    private static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pat)
    {
        var i = b.AsSpan(0, len).IndexOf(pat);
        return i;
    }

    private (int, string) Route(string method, string path, string body)
    {
        path = path.Split('?')[0].TrimEnd('/');
        if (method == "GET" && (path is "" or "/v1/status" or "/health"))
            return (200, "{\"ok\":true,\"app\":\"Qisland\"}");

        if (method == "POST" && path == "/v1/qisland/ingest")
        {
            var drafts = QislandIngest.Parse(body);
            _post(() => { foreach (var d in drafts) _store.Upsert(d); });
            return (200, $"{{\"ok\":true,\"accepted\":{drafts.Count}}}");
        }

        if (method == "POST" && (path is "/v1/island/push" or "/v3/island/push" or "/push"))
        {
            var d = ParsePush(body, out var progress);
            if (d is null) return (400, "{\"ok\":false,\"error\":\"需要 title\"}");
            _post(() => _store.Upsert(d, progress));
            return (200, $"{{\"ok\":true,\"id\":{JsonSerializer.Serialize(d.Id)}}}");
        }

        if (method == "POST" && path.StartsWith("/v1/agent/", StringComparison.Ordinal))
        {
            var a = AgentEventParser.Parse(body, path["/v1/agent/".Length..]);
            if (a is not null) _post(() => _store.Upsert(a));
            return (200, "{\"ok\":true}");
        }

        if (method == "DELETE" && path.StartsWith("/v1/island/push/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/v1/island/push/".Length..]);
            _post(() => _store.Remove(id));
            return (200, "{\"ok\":true}");
        }
        return (404, "{\"ok\":false,\"error\":\"not found\"}");
    }

    internal static PushDraft? ParsePush(string body, out double? progress)
    {
        progress = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int? I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            var title = S("title");
            if (title.Length == 0) return null;
            if (r.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number)
            {
                var v = p.GetDouble();
                progress = Math.Clamp(v > 1 ? v / 100 : v, 0, 1);
            }
            var id = S("id");
            return new PushDraft
            {
                Id = id.Length > 0 ? id : "push:" + Guid.NewGuid().ToString("N")[..8],
                Title = title,
                Body = S("body"),
                Subtitle = S("subtitle"),
                Icon = S("icon"),
                Type = S("type").ToLowerInvariant(),
                Priority = S("priority").ToLowerInvariant(),
                Source = S("source").Length > 0 ? S("source") : "push",
                DurationSeconds = I("duration_seconds") ?? I("duration") ?? I("durationSeconds") ?? 12,
                ResetTtl = true,
            };
        }
        catch (JsonException) { return null; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
