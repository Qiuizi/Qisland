# 给 Codex 的 config.toml 加上 notify。已经有 notify 时不覆盖。
$ErrorActionPreference = "Stop"
$hook = (Resolve-Path (Join-Path $PSScriptRoot "hooks\codex-notify.ps1")).Path
$dir = Join-Path $env:USERPROFILE ".codex"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$path = Join-Path $dir "config.toml"
$line = "notify = [`"powershell.exe`", `"-NoProfile`", `"-ExecutionPolicy`", `"Bypass`", `"-File`", `"$hook`"]"
if (Test-Path $path) {
    $text = Get-Content $path -Raw -Encoding UTF8
    if ($text -match '(?m)^\s*notify\s*=') {
        Write-Host "config.toml 里已经有 notify，没有改它。若要交给 Qisland，把命令换成："
        Write-Host $line
        exit 0
    }
    Add-Content -Path $path -Value "`n$line`n" -Encoding UTF8
} else {
    Set-Content -Path $path -Value "$line`n" -Encoding UTF8
}
Write-Host "Codex notify 已写入 $path"
