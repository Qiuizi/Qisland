# 把标准输入或 -Body 送到 Qisland。失败时写入 %APPDATA%\Qisland\inbox。
param(
    [string]$Body = "",
    [int]$Port = 0
)
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Body)) {
    $Body = [Console]::In.ReadToEnd()
}
if ([string]::IsNullOrWhiteSpace($Body) -and $args.Count -gt 0) {
    $Body = $args -join " "
}
$Body = $Body.Trim()
if ($Body.Length -eq 0) { exit 0 }
if ($Port -le 0) {
    $Port = 9840
    if ($env:QISLAND_PORT) { $Port = [int]$env:QISLAND_PORT }
}

$parsed = $null
try { $parsed = $Body | ConvertFrom-Json } catch { }
$path = "/v3/island/push"
if ($null -eq $parsed -or $null -eq $parsed.title -or $parsed.hook_event_name -or $parsed.source -or $parsed.kind -or $parsed.type) {
    if ($parsed.hook_event_name -or $parsed.source -or $parsed.kind -or ($parsed.type -and -not $parsed.title)) {
        $path = "/v1/qisland/ingest"
    } elseif ($parsed.title) {
        $path = "/v3/island/push"
    } else {
        $path = "/v1/qisland/ingest"
    }
}

$headers = @{ "Content-Type" = "application/json; charset=utf-8" }
if ($env:QISLAND_TOKEN) {
    $headers["X-Qisland-Token"] = $env:QISLAND_TOKEN
    $headers["X-WinIslands-Token"] = $env:QISLAND_TOKEN
}
$uri = "http://127.0.0.1:$Port$path"
try {
    Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($Body)) | Out-Null
    exit 0
} catch {
    $dir = Join-Path $env:APPDATA "Qisland\inbox"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $name = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + "-" + [guid]::NewGuid().ToString("N") + ".json"
    Set-Content -Path (Join-Path $dir $name) -Value $Body -Encoding utf8
    exit 0
}
