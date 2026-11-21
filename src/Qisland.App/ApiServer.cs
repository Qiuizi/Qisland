using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
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
    /// <summary>调试：按条目 id 走一遍点击跳转。只在本机回环上监听。</summary>
    public Action<string>? Jump;
    /// <summary>调试预览：只解析会跳到哪，不调用 Jump、更新器、进程或窗口。</summary>
    public Func<string, string>? Preview;
    public Func<string>? PerfJson;
    public Action<double, string?>? Peek;
    private readonly Action<Action> _post;
    private readonly ActivityStore _store;
    private readonly string? _approvalKey;
    private readonly byte[]? _apiToken;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _approvalSlots = new(ApprovalReserve, ApprovalReserve);
    private readonly SemaphoreSlim _overflowSlots = new(OverflowReserve, OverflowReserve);
    private readonly object _handlersGate = new();
    private readonly HashSet<Task> _handlers = [];
    private readonly object _clientsGate = new();
    private readonly HashSet<TcpClient> _clients = [];
    private readonly TimeSpan _receiveTimeout;
    private readonly TimeSpan _approvalTimeout;
    private readonly object _stopGate = new();
    private Task<bool>? _stopTask;
    private Task? _acceptTask;
    private int _disposed;
    private int _cleaned;
    private const int ApprovalReserve = 8;
    private const int OverflowReserve = 32;
    private static readonly TimeSpan OverflowReceiveTimeout = TimeSpan.FromMilliseconds(500);
    /// <summary>挂起式审批：hook 连着不放，直到岛上给出决定（allow / deny / 空）。</summary>
    public Func<string, string, Task<string>>? Approval { get; set; }
    /// <summary>每条 Agent 事件（已经在 UI 线程上），给 AI 页的多会话列表用。</summary>
    public Action<IslandActivity>? AgentEvent { get; set; }

    public ApiServer(int port, ActivityStore store, Action<Action> postToUi, string? approvalKey = null, string? apiToken = null)
        : this(port, store, postToUi, approvalKey, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), 128, apiToken)
    {
    }

    /// <summary>按 Token 文件的三种状态启动：Invalid 时普通读写一律 503 并说明原因（健康检查、HMAC 审批路由不受影响）。</summary>
    public ApiServer(int port, ActivityStore store, Action<Action> postToUi, string? approvalKey, ApiTokenConfig token)
        : this(port, store, postToUi, approvalKey, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), 128,
            token.State == ApiTokenState.Enabled ? token.Token : null)
    {
        if (token.State == ApiTokenState.Invalid) _tokenError = token.Error ?? "Token 文件无效";
    }

    private readonly string? _tokenError;

    internal ApiServer(int port, ActivityStore store, Action<Action> postToUi,
        TimeSpan receiveTimeout, TimeSpan approvalTimeout, int maxConnections)
        : this(port, store, postToUi, null, receiveTimeout, approvalTimeout, maxConnections)
    {
    }

    internal ApiServer(int port, ActivityStore store, Action<Action> postToUi, string? approvalKey,
        TimeSpan receiveTimeout, TimeSpan approvalTimeout, int maxConnections, string? apiToken = null)
    {
        // 设了 Token（%APPDATA%\Qisland\api-token.txt）时，普通 HTTP 请求必须带 X-Qisland-Token 或 X-WinIslands-Token
        _apiToken = string.IsNullOrWhiteSpace(apiToken) ? null : Encoding.UTF8.GetBytes(apiToken.Trim());
        _listener = new TcpListener(IPAddress.Loopback, port);
        _store = store;
        _post = postToUi;
        _approvalKey = approvalKey;
        _receiveTimeout = receiveTimeout;
        _approvalTimeout = approvalTimeout;
        _slots = new SemaphoreSlim(maxConnections, maxConnections);
    }

    internal int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>还没结束的处理任务数。任务已完成、但「从集合里移除」的续延还没跑到时不算（停机等待看的是 IsCompleted，两边口径一致）。</summary>
    internal int ActiveHandlers
    {
        get { lock (_handlersGate) return _handlers.Count(t => !t.IsCompleted); }
    }

    /// <summary>还记在跟踪集合里的连接数（测试用，确认每条退出路径都清理了）。</summary>
    internal int TrackedClients
    {
        get { lock (_clientsGate) return _clients.Count; }
    }

    public void Start()
    {
        if (_acceptTask != null) return;
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { break; }
            lock (_clientsGate) _clients.Add(client);
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
        // 跟踪集合的清理放在最外层 finally：快速 503、超时、异常、正常返回，每条出口都会走到
        try { await HandleCore(client).ConfigureAwait(false); }
        finally { lock (_clientsGate) _clients.Remove(client); }
    }

    private async Task HandleCore(TcpClient client)
    {
        using (client)
        {
            var normalSlot = _slots.Wait(0);
            if (!normalSlot && !_overflowSlots.Wait(0))
            {
                await Reject(client).ConfigureAwait(false);
                return;
            }
            var approvalSlot = false;
            try
            {
                if (!normalSlot && !await WaitForData(client, OverflowReceiveTimeout, _cts.Token).ConfigureAwait(false))
                {
                    await Reject(client).ConfigureAwait(false);
                    return;
                }
                var stream = client.GetStream();
                using var receive = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                receive.CancelAfter(normalSlot ? _receiveTimeout : TimeSpan.FromSeconds(2));
                var (method, path, body, headers) = await ReadRequest(stream, receive.Token).ConfigureAwait(false);
                var clean = path.Split('?')[0].TrimEnd('/');
                if (method == "POST" && IsApprovalPath(clean))
                {
                    // 普通连接再多也不能挡住审批：审批走独立的小池子，满了才回 503
                    if (!_approvalSlots.Wait(0))
                    {
                        await Response(stream, 503, "{\"ok\":false,\"error\":\"too many approval requests\"}").ConfigureAwait(false);
                        return;
                    }
                    approvalSlot = true;
                    var (code, json) = await ApprovalRoute(clean, headers, body).ConfigureAwait(false);
                    await Response(stream, code, json).ConfigureAwait(false);
                    return;
                }
                if (!normalSlot)
                {
                    // 已经超过普通连接上限；等读完请求再拒绝，避免把审批请求误伤在入口
                    await Response(stream, 503, "{\"ok\":false,\"error\":\"too many connections\"}").ConfigureAwait(false);
                    return;
                }
                if (_tokenError != null && !IsHealth(method, clean))
                {
                    // 配置了 Token 但文件读不了 / 无效：明确拒绝，不能悄悄当成「没配置」放行
                    await Response(stream, 503, JsonSerializer.Serialize(new { ok = false, error = "api token invalid", detail = _tokenError })).ConfigureAwait(false);
                    return;
                }
                if (!Authorized(method, clean, headers))
                {
                    await Response(stream, 401, "{\"ok\":false,\"error\":\"unauthorized\"}").ConfigureAwait(false);
                    return;
                }
                var (normalCode, normalJson) = method == "GET" && clean is "/v1/island/active" or "/v3/island/active"
                    ? await ActiveRoute(full: _apiToken != null).ConfigureAwait(false)
                    : SafeRoute(method, path, body);
                await Response(stream, normalCode, normalJson).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!normalSlot) await Reject(client).ConfigureAwait(false);
            }
            catch { /* 客户端断开就算了 */ }
            finally
            {
                if (approvalSlot) _approvalSlots.Release();
                if (normalSlot) _slots.Release();
                else _overflowSlots.Release();
            }
        }
    }

    private static async Task<bool> WaitForData(TcpClient client, TimeSpan timeout, CancellationToken ct)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            try { if (client.Available > 0) return true; } catch { return false; }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
        try { return client.Available > 0; } catch { return false; }
    }

    private async Task Response(NetworkStream stream, int code, string json)
    {
        var payload = code == 204 ? [] : Encoding.UTF8.GetBytes(json);
        var type = payload.Length > 0 ? "Content-Type: application/json; charset=utf-8\r\n" : "";
        var reason = code switch { 200 => "OK", 204 => "No Content", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 500 => "Internal Server Error", 503 => "Service Unavailable", 504 => "Gateway Timeout", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\n{type}Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        write.CancelAfter(TimeSpan.FromSeconds(5));
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), write.Token).ConfigureAwait(false);
        await stream.WriteAsync(payload, write.Token).ConfigureAwait(false);
    }

    private async Task Reject(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            await Response(stream, 503, "{\"ok\":false,\"error\":\"too many connections\"}").ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task<(string Method, string Path, string Body, Dictionary<string, string> Headers)> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var total = 0;
        int headerEnd = -1;
        while (headerEnd < 0 && total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
            headerEnd = IndexOf(buf, total, "\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var header = Encoding.ASCII.GetString(buf, 0, headerEnd);
        var lines = header.Split("\r\n");
        var first = lines[0].Split(' ');
        var len = 0;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var colon = l.IndexOf(':');
            if (colon > 0)
                headers[l[..colon].Trim()] = l[(colon + 1)..].Trim();
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(l[15..].Trim(), out len);
        }
        len = Math.Clamp(len, 0, 1024 * 1024);
        var bodyBytes = new byte[len];
        var have = Math.Min(total - headerEnd - 4, len);
        Array.Copy(buf, headerEnd + 4, bodyBytes, 0, have);
        while (have < len)
        {
            var n = await s.ReadAsync(bodyBytes.AsMemory(have), ct).ConfigureAwait(false);
            if (n == 0) break;
            have += n;
        }
        return (first[0].ToUpperInvariant(), first.Length > 1 ? first[1] : "/",
            Encoding.UTF8.GetString(bodyBytes, 0, have), headers);
    }

    private static int IndexOf(byte[] b, int len, ReadOnlySpan<byte> pat)
    {
        var i = b.AsSpan(0, len).IndexOf(pat);
        return i;
    }

    /// <summary>健康检查不带数据，不要求 Token；其余普通路由在设了 Token 时都要校验（定长比较）。审批路由走自己的 HMAC，不经过这里。</summary>
    private bool Authorized(string method, string clean, Dictionary<string, string> headers)
    {
        if (_apiToken == null) return true;
        if (IsHealth(method, clean)) return true;
        foreach (var name in new[] { "X-Qisland-Token", "X-WinIslands-Token" })
        {
            if (headers.TryGetValue(name, out var value)
                && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value.Trim()), _apiToken))
                return true;
        }
        return false;
    }

    private static bool IsHealth(string method, string clean) =>
        method == "GET" && clean is "" or "/health" or "/v1/status" or "/v1/health";

    private static bool IsApprovalPath(string path)
    {
        const string prefix = "/v1/approval/";
        return path.StartsWith(prefix, StringComparison.Ordinal)
               && path.Length > prefix.Length
               && !path[prefix.Length..].Contains('/');
    }

    private async Task<(int, string)> ApprovalRoute(
        string path, Dictionary<string, string> headers, string body)
    {
        if (_approvalKey == null || Approval == null)
            return (503, "{\"ok\":false,\"error\":\"审批接口未启用\"}");
        if (!ApprovalProtocol.VerifyRequest(_approvalKey, "POST", path, headers, body,
                out var requestId, out var nonce))
            return (403, "{\"ok\":false,\"error\":\"unauthorized\"}");

        var source = Uri.UnescapeDataString(path["/v1/approval/".Length..]);
        if (!AgentPolicy.CanApproveOnIsland(source)) return (204, "");

        string decision;
        try { decision = await Approval(source, body).WaitAsync(_approvalTimeout, _cts.Token).ConfigureAwait(false); }
        catch (TimeoutException) { return (504, "{\"ok\":false,\"error\":\"审批等待超时\"}"); }
        catch { return (500, "{\"ok\":false,\"error\":\"审批处理失败\"}"); }
        if (decision is not ("allow" or "deny")) return (204, "");

        try { return (200, ApprovalProtocol.BuildResponse(_approvalKey, requestId, nonce, decision)); }
        catch { return (500, "{\"ok\":false,\"error\":\"审批响应失败\"}"); }
    }

    /// <summary>
    /// GET /v1/island/active（/v3 同）：只读，列出当前的外部卡片（本机接口 / 收件箱推进来的），
    /// Qisland 自己的条目（验证码、日程、待办……）不往外给。ActivityStore 只在 UI 线程上动，所以到 UI 线程上取快照。
    /// 没配置 Token 时任何本机进程都能调这个接口，所以只给最少字段（id、来源、类型、优先级、进度、过期时间），
    /// 不给标题和正文；配置了 Token 且请求带对了 Token（full）才返回完整卡片。
    /// </summary>
    private async Task<(int, string)> ActiveRoute(bool full)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                var cards = _store.Ranked().Where(a => a.External).Select(a =>
                {
                    var card = new Dictionary<string, object?>
                    {
                        ["id"] = a.Id,
                        ["source"] = a.Source,
                        ["type"] = a.Kind switch { Qisland.Core.ActivityKind.Error => "error", Qisland.Core.ActivityKind.Warning or Qisland.Core.ActivityKind.WaitingApproval => "warning", Qisland.Core.ActivityKind.Done => "success", _ => "info" },
                        ["priority"] = a.Priority >= 70 ? "high" : a.Priority <= 15 ? "low" : "normal",
                        ["progress"] = a.Progress,
                        ["expires_at"] = a.Expires?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (full) { card["title"] = a.Title; card["body"] = a.Body; }
                    return card;
                }).ToList();
                done.TrySetResult(JsonSerializer.Serialize(cards));
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        try { return (200, await done.Task.WaitAsync(TimeSpan.FromSeconds(3), _cts.Token).ConfigureAwait(false)); }
        catch (TimeoutException) { return (503, "{\"ok\":false,\"error\":\"busy\"}"); }
        catch (OperationCanceledException) { throw; }
        catch { return (500, "{\"ok\":false,\"error\":\"读取失败\"}"); }
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
        if (method == "GET" && (path is "" or "/v1/status" or "/health" or "/v1/health"))
            return (200, "{\"ok\":true,\"app\":\"Qisland\",\"product\":\"Qisland\"}");

        if (method == "POST" && path == "/v1/qisland/ingest")
        {
            // 和收件箱同一个入口（IslandPayload）：没有类别时按内容判定，有 $type 时照它走
            var r = IslandPayload.Decode(body, PayloadRoute.Ingest);
            if (!r.Ok) return (400, $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(r.Error)}}}");
            var drafts = r.Cards;
            // 外部入口：只写外部命名空间 / 自己写过的条目，内部活动由 ActivityStore 拒绝
            _post(() => { foreach (var (d, p) in drafts) _store.UpsertExternal(d, p); });
            return (200, $"{{\"ok\":true,\"accepted\":{drafts.Count}}}");
        }

        if (method == "GET" && path == "/v1/perf" && PerfJson != null)
            return (200, PerfJson());

