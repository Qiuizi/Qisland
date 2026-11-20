using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;

namespace Qisland.App;

/// <summary>
/// 从 GitHub Releases 自动更新。原理很朴素：
/// 查 releases 列表 → 找比自己新的版本 → 岛上提示 → 点一下下载 zip → 解压到临时目录
/// → 写一个小脚本：等本进程退出、把新文件盖到程序目录、再启动 → 自己退出。
/// 不需要安装器，也不需要管理员权限（程序目录要可写）。
/// 未登录的 GitHub API 每小时 60 次，这里 6 小时查一次，足够。
/// </summary>
public sealed class Updater
{
    public const string Repo = "Qiuizi/Qisland";
    private const string Id = "update";
    private readonly ActivityStore _store;
    private readonly HttpClient _http;
    private readonly Action _quit;
    private Release? _found;
    private bool _busy;

    public static string Current { get; } = ReadVersion();

    public Updater(ActivityStore store, HttpClient http, Action quit) { _store = store; _http = http; _quit = quit; }

    private sealed record Release(string Tag, string Name, string Notes, string Url, string? ZipUrl, long ZipSize, bool Pre, string? Sha256);

    /// <summary>下载专用：不设总超时（大文件），由每次尝试自己的握手/卡顿超时控制。</summary>
    private static readonly HttpClient Dl = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>国内直连 GitHub 的下载域名（release-assets）经常 SSL 握手被掐。依次尝试：直连两次 → 公共加速镜像。
    /// 镜像是第三方，所以只有 GitHub API 给出了 sha256 时才走镜像，下载完逐字节校验，对不上就丢弃。</summary>
    private static readonly string[] Mirrors = ["https://gh-proxy.com/", "https://ghfast.top/"];

    private static string ReadVersion()
    {
        var v = typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    private static string SkipFile => Path.Combine(App.DataDir, "update-skip.txt");

    public async Task Loop()
    {
        await Task.Delay(TimeSpan.FromSeconds(40));
        while (true)
        {
            var ok = await Check(manual: false);
            await App.Nap(ok ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(45)); // 开机时网还没通 / GitHub 限流：别等 6 小时
        }
    }

    /// <summary>manual：托盘里点的「检查更新」，没有新版本也要说一声。</summary>
    public async Task<bool> Check(bool manual)
    {
        if (_busy) return true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=15");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) { App.Log($"检查更新: HTTP {(int)resp.StatusCode}"); if (manual) Say("检查更新失败", $"GitHub 返回 {(int)resp.StatusCode}", ActivityKind.Warning); return false; }
            var best = Pick(await resp.Content.ReadAsStringAsync(cts.Token), Current, includePre: Current.Contains('-'));
            if (best == null) { App.Log($"检查更新: 已是最新 {Current}"); if (manual) Say("已是最新版本", "v" + Current, ActivityKind.Done); return true; }
            if (!manual && File.Exists(SkipFile) && File.ReadAllText(SkipFile).Trim() == best.Tag) return true;
            _found = best;
            App.Log($"发现新版本 {best.Tag}");
            var body = FirstLine(best.Notes);
            _store.Upsert(new IslandActivity(Id, "update", $"Qisland {best.Tag} 可以更新了", body.Length > 0 ? $"点一下更新 · {body}" : "点一下下载并重启", ActivityKind.Info, 58, DurationSeconds: 0), sticky: true, quiet: true);
            return true;
        }
        catch (Exception ex) { App.Log("检查更新: " + ex.Message); if (manual) Say("检查更新失败", "网络不通或 GitHub 暂时不可用", ActivityKind.Warning); return false; }
    }

    private void Say(string title, string body, ActivityKind kind) =>
        _store.Upsert(new IslandActivity(Id, "update", title, body, kind, 50, DurationSeconds: 4, ResetTtl: true));

    private static string FirstLine(string s)
    {
        foreach (var raw in s.Split('\n'))
        {
            var l = raw.Trim().TrimStart('#', '-', '*', ' ').Replace("**", "");
            if (l.Length > 1) return l.Length > 28 ? l[..28] + "…" : l;
        }
        return "";
    }

