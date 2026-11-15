using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
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

    private sealed record Release(string Tag, string Name, string Notes, string Url, string? ZipUrl, long ZipSize, bool Pre);

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
            await Check(manual: false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    /// <summary>manual：托盘里点的「检查更新」，没有新版本也要说一声。</summary>
    public async Task Check(bool manual)
    {
        if (_busy) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=15");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) { App.Log($"检查更新: HTTP {(int)resp.StatusCode}"); if (manual) Say("检查更新失败", $"GitHub 返回 {(int)resp.StatusCode}", ActivityKind.Warning); return; }
            var best = Pick(await resp.Content.ReadAsStringAsync(cts.Token), Current, includePre: Current.Contains('-'));
            if (best == null) { App.Log($"检查更新: 已是最新 {Current}"); if (manual) Say("已是最新版本", "v" + Current, ActivityKind.Done); return; }
            if (!manual && File.Exists(SkipFile) && File.ReadAllText(SkipFile).Trim() == best.Tag) return;
            _found = best;
            App.Log($"发现新版本 {best.Tag}");
            var body = FirstLine(best.Notes);
            _store.Upsert(new IslandActivity(Id, "update", $"Qisland {best.Tag} 可以更新了", body.Length > 0 ? $"点一下更新 · {body}" : "点一下下载并重启", ActivityKind.Info, 58, DurationSeconds: 0), sticky: true);
        }
        catch (Exception ex) { App.Log("检查更新: " + ex.Message); if (manual) Say("检查更新失败", "网络不通或 GitHub 暂时不可用", ActivityKind.Warning); }
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
            string? zip = null; long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var n = a.GetProperty("name").GetString() ?? "";
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (zip == null || n.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
                { zip = a.GetProperty("browser_download_url").GetString(); size = a.GetProperty("size").GetInt64(); }
            }
            best = new Release(tag, r.GetProperty("name").GetString() ?? tag, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.GetProperty("html_url").GetString() ?? "", zip, size, pre);
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

            Progress(0, "下载中…");
            using (var resp = await _http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? r.ZipSize;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zip);
                var buf = new byte[81920];
                long got = 0; var last = DateTime.MinValue;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    got += n;
                    if ((DateTime.Now - last).TotalMilliseconds > 150 && total > 0)
                    {
                        last = DateTime.Now;
                        Progress(0.9 * got / total, $"{got / 1048576.0:0.0} / {total / 1048576.0:0.0} MB");
                    }
                }
            }
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
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                $"robocopy \"{from}\" \"{appDir}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                $"start \"\" \"{appDir}\\Qisland.exe\" --updated\r\n", new UTF8Encoding(false));
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
                _store.Upsert(new IslandActivity(Id, "update", "更新没成功", "点一下去 GitHub 手动下载", ActivityKind.Error, 60, DurationSeconds: 0), sticky: true));
            _found = _found! with { ZipUrl = null };
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
