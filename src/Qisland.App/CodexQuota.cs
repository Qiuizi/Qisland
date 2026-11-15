using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Qisland.App;

/// <summary>
/// Codex 额度：拉起 Codex 自带的 `codex app-server`，按官方 JSON-RPC 读 account/rateLimits/read，读完就关。
/// 只读，不登录、不碰令牌文件；每 5 分钟一次，Codex 刚干完活时提前刷新。
/// </summary>
public sealed class CodexQuota
{
    public sealed record Window(double UsedPercent, int Minutes, DateTime ResetsAt);
    public Window? Primary { get; private set; }
    public Window? Secondary { get; private set; }
    public string Plan { get; private set; } = "";
    public DateTime LastOk { get; private set; }
    public event Action? Changed;

    private readonly Action<Action> _post;
    private DateTime _next = DateTime.Now.AddSeconds(8);
    private int _busy;
    private string? _exe;

    public CodexQuota(Action<Action> post) => _post = post;

    public bool Fresh => Primary != null && DateTime.Now - LastOk < TimeSpan.FromMinutes(30);

    public void Tick()
    {
        if (DateTime.Now < _next) return;
        _next = DateTime.Now.AddMinutes(5);
        Refresh();
    }

    /// <summary>Codex 一轮结束后调用：额度刚变，30 秒后读一次。</summary>
    public void Soon()
    {
        var t = DateTime.Now.AddSeconds(30);
        if (t < _next) _next = t;
    }

    public void Refresh()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var json = Query();
                if (json != null && Parse(json)) _post(() => Changed?.Invoke());
            }
            catch (Exception ex) { App.Log("Codex 额度: " + ex.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private string? FindExe()
    {
        if (_exe != null && File.Exists(_exe)) return _exe;
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai", "codex");
        var guess = Path.Combine(npm, "node_modules", "@openai", "codex-win32-x64", "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
        if (File.Exists(guess)) return _exe = guess;
        if (Directory.Exists(npm))
            _exe = Directory.EnumerateFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
        return _exe;
    }

    private string? Query()
    {
        var exe = FindExe();
        if (exe == null) return null;
        var psi = new ProcessStartInfo(exe, "app-server")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi);
        if (p == null) return null;
        p.ErrorDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        try
        {
            p.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"qisland\",\"title\":\"Qisland\",\"version\":\"1.0\"}}}");
            p.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");
            p.StandardInput.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"account/rateLimits/read\"}");
            p.StandardInput.Flush();
            var until = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < until)
            {
                var t = p.StandardOutput.ReadLineAsync();
                if (!t.Wait(until - DateTime.Now) || t.Result == null) return null;
                if (t.Result.StartsWith("{\"id\":2")) return t.Result;
            }
            return null;
        }
        finally { try { p.Kill(true); } catch { } }
    }

    internal bool Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var res) || !res.TryGetProperty("rateLimits", out var rl)) return false;
        Window? W(string n)
        {
            if (!rl.TryGetProperty(n, out var w) || w.ValueKind != JsonValueKind.Object) return null;
            var used = w.TryGetProperty("usedPercent", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
            var mins = w.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            var reset = w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).LocalDateTime : DateTime.MinValue;
            return new Window(used, mins, reset);
        }
        Primary = W("primary");
        Secondary = W("secondary");
        Plan = rl.TryGetProperty("planType", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() ?? "" : "";
        LastOk = DateTime.Now;
        return Primary != null || Secondary != null;
    }

    /// <summary>演示用（--snapshot）。</summary>
    public void Demo(double five, double week)
    {
        Primary = new Window(five, 300, DateTime.Now.AddHours(2.4));
        Secondary = new Window(week, 10080, DateTime.Now.AddDays(4.2));
        Plan = "plus"; LastOk = DateTime.Now;
    }
}
