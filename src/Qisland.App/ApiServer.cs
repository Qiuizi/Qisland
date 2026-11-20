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
    /// <summary>本机调试：连续展开收起几次，帧数据写进日志。</summary>
    public Action? Bench;
    /// <summary>调试：按条目 id 走一遍点击跳转（dry=true 只解析目标、写日志，不切前台不启动）。只在本机回环上监听。</summary>
    public Action<string, bool>? Jump;
    public Func<string>? PerfJson;
    public Action<double, string?>? Peek;
    private readonly Action<Action> _post;
    private readonly ActivityStore _store;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;
    private readonly object _handlersGate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _approvalTimeout;
    private Task? _acceptTask;
    private int _disposed;
    /// <summary>挂起式审批：hook 连着不放，直到岛上给出决定（allow / deny / 空）。</summary>
    public Func<string, string, Task<string>>? Approval { get; set; }
    /// <summary>每条 Agent 事件（已经在 UI 线程上），给 AI 页的多会话列表用。</summary>
    public Action<IslandActivity>? AgentEvent { get; set; }

    public ApiServer(int port, ActivityStore store, Action<Action> postToUi)
        : this(port, store, postToUi, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), 128)
    {
    }

    internal ApiServer(int port, ActivityStore store, Action<Action> postToUi,
        TimeSpan receiveTimeout, TimeSpan approvalTimeout, int maxConnections)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _store = store;
        _post = postToUi;
        _receiveTimeout = receiveTimeout;
        _approvalTimeout = approvalTimeout;
        _slots = new SemaphoreSlim(maxConnections, maxConnections);
    }

    internal int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    internal int ActiveHandlers
    {
        get { lock (_handlersGate) return _handlers.Count; }
    }

    public void Start()
    {
        if (_acceptTask != null) return;
        _listener.Start();
        _acceptTask = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            var task = Handle(client);
            lock (_handlersGate) _handlers.Add(task);
            _ = task.ContinueWith(done =>
            {
                lock (_handlersGate) _handlers.Remove(done);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            if (!_slots.Wait(0))
            {
                await Reject(client);
                return;
            }
            try
            {
                var stream = client.GetStream();
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                receive.CancelAfter(_receiveTimeout);
                var (method, path, body) = await ReadRequest(stream, receive.Token);
                var clean = path.Split('?')[0].TrimEnd('/');
                (int code, string json) result;
                if (method == "POST" && clean.StartsWith("/v1/approval/", StringComparison.Ordinal) && Approval != null)
                {
                    try
                    {
                        var decision = await Approval(clean["/v1/approval/".Length..], body)
                            .WaitAsync(_approvalTimeout, _cts.Token);
                        result = (200, $"{{\"decision\":{JsonSerializer.Serialize(decision)}}}");
                    }
                    catch (TimeoutException) { result = (504, "{\"ok\":false,\"error\":\"审批等待超时\"}"); }
                    catch { result = (500, "{\"ok\":false,\"error\":\"审批处理失败\"}"); }
                }
                else result = SafeRoute(method, path, body);
                var payload = Encoding.UTF8.GetBytes(result.json);
                var reason = result.code switch { 200 => "OK", 500 => "Internal Server Error", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error" };
                var head = $"HTTP/1.1 {result.code} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                using var write = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                write.CancelAfter(TimeSpan.FromSeconds(5));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), write.Token);
                await stream.WriteAsync(payload, write.Token);
            }
            catch (OperationCanceledException) { }
            catch { /* 客户端断开就算了 */ }
            finally { _slots.Release(); }
        }
    }

    private async Task Reject(TcpClient client)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"too many connections\"}");
            var head = $"HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(1));
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token);
            await stream.WriteAsync(body, timeout.Token);
        }
        catch { }
    }

    private static async Task<(string, string, string)> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var total = 0;
        int headerEnd = -1;
        while (headerEnd < 0 && total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct);
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
            var n = await s.ReadAsync(bodyBytes.AsMemory(have), ct);
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

    /// <summary>请求内容不合要求时回 400，不让异常把连接直接断掉。</summary>
    private (int, string) SafeRoute(string method, string path, string body)
    {
        try { return Route(method, path, body); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            return (400, $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize("请求内容不合要求：" + ex.GetType().Name)}}}");
        }
    }

    private (int, string) Route(string method, string path, string body)
    {
        path = path.Split('?')[0].TrimEnd('/');
        if (method == "GET" && (path is "" or "/v1/status" or "/health"))
            return (200, "{\"ok\":true,\"app\":\"Qisland\"}");

        if (method == "POST" && path == "/v1/qisland/ingest")
        {
            // 和收件箱同一个入口（IslandPayload），同一份数据两条路结果一致
            var r = IslandPayload.Decode(body, PayloadRoute.Ingest);
            if (!r.Ok) return (400, $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(r.Error)}}}");
            var drafts = r.Cards;
            _post(() => { foreach (var (d, p) in drafts) _store.Upsert(d, p); });
            return (200, $"{{\"ok\":true,\"accepted\":{drafts.Count}}}");
        }

        if (method == "GET" && path == "/v1/perf" && PerfJson != null)
            return (200, PerfJson());

        if (method == "POST" && path == "/v1/debug/peek" && Peek != null)
        {
            // 正文「秒数 [页名]」，比如「8 widgets」：调试 / 截图用，展开后停在指定页
            var parts = body.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var secs = parts.Length > 0 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 1, 30) : 10;
            var page = parts.Length > 1 ? parts[1] : null;
            _post(() => Peek(secs, page));
            return (200, "{\"ok\":true}");
        }

        if (method == "POST" && path == "/v1/debug/jump" && Jump != null)
        {
            using var jd = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var id = jd.RootElement.TryGetProperty("id", out var ie) ? ie.GetString() ?? "" : "";
            var dry = !jd.RootElement.TryGetProperty("dry", out var de) || de.ValueKind != JsonValueKind.False;
            _post(() => Jump(id, dry));
            return (200, "{\"ok\":true}");
        }

        if (method == "POST" && path == "/v1/debug/bench" && Bench != null)
        {
            _post(Bench);
            return (200, "{\"ok\":true}");
        }

        if (method == "POST" && (path is "/v1/island/push" or "/v3/island/push" or "/push"))
        {
            var r = IslandPayload.Decode(body, PayloadRoute.Push);
            if (!r.Ok || r.Cards.Count == 0)
                return (400, $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(r.Error ?? "需要 title")}}}");
            var (d, progress) = r.Cards[0];
            _post(() => _store.Upsert(d, progress));
            return (200, $"{{\"ok\":true,\"id\":{JsonSerializer.Serialize(d.Id)}}}");
        }

        if (method == "POST" && path.StartsWith("/v1/agent/", StringComparison.Ordinal))
        {
            var a = AgentEventParser.Parse(body, path["/v1/agent/".Length..]);
            if (a is not null) _post(() => { _store.Upsert(a); AgentEvent?.Invoke(a); });
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _acceptTask?.GetAwaiter().GetResult(); } catch { }
        Task[] handlers;
        lock (_handlersGate) handlers = _handlers.ToArray();
        try { Task.WhenAll(handlers).GetAwaiter().GetResult(); } catch { }
        _slots.Dispose();
        _cts.Dispose();
    }
}
