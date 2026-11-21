<#
发版脚本的纯函数部分（release.ps1 会 dot-source 这个文件）。这里不改文件、不提交、不推送，只做判断和核对，
所以测试可以在临时假仓库里直接调用它们。
#>

# 所有 git 调用都走这里：退出码不是 0 就抛错，不让失败的 git 命令被悄悄吞掉
function Invoke-Git {
    $gitArgs = @($args)
    $output = & git @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') 失败（退出码 $LASTEXITCODE）" }
    return $output
}

# 把 csproj 文本里的三处版本号换成目标版本，返回新文本（不写文件）
function Set-CsprojVersion([string]$Text, [string]$Version) {
    $core = ($Version -split '-')[0]
    $x = $Text
    $x = $x -replace '<Version>[^<]*</Version>', "<Version>$Version</Version>"
    $x = $x -replace '<FileVersion>[^<]*</FileVersion>', "<FileVersion>$core.0</FileVersion>"
    $x = $x -replace '<InformationalVersion>[^<]*</InformationalVersion>', "<InformationalVersion>$Version</InformationalVersion>"
    return $x
}

function Get-DeclaredVersion([string]$Text) {
    $m = [regex]::Match($Text, '<Version>([^<]*)</Version>')
    if (-not $m.Success) { return '' }
    return $m.Groups[1].Value.Trim()
}

# write：要把版本号写进 csproj 并单独提交；committed：HEAD 上的 csproj 已经是目标版本，直接从 HEAD 发布
function Get-VersionPlan([string]$Text, [string]$Version) {
    $updated = Set-CsprojVersion $Text $Version
    if ($updated -ne $Text) { return 'write' }
    $declared = Get-DeclaredVersion $Text
    if ($declared -ne $Version) { throw "csproj 里找不到可替换的版本号（声明的是「$declared」，要发 $Version）" }
    return 'committed'
}

# 「版本号已提交」分支的核对：工作区干净、HEAD 仍是确认过的提交、HEAD 里的 csproj 声明的就是要发的版本
function Assert-ReleaseFromHead([string]$CsprojGit, [string]$Version, [string]$Expected) {
    $dirty = Invoke-Git status --porcelain
    if ($dirty) { throw "工作区不干净，不能直接从 HEAD 发布：`n$($dirty -join "`n")" }
    $head = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()
    if ($head -ne $Expected) { throw "HEAD 是 $head，不是确认过的 $Expected，已停止发版" }
    $committed = (Invoke-Git show "HEAD:$CsprojGit") -join "`n"
    $declared = Get-DeclaredVersion $committed
    if ($declared -ne $Version) { throw "HEAD 里 $CsprojGit 声明的版本是「$declared」，不是要发的 $Version" }
}
