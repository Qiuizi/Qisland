using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Qisland.Core;
using ActivityKind = Qisland.Core.ActivityKind;

namespace Qisland.App;

/// <summary>
/// 系统里的「小事」：插拔电源、低电量、充满；资源管理器复制进度；浏览器下载进度。
/// 全部在 UI 线程每秒 Tick 一次，开销是几次 Win32 调用 + 一次目录枚举。
/// </summary>
public sealed partial class SystemWatch
{
    private readonly ActivityStore _store;
    public SystemWatch(ActivityStore store) { _store = store; _downloads = DownloadsFolder(); }

    public void Tick(int n)
    {
        try { Power(); } catch (Exception ex) { Fail("电源", ex); }
        try { Copy(); } catch (Exception ex) { Fail("复制", ex); }
        try { Downloads(); } catch (Exception ex) { Fail("下载", ex); }
    }

    private readonly HashSet<string> _failed = new();
    private void Fail(string what, Exception ex) { if (_failed.Add(what)) App.Log($"{what}监听出错: {ex.Message}"); }

    /// <summary>点一下：下载完成打开所在文件夹并选中文件。</summary>
    public bool Open(LiveActivity a)
    {
        if (a.Id.StartsWith("download:done:", StringComparison.Ordinal))
        {
            var path = a.Id["download:done:".Length..];
            if (File.Exists(path)) { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); _store.Remove(a.Id); return true; }
        }
        if (a.Source == "download" && Directory.Exists(_downloads)) { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_downloads}\"") { UseShellExecute = true }); return true; }
        return false;
    }

    // ───────── 电源 ─────────
    private System.Windows.Forms.PowerLineStatus? _line;
    private int _lastPct = -1;
    private bool _warned20, _warned10, _full;

    private void Power()
    {
        var ps = System.Windows.Forms.SystemInformation.PowerStatus;
        if (ps.BatteryChargeStatus.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)) return;
        var pct = (int)Math.Round(ps.BatteryLifePercent * 100);
        var line = ps.PowerLineStatus;
        if (_line == null) { _line = line; _lastPct = pct; _full = pct >= 100; return; }

        if (line != _line)
        {
            _line = line;
            if (line == System.Windows.Forms.PowerLineStatus.Online)
            {
                _warned20 = _warned10 = false;
                _store.Upsert(new IslandActivity("power", "battery", "正在充电", $"{pct}%", ActivityKind.Done, 70, DurationSeconds: 4, ResetTtl: true), progress: pct / 100.0);
            }
            else
            {
                var left = ps.BatteryLifeRemaining > 0 ? $" · 约 {TimeSpan.FromSeconds(ps.BatteryLifeRemaining):h\\:mm}" : "";
                _store.Upsert(new IslandActivity("power", "battery", "已断开电源", $"{pct}%{left}", ActivityKind.Info, 70, DurationSeconds: 4, ResetTtl: true), progress: pct / 100.0);
            }
        }
        if (line != System.Windows.Forms.PowerLineStatus.Online)
        {
            if (pct <= 10 && !_warned10) { _warned10 = _warned20 = true; _store.Upsert(new IslandActivity("power", "battery-low", "电量严重不足", $"{pct}% · 请接上电源", ActivityKind.Error, 92, DurationSeconds: 12, ResetTtl: true), progress: pct / 100.0); }
            else if (pct <= 20 && !_warned20) { _warned20 = true; _store.Upsert(new IslandActivity("power", "battery-low", "电量低", $"{pct}%", ActivityKind.Warning, 85, DurationSeconds: 8, ResetTtl: true), progress: pct / 100.0); }
        }
        else if (pct >= 100 && !_full && _lastPct < 100)
            _store.Upsert(new IslandActivity("power", "battery", "已充满", "可以拔掉电源了", ActivityKind.Done, 60, DurationSeconds: 5, ResetTtl: true), progress: 1);
        _full = pct >= 100;
        _lastPct = pct;
    }

    // ───────── 资源管理器复制 / 移动 / 删除 ─────────
    private DateTime _copySeen = DateTime.MinValue;
    private bool _copying;

    private void Copy()
    {
        var (found, title) = FindOperationWindow();
        if (found)
        {
            _copySeen = DateTime.Now;
            _copying = true;
            var m = Pct().Match(title);
            double? p = m.Success ? int.Parse(m.Groups[1].Value) / 100.0 : null;
            var verb = title.Contains("删除") || title.Contains("Delet", StringComparison.OrdinalIgnoreCase) ? "正在删除"
                     : title.Contains("移动") || title.Contains("Mov", StringComparison.OrdinalIgnoreCase) ? "正在移动" : "正在复制";
            _store.Upsert(new IslandActivity("filecopy", "filecopy", m.Success ? $"{verb} · {m.Groups[1].Value}%" : verb, "资源管理器", ActivityKind.Running, 64), sticky: true, progress: p);
        }
        else if (_copying && DateTime.Now - _copySeen > TimeSpan.FromSeconds(1.5))
        {
            _copying = false;
            _store.Remove("filecopy");
            _store.Upsert(new IslandActivity("filecopy:done", "filecopy", "文件操作完成", "", ActivityKind.Done, 60, DurationSeconds: 3, ResetTtl: true));
        }
    }

    [GeneratedRegex(@"(\d{1,3})\s*%")] private static partial Regex Pct();

    private static (bool, string) FindOperationWindow()
    {
        var h = FindWindow("OperationStatusWindow", null);
        while (h != IntPtr.Zero)
        {
            if (IsWindowVisible(h))
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                return (true, sb.ToString());
            }
            h = FindWindowEx(IntPtr.Zero, h, "OperationStatusWindow", null);
        }
        return (false, "");
    }

    // ───────── 浏览器下载 ─────────
    private readonly string _downloads;
    private readonly Dictionary<string, (long size, DateTime t, double speed)> _dl = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Partial = [".crdownload", ".part", ".downloading", ".tmp.download", ".xltd", ".bc!"];

    private void Downloads()
    {
        if (!Directory.Exists(_downloads)) return;
        var now = DateTime.Now;
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(_downloads))
        {
            var ext = Partial.FirstOrDefault(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (ext == null) continue;
            long size;
            try { size = new FileInfo(f).Length; } catch { continue; }
            live.Add(f);
            var name = FinalName(Path.GetFileName(f)[..^ext.Length]);
            if (_dl.TryGetValue(f, out var prev))
            {
                var dt = (now - prev.t).TotalSeconds;
                var inst = dt > 0 ? (size - prev.size) / dt : 0;
                var speed = prev.speed <= 0 ? inst : prev.speed * 0.6 + inst * 0.4;
                _dl[f] = (size, now, speed);
                if (size == 0 && speed <= 0) continue;   // Chrome 刚建的占位文件
                _store.Upsert(new IslandActivity("download:" + f, "download", name, $"{Mb(size)} · {Mb((long)Math.Max(0, speed))}/s", ActivityKind.Running, 62), sticky: true, progress: null);
            }
            else _dl[f] = (size, now, 0);
        }
        foreach (var gone in _dl.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _dl.Remove(gone);
            _store.Remove("download:" + gone);
            var ext = Partial.First(e => gone.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            var final = gone[..^ext.Length];
            // Chrome：xxx.crdownload → xxx；Edge 的 “未确认 123.crdownload” 会被改成真实文件名，取最近写入的文件
            if (!File.Exists(final))
                final = new DirectoryInfo(_downloads).EnumerateFiles().Where(i => !Partial.Any(e => i.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(i => i.LastWriteTime).FirstOrDefault(i => now - i.LastWriteTime < TimeSpan.FromSeconds(5))?.FullName ?? "";
            if (final.Length > 0 && File.Exists(final))
                _store.Upsert(new IslandActivity("download:done:" + final, "download", "下载完成", Path.GetFileName(final), ActivityKind.Done, 66, DurationSeconds: 8, ResetTtl: true));
        }
    }

    private static string FinalName(string n) => n.StartsWith("未确认 ") || n.StartsWith("Unconfirmed ") ? "正在下载" : n;
    private static string Mb(long b) => b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0.0} MB" : $"{Math.Max(0, b) / 1024.0:0} KB";

    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
            {
                var s = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (s.Length > 0) return s;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
}
