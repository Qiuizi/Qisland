# 把标准输入或 -Body 送到 Qisland。失败时写入 %APPDATA%\Qisland\inbox。
# 只投一个入口；消息带上 $type，由 Qisland 按类别解析，脚本不再自己挑接口。
param(
    [string]$Body = "",
    [int]$Port = 0,
    [ValidateSet("push", "ingest", "hook")]
    [string]$Type = "push"
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
if ($Body.StartsWith("{") -and $Body -notmatch '"$type"\s*:') {
    $Body = '{"$type":"' + $Type + '",' + $Body.Substring(1)
}
if ($Port -le 0) {
    $Port = 9840
    if ($env:QISLAND_PORT) { $Port = [int]$env:QISLAND_PORT }
}

$headers = @{ "Content-Type" = "application/json; charset=utf-8" }
if ($env:QISLAND_TOKEN) {
    $headers["X-Qisland-Token"] = $env:QISLAND_TOKEN
    $headers["X-WinIslands-Token"] = $env:QISLAND_TOKEN
}
$uri = "http://127.0.0.1:$Port/v1/qisland/ingest"
try {
    Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($Body)) | Out-Null
    exit 0
} catch {
    $dir = Join-Path $env:APPDATA "Qisland\inbox"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $name = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString() + "-" + [guid]::NewGuid().ToString("N") + ".json"
    [IO.File]::WriteAllText((Join-Path $dir $name), $Body, (New-Object Text.UTF8Encoding $false))
    exit 0
}
