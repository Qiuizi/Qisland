namespace Qisland.Core;

/// <summary>
/// 自动更新的替换脚本（cmd）。岛退出后由它把新版本复制进安装目录：
/// 1. 先把安装目录整份镜像备份；备份失败就不动安装目录，直接启动旧版并报错；
/// 2. robocopy 新版本过去，退出码 ≥ 8 视为失败（有文件被锁、没复制完，新旧混装）；
/// 3. 失败时用备份 /MIR 还原成旧版，启动旧版（--update-failed），结果写进 result 文件给旧版读出来提示。
/// 纯文本生成，可在 Linux 上单测。
/// </summary>
public static class UpdateScript
{
    /// <summary>robocopy 退出码：0–7 是成功的各种组合（1 复制了文件、2 有多余文件…），8 及以上表示有文件没复制成功。</summary>
    public static bool RobocopyFailed(int code) => code >= 8;

    private const string Quiet = "/NFL /NDL /NJH /NJS /NP";

    public static string Build(int pid, string from, string appDir, string backupDir, string resultFile, string exe = "Qisland.exe")
    {
        appDir = appDir.TrimEnd('\\'); from = from.TrimEnd('\\'); backupDir = backupDir.TrimEnd('\\');
        var nl = "\r\n";
        // 注意：结果用「> 文件 echo …」前置重定向，免得「rolled-back 9>」里的 9> 被当成句柄 9 的重定向
        return
            "@echo off" + nl +
            "setlocal" + nl +
            ":wait" + nl +
            $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)" + nl +
            $"if exist \"{backupDir}\" rmdir /s /q \"{backupDir}\"" + nl +
            $"robocopy \"{appDir}\" \"{backupDir}\" /MIR /R:2 /W:1 {Quiet} >nul" + nl +
            "if errorlevel 8 goto nobackup" + nl +
            $"robocopy \"{from}\" \"{appDir}\" /E /R:5 /W:1 {Quiet} >nul" + nl +
            "if errorlevel 8 goto rollback" + nl +
            $"> \"{resultFile}\" echo ok" + nl +
            $"start \"\" \"{appDir}\\{exe}\" --updated" + nl +
            "exit /b 0" + nl +
            ":nobackup" + nl +
            "set RC=%errorlevel%" + nl +
            $"> \"{resultFile}\" echo backup-failed %RC%" + nl +
            "goto startold" + nl +
            ":rollback" + nl +
            "set RC=%errorlevel%" + nl +
            $"robocopy \"{backupDir}\" \"{appDir}\" /MIR /R:10 /W:1 {Quiet} >nul" + nl +
            "if errorlevel 8 goto rbfail" + nl +
            $"> \"{resultFile}\" echo rolled-back %RC%" + nl +
            "goto startold" + nl +
            ":rbfail" + nl +
            $"> \"{resultFile}\" echo rollback-failed %RC% %errorlevel%" + nl +
            ":startold" + nl +
            $"start \"\" \"{appDir}\\{exe}\" --update-failed" + nl +
            "exit /b 1" + nl;
    }

    public readonly record struct Result(bool Ok, string State, string Detail);

    /// <summary>读 result 文件：ok / rolled-back &lt;码&gt; / rollback-failed &lt;码&gt; &lt;码&gt; / backup-failed &lt;码&gt;。</summary>
    public static Result ParseResult(string? text)
    {
        var t = (text ?? "").Trim();
        if (t == "ok") return new(true, "", "");
        var sp = t.IndexOf(' ');
        var state = sp > 0 ? t[..sp] : t;
        var detail = sp > 0 ? t[(sp + 1)..].Trim() : "";
        return state is "rolled-back" or "rollback-failed" or "backup-failed" ? new(false, state, detail) : new(false, "unknown", t);
    }

    /// <summary>给用户看的一句话。</summary>
    public static string Describe(Result r) => r.State switch
    {
        "rolled-back" => $"有文件被占用没能替换（robocopy {r.Detail}），已还原成原来的版本",
        "rollback-failed" => $"替换失败且还原也没完成（robocopy {r.Detail}），请重新下载安装包覆盖",
        "backup-failed" => $"备份旧版本失败（robocopy {r.Detail}），没有改动安装目录",
        _ => "更新脚本没有正常结束，仍在运行原来的版本",
    };
}
