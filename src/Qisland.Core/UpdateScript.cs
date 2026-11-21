using System.Diagnostics;
using System.Text;

namespace Qisland.Core;

/// <summary>
/// 自动更新的替换脚本（PowerShell，-EncodedCommand 传入，不再经过 cmd 的代码页）：
/// 1. 等旧进程退出，校验候选包的入口、必需文件和 ProductVersion；不合格时不动安装目录；
/// 2. 生成“相对路径 + 新旧 SHA256”的变更清单，只备份会被改写或删除的文件；
/// 3. 逐项覆盖并校验；启动新进程前先写 pending，等新进程落就绪标记后才写 ok；
/// 4. 任何失败都按清单逐文件还原并校验 SHA256，成功才写 rolled-back，失败写 rollback-failed。
/// 脚本内容用 UTF-16LE Base64 编码，中文路径不会受 cmd 代码页影响。
/// </summary>
public static class UpdateScript
{
    public const int DefaultReadyTimeoutSeconds = 20;

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";

    public static string NormalizeVersion(string version)
    {
        var value = version.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var plus = value.IndexOf('+');
        return (plus >= 0 ? value[..plus] : value).Trim();
    }

    /// <summary>覆盖前校验候选安装包；失败时抛出 InvalidDataException，安装目录不应有任何改动。</summary>
    public static void ValidateCandidate(string from, string targetVersion, string exe = "Qisland.exe")
    {
        if (!Directory.Exists(from))
            throw new InvalidDataException("候选目录不存在");

        var entry = Path.Combine(from, exe);
        if (!File.Exists(entry))
            throw new InvalidDataException($"候选包缺少 {exe}");

        foreach (var name in RequiredFiles())
        {
            if (!File.Exists(Path.Combine(from, name)))
                throw new InvalidDataException($"候选包缺少 {name}");
        }

        var expected = NormalizeVersion(targetVersion);
        var actual = FileVersionInfo.GetVersionInfo(entry).ProductVersion?.Trim() ?? "";
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"候选包版本是 {actual}，要更新到 {expected}");
    }

    private static string[] RequiredFiles() =>
    [
        "Qisland.Core.dll",
        "Qisland.runtimeconfig.json",
        "Qisland.deps.json",
    ];

    /// <summary>生成可交给 powershell.exe -EncodedCommand 的脚本；路径作为 PowerShell 单引号字面量写入。</summary>
    public static string Build(
        int pid,
        string from,
        string appDir,
        string backupDir,
        string resultFile,
        string targetVersion,
        string exe = "Qisland.exe",
        string? manifestFile = null,
        string? readyFile = null,
        int readyTimeoutSeconds = DefaultReadyTimeoutSeconds)
    {
        from = Path.GetFullPath(from).TrimEnd('\\');
        appDir = Path.GetFullPath(appDir).TrimEnd('\\');
        backupDir = Path.GetFullPath(backupDir).TrimEnd('\\');
        resultFile = Path.GetFullPath(resultFile);
        var resultDir = Path.GetDirectoryName(resultFile)!;
        manifestFile ??= Path.Combine(resultDir, "manifest.json");
        readyFile ??= Path.Combine(resultDir, "ready.txt");
        manifestFile = Path.GetFullPath(manifestFile);
        readyFile = Path.GetFullPath(readyFile);
        targetVersion = NormalizeVersion(targetVersion);
        if (readyTimeoutSeconds < 1) readyTimeoutSeconds = 1;

        return $$"""
$ErrorActionPreference = 'Stop'
$pidToWait = {{pid}}
$from = {{Ps(from)}}
$appDir = {{Ps(appDir)}}
$backupDir = {{Ps(backupDir)}}
$resultFile = {{Ps(resultFile)}}
$manifestFile = {{Ps(manifestFile)}}
$readyFile = {{Ps(readyFile)}}
$expectedVersion = {{Ps(targetVersion)}}
$exeName = {{Ps(exe)}}
$entryExe = 'Qisland.exe'
$requiredFiles = @('Qisland.exe', 'Qisland.Core.dll', 'Qisland.runtimeconfig.json', 'Qisland.deps.json')
$readyTimeoutSeconds = {{readyTimeoutSeconds}}
$script:manifest = @()
$script:manifestReady = $false

function Write-Result([string]$text) {
    try {
        $dir = [IO.Path]::GetDirectoryName($resultFile)
        if ($dir) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
        [IO.File]::WriteAllText($resultFile, $text, [Text.UTF8Encoding]::new($false))
    } catch { }
}
function Write-HelperLog([string]$text) {
    try { [IO.File]::AppendAllText($resultFile + '.log', "$(Get-Date -Format o) $text`r`n", [Text.UTF8Encoding]::new($false)) } catch { }
}
function Start-Old {
    try {
        Remove-Item Env:QISLAND_UPDATE_READY -ErrorAction SilentlyContinue
        Start-Process -FilePath (Join-Path $appDir $exeName) -ArgumentList '--update-failed' -WorkingDirectory $appDir -ErrorAction Stop | Out-Null
    } catch { Write-HelperLog "start old failed: $($_.Exception.Message)" }
}
function Start-New {
    $dir = [IO.Path]::GetDirectoryName($readyFile)
    if ($dir) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
    Remove-Item -LiteralPath $readyFile -Force -ErrorAction SilentlyContinue
    $env:QISLAND_UPDATE_READY = $readyFile
    Start-Process -FilePath (Join-Path $appDir $exeName) -ArgumentList '--updated' -WorkingDirectory $appDir -PassThru -ErrorAction Stop
}
function Stop-New([Diagnostics.Process]$process) {
    if ($null -eq $process) { return }
    try {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit(3000) | Out-Null
        }
    } catch { Write-HelperLog "stop new failed: $($_.Exception.Message)" }
}
function Get-RelativePath([string]$root, [string]$path) {
    $base = [IO.Path]::GetFullPath($root).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($path)
    $prefix = $base + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "文件不在目录内：$full"
    }
    $full.Substring($prefix.Length)
}
function Get-Sha256([string]$path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        } finally { $stream.Dispose() }
    } finally { $sha.Dispose() }
}
function Get-FileMap([string]$root) {
    $map = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object {
        $map[(Get-RelativePath $root $_.FullName)] = $_.FullName
    }
    return ,$map
}
function Test-Content([string]$path, [string]$expected) {
    if (-not $expected) {
        return -not (Test-Path -LiteralPath $path -PathType Leaf)
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
    try { return (Get-Sha256 $path) -eq $expected } catch { return $false }
}
function Restore-Plan {
    $errors = [Collections.Generic.List[string]]::new()
    foreach ($entry in $script:manifest) {
        $target = Join-Path $appDir $entry.path
        if (Test-Content $target $entry.old) { continue }
        try {
            if ($entry.old) {
                $source = Join-Path $backupDir $entry.path
                $dir = [IO.Path]::GetDirectoryName($target)
                if ($dir) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
                Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
            } elseif (Test-Path -LiteralPath $target -PathType Leaf) {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            }
        } catch {
            $errors.Add("$($entry.path): $($_.Exception.Message)")
        }
    }
    foreach ($entry in $script:manifest) {
        $target = Join-Path $appDir $entry.path
        if (-not (Test-Content $target $entry.old)) {
            $errors.Add("$($entry.path): SHA256 不一致")
        }
    }
    return $errors.ToArray()
}
function Stop-WithResult([string]$text) {
    Write-Result $text
    Start-Old
    exit 1
}
function Rollback-And-StartOld([string]$reason) {
    $errors = @()
    if ($script:manifestReady) { $errors = @(Restore-Plan) }
    if ($errors.Count -eq 0) {
        Write-Result "rolled-back $reason"
    } else {
        Write-Result "rollback-failed $reason | $($errors -join '; ')"
    }
    Start-Old
    exit 1
}

trap {
    $reason = $_.Exception.Message
    if ($script:manifestReady) { Rollback-And-StartOld "exception $reason" }
    else { Stop-WithResult "backup-failed $reason" }
}

try {
    $old = [Diagnostics.Process]::GetProcessById($pidToWait)
    if (-not $old.WaitForExit(60000)) {
        Write-Result 'backup-failed old-process'
        exit 1
    }
} catch { }

try {
    foreach ($name in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $from $name) -PathType Leaf)) {
            throw "缺少 $name"
        }
    }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $from $entryExe)).ProductVersion
    if ($null -eq $actualVersion) { $actualVersion = '' }
    $actualVersion = $actualVersion.Trim()
    if ($actualVersion -ne $expectedVersion) {
        throw "版本是 $actualVersion，应为 $expectedVersion"
    }
} catch {
    Stop-WithResult "candidate-invalid $($_.Exception.Message)"
}

try {
    $candidate = Get-FileMap $from
    $installed = Get-FileMap $appDir
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $candidate.Keys) { $paths.Add($path) | Out-Null }
    foreach ($path in $installed.Keys) { $paths.Add($path) | Out-Null }

    foreach ($path in (@($paths) | Sort-Object)) {
        $oldHash = ''
        $newHash = ''
        if ($installed.ContainsKey($path)) { $oldHash = Get-Sha256 $installed[$path] }
        if ($candidate.ContainsKey($path)) { $newHash = Get-Sha256 $candidate[$path] }
        if ($oldHash -eq $newHash) { continue }
        $script:manifest += [pscustomobject]@{
            path = $path
            old = $oldHash
            new = $newHash
            action = $(if ($newHash) { 'copy' } else { 'delete' })
        }
    }

    $manifestDir = [IO.Path]::GetDirectoryName($manifestFile)
    if ($manifestDir) { [IO.Directory]::CreateDirectory($manifestDir) | Out-Null }
    [IO.File]::WriteAllText($manifestFile,
        (ConvertTo-Json -InputObject @($script:manifest) -Depth 4),
        [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction Stop
    }
    [IO.Directory]::CreateDirectory($backupDir) | Out-Null
    foreach ($entry in $script:manifest) {
        if (-not $entry.old) { continue }
        $source = Join-Path $appDir $entry.path
        $target = Join-Path $backupDir $entry.path
        $dir = [IO.Path]::GetDirectoryName($target)
        if ($dir) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
        Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
        if ((Get-Sha256 $target) -ne $entry.old) {
            throw "备份 SHA256 不一致：$($entry.path)"
        }
    }
    $script:manifestReady = $true
} catch {
    Stop-WithResult "backup-failed $($_.Exception.Message)"
}

$applyError = $null
try {
    foreach ($entry in $script:manifest) {
        $target = Join-Path $appDir $entry.path
        if ($entry.action -eq 'copy') {
            $source = Join-Path $from $entry.path
            $dir = [IO.Path]::GetDirectoryName($target)
            if ($dir) { [IO.Directory]::CreateDirectory($dir) | Out-Null }
            Copy-Item -LiteralPath $source -Destination $target -Force -ErrorAction Stop
            if ((Get-Sha256 $target) -ne $entry.new) {
                throw "覆盖后 SHA256 不一致：$($entry.path)"
            }
        } else {
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                Remove-Item -LiteralPath $target -Force -ErrorAction Stop
            }
            if (Test-Path -LiteralPath $target -PathType Leaf) {
                throw "删除后文件仍存在：$($entry.path)"
            }
        }
    }
} catch {
    $applyError = $_.Exception.Message
}
if ($null -ne $applyError) {
    Rollback-And-StartOld "apply $applyError"
}

foreach ($entry in $script:manifest) {
    $target = Join-Path $appDir $entry.path
    if (-not (Test-Content $target $entry.new)) {
        Rollback-And-StartOld "verify $($entry.path)"
    }
}

$newProcess = $null
try {
    Write-Result 'pending'
    $newProcess = Start-New
} catch {
    Rollback-And-StartOld "start $($_.Exception.Message)"
}

$ready = $false
$deadline = [DateTime]::UtcNow.AddSeconds($readyTimeoutSeconds)
while ([DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath $readyFile -PathType Leaf) {
        $ready = $true
        break
    }
    try {
        if ($newProcess.HasExited) { break }
    } catch { break }
    Start-Sleep -Milliseconds 200
}
if (-not $ready) {
    Stop-New $newProcess
    Rollback-And-StartOld "not-ready"
}

Start-Sleep -Milliseconds 300
try {
    if ($newProcess.HasExited) {
        Rollback-And-StartOld "new-process-exited"
    }
} catch {
    Stop-New $newProcess
    Rollback-And-StartOld "new-process-unknown"
}

Write-Result 'ok'
exit 0
""";
    }

    /// <summary>Windows PowerShell 的 -EncodedCommand 要 UTF-16LE Base64；脚本里的中文路径因此不受代码页影响。</summary>
    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>启动更新器；脚本内容走 -EncodedCommand，参数列表由 ProcessStartInfo.ArgumentList 传递。</summary>
    public static Process Start(
        int pid,
        string from,
        string appDir,
        string backupDir,
        string resultFile,
        string targetVersion,
        string exe = "Qisland.exe",
        string? manifestFile = null,
        string? readyFile = null,
        int readyTimeoutSeconds = DefaultReadyTimeoutSeconds)
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
        psi.ArgumentList.Add(Encode(Build(pid, from, appDir, backupDir, resultFile, targetVersion,
            exe, manifestFile, readyFile, readyTimeoutSeconds)));
        return Process.Start(psi)!;
    }

    public readonly record struct Result(bool Ok, string State, string Detail);

    /// <summary>读 result 文件：ok / rolled-back / rollback-failed / backup-failed / candidate-invalid。</summary>
    public static Result ParseResult(string? text)
    {
        var t = (text ?? "").Trim();
        if (t == "ok") return new(true, "", "");
        var sp = t.IndexOf(' ');
        var state = sp > 0 ? t[..sp] : t;
        var detail = sp > 0 ? t[(sp + 1)..].Trim() : "";
        return state is "rolled-back" or "rollback-failed" or "backup-failed" or "candidate-invalid"
            ? new(false, state, detail)
            : new(false, "unknown", t);
    }

    /// <summary>给用户看的一句话。</summary>
    public static string Describe(Result r) => r.State switch
    {
        "candidate-invalid" => $"更新包不完整或版本不符（{r.Detail}），没有改动安装目录",
        "rolled-back" => $"更新没有成功（{r.Detail}），已按变更清单逐文件还原并通过 SHA256 校验",
        "rollback-failed" => $"更新失败，且部分旧文件无法按 SHA256 校验还原（{r.Detail}），请从官网重新下载覆盖安装",
        "backup-failed" => $"备份旧版本失败（{r.Detail}），没有改动安装目录",
        _ => "更新脚本没有正常结束，仍在运行原来的版本",
    };
}
