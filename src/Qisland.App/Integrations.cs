using System.IO;
using System.Text;
using System.Windows;
using Qisland.Core;

namespace Qisland.App;

/// <summary>
/// 把 Gemini CLI / Qwen Code / Cursor / OpenCode 接到岛上。每一个都要你在弹窗里点「确定」：
/// 弹窗写明改哪个文件、加什么；改前备份；记进改动账本；托盘里可以单独移除，也能一键还原。
/// 从不自动安装，从不碰 ~/.claude/settings.json，所有接入都只上报状态，不在岛上替它们批准任何操作。
/// </summary>
public static class Integrations
{
    /// <summary>测试用：把「用户目录」换成临时目录，测试绝不读写真实的 hook / 插件配置。</summary>
    internal static string? HomeOverride { get; set; }
    internal static string? ExeOverride { get; set; }
    internal static string Home => HomeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>确认弹窗（测试里换成假的）：返回 true 表示点了「确定」。</summary>
    internal static Func<string, string, bool> Confirm { get; set; } =
        (msg, title) => MessageBox.Show(msg, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
    internal static Action<string> Notify { get; set; } = msg => MessageBox.Show(msg, "Qisland");

    public static string FullPath(HookTarget t) => Path.Combine(Home, t.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public static bool Installed(HookTarget t)
    {
        try { return HookPlans.InstalledEvents(t, Read(FullPath(t))).Count > 0; } catch { return false; }
    }

    /// <summary>这个工具好像装了没有（看它的配置目录在不在）。</summary>
    public static bool AppPresent(HookTarget t) => Directory.Exists(Path.Combine(Home, t.RelativePath.Split('/')[0]));

    private static string Exe => ExeOverride ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Qisland.exe");

    public static void Install(HookTarget t)
    {
        if (t.RelativePath.Contains(".claude", StringComparison.OrdinalIgnoreCase)) return;   // 规则：不碰 Claude 的配置
        var path = FullPath(t);
        if (!AppPresent(t)) { Notify($"没找到 {t.App} 的配置目录（{Path.Combine(Home, t.RelativePath.Split('/')[0])}），好像还没装 {t.App}。装好、运行过一次以后再来接入。"); return; }
        string? existing; string next;
        try { existing = Read(path); next = HookPlans.Apply(t, existing, Exe); }
        catch (Exception ex) { Notify($"{path} 读不懂（{ex.Message}），为了不弄坏它，没有修改。"); return; }
        var comments = existing != null && (existing.Contains("//") && !existing.Contains("://") || existing.Contains("/*"));
        var msg = $"要把 {t.App} 接到灵动岛吗？\n\n{HookPlans.Describe(t, path, Exe)}\n\n{t.Note}。"
                  + (t.Experimental ? "\n（实验：不同版本的 " + t.App + " 可能不生效）" : "")
                  + (comments ? "\n注意：这个文件里的注释在重写后会丢失（备份里保留原样）。" : "")
                  + "\n\n这一项会记进「对其他软件的改动」，随时可以移除或一键还原。重启 " + t.App + " 后生效。";
        if (!Confirm(msg, "Qisland · 接入 " + t.App)) { App.Log($"接入 {t.App}: 你取消了"); return; }
        try
        {
            // 弹窗可能开了很久：确认后重新读文件，在最新内容上合并我们的 hook；提交前再核对版本，原子替换
            string? backup = null;
            if (!ConfigFile.Update(path, text => HookPlans.Apply(t, text, Exe), v => backup = Backup(path, v), out var err))
            {
                Notify($"{path} 在写入时被别的程序改动了，为了不覆盖它，没有修改（{err}）。");
                App.Log($"接入 {t.App}: 配置文件版本冲突，未写入");
                return;
            }
            ChangeLedger.Record(t.Id, t.App, path, t.Style == HookStyle.PluginFile ? "新建了 Qisland 插件文件" : $"hooks 里加了 {t.Events.Length} 个事件（{string.Join("、", t.Events)}）" + (backup != null ? "，备份 " + Path.GetFileName(backup) : ""));
            Notify($"已接入 {t.App}，重启它后生效。");
        }
        catch (Exception ex) { Notify($"写不进 {path}：{ex.Message}"); App.Log($"接入 {t.App} 失败: {ex.Message}"); }
    }

    /// <summary>移除我们加的那部分；ask=false 用于一键还原（那边已经整体确认过）。</summary>
    public static string? Remove(HookTarget t, bool ask)
    {
        var path = FullPath(t);
        var existing = Read(path);
        if (existing == null || HookPlans.InstalledEvents(t, existing).Count == 0) return null;
        if (ask && !Confirm($"从 {t.App} 移除 Qisland？\n\n{(t.Style == HookStyle.PluginFile ? "删除文件 " + path : "只删 " + path + " 里命令带 Qisland.exe 的 hook，其余不动")}\n改前备份。", "Qisland")) return null;
        // 确认后按最新内容重新计算；期间已经没有我们的条目就什么都不改
        string? backup = null;
        if (!ConfigFile.Update(path,
                text => HookPlans.InstalledEvents(t, text).Count == 0 ? text
                    : t.Style == HookStyle.PluginFile ? null : HookPlans.Remove(t, text, out _),
                v => backup = Backup(path, v), out var err))
        {
            var fail = $"{t.App}：{path} 在写入时被别的程序改动了，没有移除（{err}）";
            App.Log(fail);
            return fail;
        }
        var r = $"{t.App}：已移除 Qisland 的接入" + (backup != null ? $"（备份 {Path.GetFileName(backup)}）" : "");
        App.Log(r);
        ChangeLedger.Sync();
        return r;
    }

    /// <summary>按即将被替换的那一版内容备份（不是再去拷一次可能又变了的文件）。</summary>
    private static string? Backup(string path, ConfigVersion v)
    {
        if (v.Text == null) return null;
        var b = path + ".qisland-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.WriteAllText(b, v.Text, new UTF8Encoding(false));
        return b;
    }
}
