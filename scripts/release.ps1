<#
一键发版：核对工作区和提交 → 改版本号 → 跑全部测试 → 发布并校验产物 → 打包 → 只提交版本号文件并打标签 → 推到 GitHub → 建 Release。
岛上的自动更新会在 6 小时内（或托盘「检查更新」时）发现它。

用法（在仓库根目录，工作区必须干净，-ExpectedCommit 填你确认过要发的那个提交）：
  .\scripts\release.ps1 1.0.1 "修好了天气卡片；组件页更顺滑" -ExpectedCommit 1a2b3c4
  .\scripts\release.ps1 1.1.0-preview.2 -Notes "试验功能" -ExpectedCommit 1a2b3c4   # 带 - 的版本会标成预发布
  .\scripts\release.ps1 1.0.1 -NotesFile notes.md -ExpectedCommit 1a2b3c4             # 长更新说明放文件里
  .\scripts\release.ps1 1.0.1 "..." -ExpectedCommit 1a2b3c4 -DryRun                    # 只打包，不提交不推送，结束后还原版本号

安全约定：
- 开始前工作区必须干净、HEAD 必须正好是 -ExpectedCommit，否则什么都不改；
- 先写版本号再跑测试（测的就是要发的版本）；测试 / 发布 / 校验任何一步失败都把 csproj 还原；
- 只显式暂存 csproj，提交后核对父提交和改动文件；每条 git 命令都检查退出码；
- HEAD 上的 csproj 已经是目标版本（版本号单独提交过）时，不写文件、不新建提交，核对后直接给这个 HEAD 打标签发布；
  -DryRun 也走同一个判断和核对，只是在提交 / 打标签之前停下。
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Version,
    [Parameter(Position = 1)][string]$Notes = "",
    [string]$NotesFile = "",
    [string]$Remote = "github",
    [Parameter(Mandatory = $true)][string]$ExpectedCommit,
    [switch]$DryRun
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Invoke-Git、Get-VersionPlan、Assert-ReleaseFromHead 等纯函数在 release-lib.ps1 里（有独立的行为测试）
. (Join-Path $PSScriptRoot 'release-lib.ps1')

$Version = $Version.TrimStart('v')
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z\.]+)?$') { throw "版本号要像 1.0.1 或 1.1.0-preview.2，收到的是 $Version" }
$tag = "v$Version"
$pre = $Version.Contains('-')
$csproj = "src\Qisland.App\Qisland.App.csproj"
$csprojGit = "src/Qisland.App/Qisland.App.csproj"

# 0. 发版前核对：工作区干净、HEAD 正是预期的提交、标签还没用过。任何一项不对都不动文件
$dirty = Invoke-Git status --porcelain
if ($dirty) { throw "工作区不干净，先提交或清理：`n$($dirty -join "`n")" }
$expected = (Invoke-Git rev-parse --verify "$ExpectedCommit^{commit}" | Select-Object -First 1).Trim()
$head = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()
if ($head -ne $expected) { throw "HEAD 是 $head，不是预期的 $expected，已停止发版" }
if (Invoke-Git tag -l $tag) { throw "标签 $tag 已经存在" }
if ($NotesFile) { $Notes = Get-Content $NotesFile -Raw -Encoding UTF8 }
if (-not $Notes) {
    $since = $null
    try { $since = (Invoke-Git describe --tags --abbrev=0 | Select-Object -First 1) } catch { $since = $null }   # 没有任何标签时 describe 会失败
    $range = if ($since) { "$since..HEAD" } else { "HEAD" }
    $Notes = (Invoke-Git log --pretty="- %s" $range) -join "`n"
}
if (-not $Notes) { $Notes = "Qisland $tag" }

$original = [IO.File]::ReadAllText((Resolve-Path $csproj))
function Restore-Csproj {
    if ($plan -ne 'write') { return }   # 版本号已提交的分支从没改过 csproj，不需要还原
    [IO.File]::WriteAllText((Resolve-Path $csproj), $original, (New-Object Text.UTF8Encoding $false))
    Write-Host "已还原 $csproj" -ForegroundColor Yellow
}

# 0.5 判断版本号是否已经提交在 HEAD 上
$plan = Get-VersionPlan $original $Version
if ($plan -eq 'committed') {
    Assert-ReleaseFromHead $csprojGit $Version $expected
    Write-Host "版本号已在 HEAD 提交（$expected 的 csproj 就是 $Version）：不改 csproj、不新建提交，直接从这个提交发布" -ForegroundColor Cyan
}

