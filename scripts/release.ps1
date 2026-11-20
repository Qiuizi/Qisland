<#
一键发版：跑全部测试 → 改版本号 → 发布并校验产物 → 打包 → 提交并打标签 → 推到 GitHub → 建 Release。
岛上的自动更新会在 6 小时内（或托盘「检查更新」时）发现它。

用法（在仓库根目录）：
  .\scripts\release.ps1 1.0.1 "修好了天气卡片；组件页更顺滑"
  .\scripts\release.ps1 1.1.0-preview.2 -Notes "试验功能"     # 带 - 的版本会标成预发布
  .\scripts\release.ps1 1.0.1 -NotesFile notes.md               # 长更新说明放文件里
  .\scripts\release.ps1 1.0.1 "..." -DryRun                      # 只打包，不提交不推送
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [Parameter(Position = 1)][string]$Notes = "",
    [string]$NotesFile = "",
    [string]$Remote = "github",
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z\.]+)?$') { throw "版本号要像 1.0.1 或 1.1.0-preview.2，收到的是 $Version" }
$tag = "v$Version"
$pre = $Version.Contains('-')
if (git tag -l $tag) { throw "标签 $tag 已经存在" }
if ($NotesFile) { $Notes = Get-Content $NotesFile -Raw -Encoding UTF8 }
if (-not $Notes) { $Notes = (git log --pretty="- %s" "$(git describe --tags --abbrev=0 2>$null)..HEAD" 2>$null) -join "`n" }
if (-not $Notes) { $Notes = "Qisland $tag" }

# 1. 发布前先跑全部测试；任何一项失败都不改版本、不打包
Write-Host "运行全部测试…" -ForegroundColor Cyan
dotnet test Qisland.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "测试失败，已停止发版" }

# 2. 版本号写进 csproj
$csproj = "src\Qisland.App\Qisland.App.csproj"
$core = ($Version -split '-')[0]
$x = Get-Content $csproj -Raw -Encoding UTF8
$x = $x -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
$x = $x -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$core.0</FileVersion>"
$x = $x -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
[IO.File]::WriteAllText((Resolve-Path $csproj), $x, (New-Object Text.UTF8Encoding $false))
Write-Host "版本号 → $Version" -ForegroundColor Cyan

# 3. 发布（依赖框架，体积小；用户需要 .NET 8 桌面运行时）。ContinuousIntegrationBuild：dll / pdb 里的源码路径记成 /_/，不带本机目录
$out = "publish\Qisland"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "发布失败" }

# 产物必须来自 Qisland.App，且版本与 csproj 一致；旧 WinIslands 即使放错位置也会在这里被拦下
$exe = Join-Path $out "Qisland.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "发布产物缺少 Qisland.exe：$exe" }
$projectXml = [xml](Get-Content $csproj -Raw -Encoding UTF8)
$declaredVersion = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0].Trim()
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
if ($info.ProductVersion -ne $declaredVersion) {
    throw "产物版本 $($info.ProductVersion) 与 csproj 的 $declaredVersion 不一致"
}
if ($info.CompanyName -ne "Qisland.App") {
    throw "产物入口是 $($info.CompanyName)，不是 Qisland.App"
}
Write-Host "产物校验通过：Qisland.App v$($info.ProductVersion)" -ForegroundColor Green

# 4. 打包
New-Item -ItemType Directory -Force releases | Out-Null
$zip = "releases\Qisland-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host ("打包 → {0}（{1:0.0} MB）" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Cyan
if ($DryRun) { Write-Host "DryRun：到此为止" -ForegroundColor Yellow; return }

# 5. 提交、打标签、推送
git add -A
git commit -qm "发布 $tag" 2>$null
git tag -a $tag -m "Qisland $tag"
git -c http.postBuffer=524288000 -c http.version=HTTP/1.1 -c credential.helper= -c "credential.helper=!gh auth git-credential" push $Remote HEAD:main --follow-tags
if ($LASTEXITCODE -ne 0) { throw "推送失败" }

# 6. 建 Release，附上压缩包
$ghArgs = @("release", "create", $tag, $zip, "--title", "Qisland $tag", "--notes", $Notes, "--verify-tag")
if ($pre) { $ghArgs += "--prerelease" } else { $ghArgs += "--latest" }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "创建 Release 失败" }
Write-Host "完成：https://github.com/Qiuizi/Qisland/releases/tag/$tag" -ForegroundColor Green
