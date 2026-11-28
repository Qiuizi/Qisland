using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
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

    /// <summary>自带 .NET 运行时的安装（程序目录里有 hostfxr.dll）：更新时只用自带运行时的包。</summary>
    public static bool SelfContained { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "hostfxr.dll"));

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
        // 上次回滚还没完成：禁止发起新更新，只在岛上提示，不再去查新版本
        if (UpdateScript.IsRollbackFailed(ReadResultText()))
        {
            Say("上次更新回滚未完成", "请重启 Qisland 完成恢复后再检查更新", ActivityKind.Warning);
            return false;
        }
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
            string? zip = null, sha = null; long size = 0; var rank = -1;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                var k = ReleaseAsset.Rank(n, SelfContained);
                if (k >= 0 && k >= rank)
                {
                    rank = k;
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
        // 程序目录写不进去（比如被手动放进了 Program Files）：不硬覆盖，打开发布页让用户下载新的 Setup.exe
        if (!AppDirWritable(AppContext.BaseDirectory))
        {
            App.Log("更新: 程序目录不可写，改为打开发布页 " + AppContext.BaseDirectory);
            Say("程序目录需要管理员权限", "已打开发布页，下载新版 Setup.exe 覆盖安装即可", ActivityKind.Info);
            Process.Start(new ProcessStartInfo(_found.Url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        _ = Install(_found);
        return true;
    }

    /// <summary>能不能在程序目录里建文件：Setup.exe 默认装在 %LOCALAPPDATA%\Programs\Qisland（可写），zip 解压到哪都行，只有放进受保护目录时才会是 false。</summary>
    internal static bool AppDirWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".qisland-write-test-" + Environment.ProcessId);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>发现了一个还没装的新版本时，它的标签（托盘 / 设置里据此显示「跳过」）；没有则为 null。</summary>
    public string? FoundTag => _found?.Tag;

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

    /// <summary>
    /// 启动恢复入口：上次覆盖停在 applying（覆盖到一半被杀/断电）且变更清单还在时，拉起恢复脚本并返回它；
    /// 调用方应随即退出，让脚本在本进程退出后按清单逐文件还原，再以 --update-failed 启动旧版。
    /// 没有中断痕迹时返回 null，什么都不做。
    /// </summary>
    public static Process? StartRecoveryIfInterrupted(string resultFile, string manifestFile, string backupDir,
        string appDir, int pid, string exe = "Qisland.exe", bool waitForPid = true)
    {
        string? text;
        try { text = File.Exists(resultFile) ? File.ReadAllText(resultFile) : null; }
        catch { return null; }
        // 状态、清单、备份必须属于同一次更新、同一个安装目录；对不上就什么都不做，绝不把别处的备份写进来
        var status = UpdateScript.ParseStatus(text);
        if (status.State is "applying" or "pending")
        {
            if (!File.Exists(manifestFile)) return null;
            if (UpdateScript.ApplyingPid(text) is int scriptPid && IsAlivePowerShell(scriptPid)) return null;
            if (status.UpdateId is not { } updateId) return null;
            if (!UpdateScript.ManifestMatches(manifestFile, updateId, appDir, backupDir)) return null;
            return UpdateScript.StartRecover(pid, appDir, backupDir, resultFile, manifestFile, updateId, exe, waitForPid);
        }
        if (status.State == UpdateScript.RollbackFailedState)
        {
            // 上次回滚失败但占用已解除：重新校验备份后再试一次；最多自动重试 3 次，超过就只在岛上报错
            if (UpdateScript.RollbackRetryCount(text) >= UpdateScript.MaxRollbackRetries) return null;
            if (status.UpdateId is not { } updateId) return null;
            if (!File.Exists(manifestFile)) return null;
            if (!UpdateScript.ManifestMatches(manifestFile, updateId, appDir, backupDir)) return null;
            return UpdateScript.StartRecover(pid, appDir, backupDir, resultFile, manifestFile, updateId, exe, waitForPid);
        }
        return null;
    }

    private static bool IsAlivePowerShell(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited && p.ProcessName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>每个安装目录一份独立的更新工作区（按目录路径的 SHA256 区分），两份安装互不共用状态、清单和备份。</summary>
    private static string UpdateTemp => UpdateTempFor(AppContext.BaseDirectory);

    public static string UpdateTempFor(string appDir)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            UpdateScript.NormalizeDir(appDir).ToUpperInvariant())))[..16].ToLowerInvariant();
        return Path.Combine(Path.GetTempPath(), "Qisland-update", key);
    }

    /// <summary>
    /// 发起新更新前的守卫：只要还有没解决的更新中断 / 回滚失败，就拒绝发起新更新，并保留更新工作区
    /// （目录、清单、备份都不能删）。拦下来时抛出 InvalidOperationException，界面按「更新没成功」提示。
    /// </summary>
    internal static void EnsureNoPendingRecovery(string resultFile, string tempDir)
    {
        string? text;
        try
        {
            text = File.ReadAllText(resultFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            text = null;   // 状态文件确实不存在：没有待恢复的事务，允许清理
        }
        catch (Exception ex)
        {
            // 状态文件在、但读不出来（被锁 / 权限 / IO 错误）：状态未知，绝不能当成“没有事务”去清理
            App.Log("读更新状态失败，已暂停更新: " + ex.Message);
            throw new InvalidOperationException("更新状态暂时无法读取，已暂停更新", ex);
        }

        var state = UpdateScript.ParseStatus(text).State;
        if (state is "applying" or "pending")
            throw new InvalidOperationException("上次更新中断尚未恢复，请重启 Qisland 完成恢复");
        if (state == UpdateScript.RollbackFailedState)
            throw new InvalidOperationException("上次更新回滚未完成，请重启 Qisland 完成恢复");
        // 只有已明确结束、不会再需要恢复资料的状态才允许清理；其余未知状态一律暂停更新
        if (text != null && state is not ("ok" or "rolled-back" or "backup-failed" or "candidate-invalid"))
            throw new InvalidOperationException("更新状态暂时无法读取，已暂停更新");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
    }

    /// <summary>App 启动时调用：发现中断的覆盖就拉起恢复脚本，返回 true 表示本进程应立即退出。</summary>
    public static bool RecoverInterruptedUpdate()
    {
        try
        {
            using var p = StartRecoveryIfInterrupted(ResultFile, Path.Combine(UpdateTemp, "manifest.json"),
                Path.Combine(UpdateTemp, "backup"), AppContext.BaseDirectory.TrimEnd('\\'), Environment.ProcessId);
            if (p == null) return false;
            App.Log($"检测到上次更新覆盖中断，已启动恢复脚本（PID {p.Id}）");
            return true;
        }
        catch (Exception ex) { App.Log("启动更新恢复失败: " + ex.Message); return false; }
    }

    private static string ResultFile => Path.Combine(UpdateTemp, "result.txt");

    private static string? ReadResultText()
    {
        try { return File.Exists(ResultFile) ? File.ReadAllText(ResultFile) : null; } catch { return null; }
    }

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
        var tmp = UpdateTemp;
        try
        {
            // 上次覆盖中断 / 上次回滚失败留下的清单和备份是恢复的唯一依据，恢复完成前不能清掉
            EnsureNoPendingRecovery(ResultFile, tmp);
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
            var targetVersion = UpdateScript.NormalizeVersion(r.Tag);
            UpdateScript.ValidateCandidate(from, targetVersion);

            var pid = Environment.ProcessId;
            App.Log($"更新 {Current} → {r.Tag}，覆盖 {appDir}");
            using var updater = UpdateScript.Start(pid, from, appDir, Path.Combine(tmp, "backup"), ResultFile,
                targetVersion, manifestFile: Path.Combine(tmp, "manifest.json"),
                readyFile: Path.Combine(tmp, "ready.txt"), updateId: UpdateScript.NewUpdateId());
            App.Log($"更新器已启动（PID {updater.Id}）");
            Progress(1, "马上重启…");
            await Task.Delay(600);
            App.Current.Dispatcher.Invoke(_quit);
        }
        catch (Exception ex)
        {
            App.Log("更新失败: " + ex);
            _busy = false;
            var reason = ex is InvalidDataException or InvalidOperationException
                ? ex.Message
                : "网络不通，点一下再试";
            App.Current.Dispatcher.BeginInvoke(() =>
                _store.Upsert(new IslandActivity(Id, "update", "更新没成功", reason, ActivityKind.Error, 60, DurationSeconds: 0), sticky: true, quiet: true));
        }
    }
}