    private static Release? Pick(string json, string current, bool includePre)
    {
        using var doc = JsonDocument.Parse(json);
        Release? best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.GetProperty("draft").GetBoolean()) continue;
            var pre = r.GetProperty("prerelease").GetBoolean();
            if (pre && !includePre) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (SemVer.Compare(tag, current) <= 0) continue;
            if (best != null && SemVer.Compare(tag, best.Tag) <= 0) continue;
            string? zip = null, sha = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (zip == null || n.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                {
                    zip = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64();
                    sha = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:") ? d.GetString()![7..].ToLowerInvariant() : null;
                }
            }
            best = new Release(tag, r.GetProperty("name").GetString() ?? tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.GetProperty("html_url").GetString() ?? "", zip, size, pre, sha);
        }
        return best;
    }

    /// <summary>岛上那条「可以更新」被点了。</summary>
    public bool Open(LiveActivity a)
    {
        if (a.Id != Id) return false;
        if (_found == null || _busy) return true;
        if (_found.ZipUrl == null) { Process.Start(new ProcessStartInfo(_found.Url) { UseShellExecute = true }); return true; }
        _ = Install(_found);
        return true;
    }

    public void Skip()
    {
        if (_found == null) return;
        try { File.WriteAllText(SkipFile, _found.Tag); } catch { }
        _store.Remove(Id);
    }

    /// <summary>一次下载尝试：15 秒内拿不到响应头、或连续 20 秒没收到数据，就判失败换下一条线路。</summary>
    private static async Task Download(string url, string path, Release r, string label, Action<double, string> progress)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var resp = await Dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? r.ZipSize;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
        await using var dst = File.Create(path);
        var buf = new byte[81920];
        long got = 0; var last = DateTime.MinValue;
        while (true)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var n = await src.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
            got += n;
            if ((DateTime.Now - last).TotalMilliseconds > 150 && total > 0)
            {
                last = DateTime.Now;
                progress(0.9 * got / total, $"{got / 1048576.0:0.0} / {total / 1048576.0:0.0} MB · {label}");
            }
        }
        if (total > 0 && got != total) throw new IOException($"只下到 {got}/{total} 字节");
    }

    private static string ResultFile => Path.Combine(Path.GetTempPath(), "Qisland-update", "result.txt");

    /// <summary>以 --update-failed 启动（替换脚本回滚后拉起的旧版）：读脚本留下的结果，记日志并在岛上报错。</summary>
    public void ReportFailedUpdate()
    {
        string? text = null;
        try { if (File.Exists(ResultFile)) text = File.ReadAllText(ResultFile); } catch (Exception ex) { App.Log("读更新结果失败: " + ex.Message); }
        var res = UpdateScript.ParseResult(text);
        var msg = UpdateScript.Describe(res);
        App.Log($"更新失败（{res.State} {res.Detail}）: {msg}");
        _store.Upsert(new IslandActivity(Id, "update", $"更新没成功 · 仍是 v{Current}", msg, ActivityKind.Error, 60, DurationSeconds: 0, Replace: true), sticky: true);
    }

    private async Task Install(Release r)
    {
        _busy = true;
        var tmp = Path.Combine(Path.GetTempPath(), "Qisland-update");
        try
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var zip = Path.Combine(tmp, "pkg.zip");
            void Progress(double p, string body) =>
                App.Current.Dispatcher.BeginInvoke(() => _store.Upsert(new IslandActivity(Id, "update", $"正在更新到 {r.Tag}", body, ActivityKind.Running, 70, DurationSeconds: 0), sticky: true, progress: p));

            var routes = new List<(string url, string label)> { (r.ZipUrl!, "GitHub"), (r.ZipUrl!, "GitHub") };
            if (r.Sha256 != null) foreach (var m in Mirrors) routes.Add((m + r.ZipUrl, "加速镜像"));
            Exception? lastErr = null; var ok = false;
            for (var i = 0; i < routes.Count && !ok; i++)
            {
                var (url, label) = routes[i];
                try
                {
                    Progress(0, i == 0 ? "下载中…" : $"换线路重试（{label}）…");
                    await Download(url, zip, r, label, Progress);
                    if (r.Sha256 != null)
                    {
                        string h;
                        await using (var fs = File.OpenRead(zip)) h = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                        if (h != r.Sha256) throw new InvalidDataException($"校验不一致 {h[..12]}");
                    }
                    else if (r.ZipSize > 0 && new FileInfo(zip).Length != r.ZipSize) throw new InvalidDataException("文件大小不对");
                    ok = true;
                    App.Log($"更新包下载完成（{label}）{url}");
                }
                catch (Exception ex) { lastErr = ex; App.Log($"更新下载失败（{label}）: {ex.GetType().Name} {ex.Message}"); await Task.Delay(800); }
            }
            if (!ok) throw lastErr ?? new IOException("下载失败");
            Progress(0.93, "解压中…");
            var dir = Path.Combine(tmp, "pkg");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir, true));
            // 压缩包里可能多包了一层目录：找到 Qisland.exe 所在的那一层
            var exe = Directory.GetFiles(dir, "Qisland.exe", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                      ?? throw new InvalidOperationException("压缩包里没有 Qisland.exe");
            var from = Path.GetDirectoryName(exe)!;
            var appDir = AppContext.BaseDirectory.TrimEnd('\\');

            var script = Path.Combine(tmp, "apply.cmd");
            var pid = Environment.ProcessId;
            // 先备份旧版；robocopy 退出码 ≥ 8（文件被锁等）就用备份还原并启动旧版，结果写进 result.txt 给旧版提示
            File.WriteAllText(script, UpdateScript.Build(pid, from, appDir, Path.Combine(tmp, "backup"), ResultFile), new UTF8Encoding(false));
            App.Log($"更新 {Current} → {r.Tag}，覆盖 {appDir}");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            Progress(1, "马上重启…");
            await Task.Delay(600);
            App.Current.Dispatcher.Invoke(_quit);
        }
        catch (Exception ex)
        {
            App.Log("更新失败: " + ex);
            _busy = false;
            App.Current.Dispatcher.BeginInvoke(() =>
                _store.Upsert(new IslandActivity(Id, "update", "更新没成功", "网络不通，点一下再试", ActivityKind.Error, 60, DurationSeconds: 0), sticky: true, quiet: true));
        }
    }
}

/// <summary>够用的语义化版本比较：1.0.0-preview &lt; 1.0.0-preview.2 &lt; 1.0.0 &lt; 1.0.1。</summary>
public static class SemVer
{
    public static int Compare(string a, string b)
    {
        static (int[] core, string[] pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            var plus = v.IndexOf('+'); if (plus >= 0) v = v[..plus];
            var dash = v.IndexOf('-');
            var core = (dash >= 0 ? v[..dash] : v).Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
            var pre = dash >= 0 ? v[(dash + 1)..].Split('.') : [];
            return (core, pre);
        }
        var (ca, pa) = Split(a); var (cb, pb) = Split(b);
        for (var i = 0; i < Math.Max(ca.Length, cb.Length); i++)
        {
            var x = i < ca.Length ? ca[i] : 0; var y = i < cb.Length ? cb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;   // 正式版比同号预览版新
        if (pb.Length == 0) return -1;
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var ia); var nb = int.TryParse(pb[i], out var ib);
            var c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
