# 构建 Qisland。成功时打印 BUILD_OK。
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build Qisland.sln -c Release -p:EnableWindowsTargeting=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "BUILD_OK"
