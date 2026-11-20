# Claude Code 的官方 hook：把标准输入原样交给 qisland-push.ps1。
$ErrorActionPreference = "Stop"
$push = Join-Path $PSScriptRoot "..\qisland-push.ps1"
$raw = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($raw) -and $args.Count -gt 0) { $raw = $args -join " " }
& $push -Body $raw -Type hook
exit $LASTEXITCODE
