# Codex 的 notify：JSON 在最后一个参数，有时在标准输入。
$ErrorActionPreference = "Stop"
$push = Join-Path $PSScriptRoot "..\qisland-push.ps1"
$raw = ""
if ($args.Count -gt 0) { $raw = $args[$args.Count - 1] }
if ([string]::IsNullOrWhiteSpace($raw) -or -not $raw.TrimStart().StartsWith("{")) {
    $stdin = [Console]::In.ReadToEnd()
    if (-not [string]::IsNullOrWhiteSpace($stdin)) { $raw = $stdin }
}
& $push -Body $raw
exit $LASTEXITCODE
