using System.Diagnostics;
using System.Text;

namespace Qisland.Core;

/// <summary>
/// 自动更新的替换脚本（PowerShell，-EncodedCommand 传入，不再经过 cmd 的代码页）：
/// 1. 等旧进程退出后，先把安装目录整份镜像备份；备份失败就不动安装目录，直接启动旧版并报错；
/// 2. 用 PowerShell 逐项覆盖成新版本；任何一项被锁 / 没复制完都算失败；
/// 3. 失败时用备份 /MIR 还原成旧版，启动旧版（--update-failed），结果写进 result 文件给旧版读出来提示。
/// 脚本内容用 UTF-16LE Base64 编码，中文路径不会受 cmd 代码页影响。
/// </summary>
public static class UpdateScript
{
    /// <summary>robocopy 退出码：0–7 是成功的各种组合（1 复制了文件、2 有多余文件…），8 及以上表示有文件没复制成功。</summary>
    public static bool RobocopyFailed(int code) => code >= 8;

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>生成可交给 powershell.exe -EncodedCommand 的脚本；路径作为 PowerShell 单引号字面量写入。</summary>
    public static string Build(int pid, string from, string appDir, string backupDir, string resultFile, string exe = "Qisland.exe")
    {
        from = from.TrimEnd('\\');
        appDir = appDir.TrimEnd('\\');
        backupDir = backupDir.TrimEnd('\\');
        return $$"""
$ErrorActionPreference = 'Stop'
$pidToWait = {{pid}}
$from = {{Ps(from)}}
$appDir = {{Ps(appDir)}}
$backupDir = {{Ps(backupDir)}}
$resultFile = {{Ps(resultFile)}}
$exeName = {{Ps(exe)}}
$common = @('/NFL', '/NDL', '/NJH', '/NJS', '/NP')

function Write-Result([string]$text) {
    try { [IO.File]::WriteAllText($resultFile, $text, [Text.UTF8Encoding]::new($false)) } catch { }
}
function Start-Old {
    try { Start-Process -FilePath (Join-Path $appDir $exeName) -ArgumentList '--update-failed' -WorkingDirectory $appDir | Out-Null } catch { }
}
function Start-New {
    try { Start-Process -FilePath (Join-Path $appDir $exeName) -ArgumentList '--updated' -WorkingDirectory $appDir | Out-Null } catch { }
}
function Invoke-Robocopy([string]$source, [string]$destination, [string[]]$options) {
    try {
        & robocopy $source $destination @options | Out-Null
        return $LASTEXITCODE
    } catch {
        return 16
    }
}

try {
    $old = [Diagnostics.Process]::GetProcessById($pidToWait)
    if (-not $old.WaitForExit(60000)) {
        Write-Result 'backup-failed old-process'
        exit 1
    }
} catch { }

try {
    if (Test-Path -LiteralPath $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
} catch {
    Write-Result 'backup-failed remove'
    Start-Old
    exit 1
}

$backupCode = Invoke-Robocopy $appDir $backupDir (@('/MIR', '/R:2', '/W:1') + $common)
if ($backupCode -ge 8) {
    Write-Result "backup-failed $backupCode"
    Start-Old
    exit 1
}

$copyError = $null
try {
    Get-ChildItem -LiteralPath $from -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force -ErrorAction Stop
    }
} catch {
    $copyError = $_.Exception.Message
}
if ($null -ne $copyError) {
    $restoreCode = Invoke-Robocopy $backupDir $appDir (@('/MIR', '/R:10', '/W:1') + $common)
    if ($restoreCode -ge 8) { Write-Result "rollback-failed copy $restoreCode" }
    else { Write-Result 'rolled-back copy' }
    Start-Old
    exit 1
}

Write-Result 'ok'
Start-New
exit 0
""";
    }

    /// <summary>Windows PowerShell 的 -EncodedCommand 要 UTF-16LE Base64；脚本里的中文路径因此不受代码页影响。</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>启动更新器；脚本内容走 -EncodedCommand，参数列表由 ProcessStartInfo.ArgumentList 传递。</summary>
    public static Process Start(int pid, string from, string appDir, string backupDir, string resultFile, string exe = "Qisland.exe")
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Encode(Build(pid, from, appDir, backupDir, resultFile, exe)));
        return Process.Start(psi)!;
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
