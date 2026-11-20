# 只给回归测试用的 WinIslands 旧版。正式版请用 release.ps1；产物和 Qisland.exe 明确分开。
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
$out = "dist-legacy-winislands"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src\WinIslands\WinIslands.csproj -c Release -r win-x64 --self-contained true `
  -p:EnableWindowsTargeting=true -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$exe = Join-Path $out "WinIslands.Legacy.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "旧版发布产物不是 WinIslands.Legacy.exe：$exe" }
Write-Host "LEGACY_WINISLANDS_PUBLISH_OK -> $exe"