#if DEBUG
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
            if (dry)
            {
                var target = Preview?.Invoke(id) ?? "没有跳转解析器";
                return (200, JsonSerializer.Serialize(new { ok = true, preview = target }));
            }
            _post(() => Jump(id));
            return (200, "{\"ok\":true}");
        }

        if (method == "POST" && path == "/v1/debug/bench" && Bench != null)
        {
            _post(Bench);
            return (200, "{\"ok\":true}");
        }
#endif

        if (method == "POST" && (path is "/v1/island/push" or "/v3/island/push" or "/push"))
        {
            // 老入口保留：普通推送、原始 hook 或接入消息都可以进来，解析规则和收件箱一致
            var r = IslandPayload.Decode(body, PayloadRoute.Push);
            if (!r.Ok) return (400, $"{{\"ok\":false,\"error\":{JsonSerializer.Serialize(r.Error)}}}");
            var drafts = r.Cards;
            _post(() => { foreach (var (d, progress) in drafts) _store.UpsertExternal(d, progress); });
            var id = drafts.Count == 1 ? JsonSerializer.Serialize(drafts[0].Draft.Id) : "null";
            return (200, $"{{\"ok\":true,\"accepted\":{drafts.Count},\"id\":{id}}}");
        }

        if (method == "POST" && path.StartsWith("/v1/agent/", StringComparison.Ordinal))
        {
            var a = AgentEventParser.Parse(body, path["/v1/agent/".Length..]);
            if (a is not null) _post(() => { if (_store.UpsertExternal(a)) AgentEvent?.Invoke(a); });
            return (200, "{\"ok\":true}");
        }

        if (method == "DELETE" && path.StartsWith("/v1/island/push/", StringComparison.Ordinal))
        {
            // 只能撤回外部命名空间里的卡片；传 "demo" 或 "ext:demo" 都指同一张
            var id = ExternalActivity.Namespace(Uri.UnescapeDataString(path["/v1/island/push/".Length..]));
            _post(() => _store.RemoveExternal(id));
            return (200, "{\"ok\":true}");
        }
        return (404, "{\"ok\":false,\"error\":\"not found\"}");
    }

    /// <summary>
    /// 异步停机。先取消监听和在途请求；到期仍未收尾的连接会被强制关闭。
    /// 返回 true 表示所有传输任务都在期限内结束。
    /// </summary>
    public Task<bool> StopAsync(TimeSpan timeout)
    {
        lock (_stopGate)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            return _stopTask ??= StopCoreAsync(timeout);
        }
    }

    private async Task<bool> StopCoreAsync(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
        }

        var watch = Stopwatch.StartNew();
        var graceful = await WaitForTask(_acceptTask, Remaining(timeout, watch)).ConfigureAwait(false);
        if (graceful)
        {
            Task[] handlers;
            lock (_handlersGate) handlers = _handlers.ToArray();
            graceful = await WaitForAll(handlers, Remaining(timeout, watch)).ConfigureAwait(false);
        }

        if (!graceful)
        {
            TcpClient[] clients;
            lock (_clientsGate) clients = _clients.ToArray();
            foreach (var client in clients)
            {
                try { client.Close(); } catch { }
            }
            // 给被强制断开的处理任务一点收尾时间，但不让调用方跨过总期限。
            await Task.WhenAny(Task.WhenAll(SnapshotTransportTasks()), Task.Delay(Remaining(timeout, watch))).ConfigureAwait(false);
        }

        CleanupIfDone();
        return graceful;
    }

    private Task[] SnapshotTransportTasks()
    {
        var tasks = new List<Task>();
        if (_acceptTask != null) tasks.Add(_acceptTask);
        lock (_handlersGate) tasks.AddRange(_handlers);
        return tasks.ToArray();
    }

    private void CleanupIfDone()
    {
        var tasks = SnapshotTransportTasks();
        if (tasks.Any(t => !t.IsCompleted) || Interlocked.Exchange(ref _cleaned, 1) != 0) return;
        _slots.Dispose();
        _approvalSlots.Dispose();
        _overflowSlots.Dispose();
        _cts.Dispose();
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch watch)
    {
        var left = timeout - watch.Elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static async Task<bool> WaitForTask(Task? task, TimeSpan timeout)
    {
        if (task == null || task.IsCompleted) return true;
        if (timeout <= TimeSpan.Zero) return false;
        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task;
    }

    private static async Task<bool> WaitForAll(Task[] tasks, TimeSpan timeout)
    {
        if (tasks.Length == 0 || tasks.All(t => t.IsCompleted)) return true;
        if (timeout <= TimeSpan.Zero) return false;
        var all = Task.WhenAll(tasks);
        return await Task.WhenAny(all, Task.Delay(timeout)).ConfigureAwait(false) == all;
    }

    /// <summary>
    /// 回收资源。UI 线程上只发起异步停机，绝不同步等待；
    /// 非 UI 线程保留同步语义，兼容原有调用方。
    /// </summary>
    public void Dispose()
    {
        var dispatcher = Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher?.CheckAccess() == true)
        {
            _ = StopAsync(TimeSpan.FromSeconds(2));
            return;
        }

        try { StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
    }
}
