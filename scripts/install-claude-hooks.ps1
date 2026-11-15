# 把 Claude Code hooks 写进 %USERPROFILE%\.claude\settings.json。已有同一命令则跳过。
$ErrorActionPreference = "Stop"
$hook = (Resolve-Path (Join-Path $PSScriptRoot "hooks\claude-hook.ps1")).Path
$cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$hook`""
$dir = Join-Path $env:USERPROFILE ".claude"
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$path = Join-Path $dir "settings.json"
$events = @("Notification", "PermissionRequest", "Stop", "PostToolUseFailure", "UserPromptSubmit", "SessionStart", "SessionEnd")

if (Test-Path $path) {
    $obj = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    $obj = [pscustomobject]@{}
}
if (-not ($obj.PSObject.Properties.Name -contains "hooks")) {
    $obj | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}
foreach ($event in $events) {
    $entry = [pscustomobject]@{ hooks = @([pscustomobject]@{ type = "command"; command = $cmd }) }
    $names = @($obj.hooks.PSObject.Properties.Name)
    if ($names -notcontains $event) {
        $obj.hooks | Add-Member -NotePropertyName $event -NotePropertyValue @($entry)
        continue
    }
    $current = $obj.hooks.$event | ConvertTo-Json -Depth 8 -Compress
    if ($current -like "*$hook*") { continue }
    $merged = @($obj.hooks.$event) + $entry
    $obj.hooks.PSObject.Properties.Remove($event)
    $obj.hooks | Add-Member -NotePropertyName $event -NotePropertyValue $merged
}
$obj | ConvertTo-Json -Depth 20 | Set-Content -Path $path -Encoding UTF8
Write-Host "Claude hooks 已写入 $path"