try {
    # 1. 版本号写进 csproj（先写再测：下面的测试和发布都针对这个版本）
    if ($plan -eq 'write') {
        $x = Set-CsprojVersion $original $Version
        [IO.File]::WriteAllText((Resolve-Path $csproj), $x, (New-Object Text.UTF8Encoding $false))
        Write-Host "版本号 → $Version" -ForegroundColor Cyan
    }

    # 2. 先完整编译一遍；编译失败不打包，并还原版本号
    Write-Host "编译…" -ForegroundColor Cyan
    dotnet build Qisland.sln -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "编译失败，已停止发版" }

    # 3. 发布（依赖框架，体积小；用户需要 .NET 8 桌面运行时）。ContinuousIntegrationBuild：dll / pdb 里的源码路径记成 /_/，不带本机目录
    $out = "publish\Qisland"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish $csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:ContinuousIntegrationBuild=true -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "发布失败" }

    # 产物必须来自 Qisland.App，且版本与 csproj 一致；旧 WinIslands 即使放错位置也会在这里被拦下
    $exe = Join-Path $out "Qisland.exe"
    if (-not (Test-Path -LiteralPath $exe)) { throw "发布产物缺少 Qisland.exe：$exe" }
    $legacy = Get-ChildItem -LiteralPath $out -Recurse -File -Filter "WinIslands.Legacy.exe" -ErrorAction SilentlyContinue
    if ($legacy) { throw "发布产物包含旧版 WinIslands.Legacy.exe：$($legacy[0].FullName)" }
    $projectXml = [xml](Get-Content $csproj -Raw -Encoding UTF8)
    $declaredVersion = @($projectXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0].Trim()
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $exe))
    if ($info.ProductVersion -ne $declaredVersion) {
        throw "产物版本 $($info.ProductVersion) 与 csproj 的 $declaredVersion 不一致"
    }
    if ($info.CompanyName -ne "Qisland.App") {
        throw "产物入口是 $($info.CompanyName)，不是 Qisland.App"
    }
    Write-Host "产物校验通过：Qisland.App v$($info.ProductVersion)" -ForegroundColor Green

    # 4. 打包（publish\ 和 releases\ 都在 .gitignore 里）
    New-Item -ItemType Directory -Force releases | Out-Null
    $zip = "releases\Qisland-$Version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path "$out\*" -DestinationPath $zip
    Write-Host ("打包 → {0}（{1:0.0} MB）" -f $zip, ((Get-Item $zip).Length / 1MB)) -ForegroundColor Cyan
}
catch {
    Restore-Csproj
    throw
}
if ($plan -eq 'committed') {
    # 测试、发布、打包之后再核一遍：工作区仍干净（publish\ releases\ 在 .gitignore 里）、HEAD 没变
    Assert-ReleaseFromHead $csprojGit $Version $expected
}
if ($DryRun) {
    Restore-Csproj
    Write-Host "DryRun：到此为止（分支：$plan；没有提交、没有打标签、没有推送，工作区保持干净）" -ForegroundColor Yellow
    return
}

if ($plan -eq 'write') {
    # 5. 只暂存 csproj，提交后核对：父提交是发版前的 HEAD，改动的只有 csproj，工作区干净
    Invoke-Git add -- $csproj
    $staged = @(Invoke-Git diff --cached --name-only)
    if ($staged.Count -ne 1 -or $staged[0].Trim() -ne $csprojGit) { throw "暂存区不是只有 $csprojGit：$($staged -join ', ')" }
    Invoke-Git commit -q -m "发布 $tag"
    $parent = (Invoke-Git rev-parse "HEAD^" | Select-Object -First 1).Trim()
    if ($parent -ne $expected) { throw "发版提交的父提交是 $parent，不是 $expected" }
    $changed = @(Invoke-Git diff --name-only "HEAD^" HEAD)
    if ($changed.Count -ne 1 -or $changed[0].Trim() -ne $csprojGit) { throw "发版提交改动了意料之外的文件：$($changed -join ', ')" }
    $left = Invoke-Git status --porcelain
    if ($left) { throw "提交后工作区仍有改动：`n$($left -join "`n")" }
}

# 6. 打标签、推送
Invoke-Git tag -a $tag -m "Qisland $tag"
Invoke-Git -c http.postBuffer=524288000 -c http.version=HTTP/1.1 -c credential.helper= -c "credential.helper=!gh auth git-credential" push $Remote HEAD:main --follow-tags

# 7. 建 Release，附上压缩包
$ghArgs = @("release", "create", $tag, $zip, "--title", "Qisland $tag", "--notes", $Notes, "--verify-tag")
if ($pre) { $ghArgs += "--prerelease" } else { $ghArgs += "--latest" }
gh @ghArgs
if ($LASTEXITCODE -ne 0) { throw "创建 Release 失败" }
Write-Host "完成：https://github.com/Qiuizi/Qisland/releases/tag/$tag" -ForegroundColor Green
