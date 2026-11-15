# 打出 win-x64 自包含目录 dist\。成功时打印 PUBLISH_OK。
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish src\WinIslands\WinIslands.csproj -c Release -r win-x64 --self-contained true `
  -p:EnableWindowsTargeting=true -o dist
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "PUBLISH_OK"
