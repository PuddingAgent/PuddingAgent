<#
.SYNOPSIS
  外部部署身份预检（只读）：核对「源码版本 / 前端 dist 构建身份 / Core 实际提供服务的 wwwroot/admin 身份」三者是否一致。

.DESCRIPTION
  为什么需要这个脚本（AGENTS.md「前端版本管理」+「两段式验收」）：
  - 前端产物要经过 PuddingHostContent.props（PuddingPlatformAdmin/dist/** → wwwroot/admin/**）才会生效，
    **只重新构建前端不会生效**，只重启 Core 也不会重新部署静态产物；
  - 交付构建**不得带 +dirty**（带 dirty 说明产物不可复现）；
  - 页角徽标 `v版本号[+dirty] · 短哈希 · 构建时间` 是「运行中实例用的到底是哪一份构建」的唯一凭据。

  本脚本是**门禁**：三者不一致或 dist 带 dirty 时非零退出。它不修改任何文件、不重启任何进程。

.PARAMETER RepoRoot
  仓库根目录；默认取脚本所在目录的上一级。

.EXAMPLE
  pwsh -File TestScripts/verify-external-deploy-identity.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Get-InjectedIdentity {
    param([string]$Root, [string]$Label)

    if (-not (Test-Path $Root)) {
        return [pscustomobject]@{ Label = $Label; Found = $false; Version = $null; CommitShort = $null; Dirty = $null; BuiltAt = $null; Source = $null }
    }

    # 注入形态有两种，必须都认：
    #   ① 开发/未压缩：JSON 字面量 {"version":"6.4.4","commitShort":"abc1234","dirty":false,"builtAt":"..."}
    #   ② 生产压缩（umi/terser）：JS 对象字面量 —— 键不带引号、true 变 !0、键顺序任意：
    #      version:"6.4.3",commitShort:"4029ee8",dirty:!0,builtAt:"..."
    # 因此：键名允许带/不带引号；dirty 接受 true/false/!0/!1；窗口取宽（version 可能在对象末尾）。
    # 仍要求 version + commitShort + dirty + builtAt 同时出现，避免把依赖包的 version 误认成构建身份。
    $versionPattern = '(?:"version"|version)\s*:\s*"(?<v>\d+\.\d+\.\d+[^"]*)"'
    $shortPattern = '(?:"commitShort"|commitShort)\s*:\s*"(?<s>[^"]*)"'
    $dirtyPattern = '(?:"dirty"|dirty)\s*:\s*(?<d>!0|!1|true|false)'
    $builtPattern = '(?:"builtAt"|builtAt)\s*:\s*"(?<b>[^"]*)"'

    $candidates = New-Object System.Collections.Generic.List[object]

    foreach ($file in (Get-ChildItem $Root -Recurse -Include *.js -File | Select-Object -First 400)) {
        $text = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }

        foreach ($versionMatch in [regex]::Matches($text, $versionPattern)) {
            # 只在**包含该 version 的那一个对象字面量**里找其余字段。
            # 不能用一个固定宽度的窗口：产物里兜底常量（0.0.0/unknown）与真实注入值常常相邻，
            # 窗口会把兜底的 commitShort/dirty 配到真实的 version 上（实测踩过）。
            $open = $text.LastIndexOf('{', $versionMatch.Index)
            if ($open -lt 0) { continue }

            $depth = 0
            $close = -1
            $limit = [Math]::Min($text.Length, $open + 4000)
            for ($i = $open; $i -lt $limit; $i++) {
                $ch = $text[$i]
                if ($ch -eq '{') { $depth++ }
                elseif ($ch -eq '}') {
                    $depth--
                    if ($depth -eq 0) { $close = $i; break }
                }
            }
            if ($close -lt 0) { continue }

            $object = $text.Substring($open, $close - $open + 1)

            $short = [regex]::Match($object, $shortPattern)
            $dirty = [regex]::Match($object, $dirtyPattern)
            $built = [regex]::Match($object, $builtPattern)
            if (-not ($short.Success -and $dirty.Success -and $built.Success)) { continue }

            $dirtyValue = switch ($dirty.Groups['d'].Value) {
                '!0' { 'true' }
                '!1' { 'false' }
                default { $dirty.Groups['d'].Value }
            }

            $candidates.Add([pscustomobject]@{
                Label       = $Label
                Found       = $true
                Version     = $versionMatch.Groups['v'].Value
                CommitShort = $short.Groups['s'].Value
                Dirty       = $dirtyValue
                BuiltAt     = $built.Groups['b'].Value
                Source      = $file.Name
                Note        = $null
            })
        }
    }

    # 产物里必然同时存在源码兜底常量 FALLBACK_FRONTEND_BUILD（0.0.0 / unknown）。
    # 它**不是**构建身份，不得被当成「已构建」的证据；只有真实注入值才算。
    $real = $candidates |
        Where-Object { $_.Version -ne '0.0.0' -and $_.CommitShort -ne 'unknown' -and $_.CommitShort -ne '' } |
        Select-Object -First 1
    if ($real) { return $real }

    if ($candidates.Count -gt 0) {
        return [pscustomobject]@{
            Label = $Label; Found = $false; Version = $null; CommitShort = $null; Dirty = $null; BuiltAt = $null
            Source = $candidates[0].Source
            Note = '产物里只有 0.0.0/unknown 兜底常量：构建期注入缺失，不能作为「已构建」的证据'
        }
    }

    return [pscustomobject]@{ Label = $Label; Found = $false; Version = $null; CommitShort = $null; Dirty = $null; BuiltAt = $null; Source = $null; Note = $null }
}

$sourcePackageJson = Join-Path $RepoRoot 'Source/PuddingPlatformAdmin/package.json'
if (-not (Test-Path $sourcePackageJson)) {
    Write-Error "找不到 $sourcePackageJson（-RepoRoot 是否指向仓库根？）"
}
$sourceVersion = (Get-Content $sourcePackageJson -Raw | ConvertFrom-Json).version

$distPath = Join-Path $RepoRoot 'Source/PuddingPlatformAdmin/dist'
$distIdentity = Get-InjectedIdentity -Root $distPath -Label 'frontend-dist'

# Core 实际读取的静态产物：PuddingAgent 输出目录下的 wwwroot/admin（可能有多个配置目录，取最新的）。
$servedCandidates = @(
    Get-ChildItem (Join-Path $RepoRoot 'Source/PuddingAgent') -Recurse -Directory -Filter 'admin' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'wwwroot\\admin$' } |
        Sort-Object LastWriteTime -Descending
)
$servedRoot = if ($servedCandidates.Count -gt 0) { $servedCandidates[0].FullName } else { $null }
$servedIdentity = if ($servedRoot) { Get-InjectedIdentity -Root $servedRoot -Label 'served(wwwroot/admin)' } `
                  else { [pscustomobject]@{ Label = 'served(wwwroot/admin)'; Found = $false; Version = $null; CommitShort = $null; Dirty = $null; BuiltAt = $null; Source = $null } }

Write-Host "== 源码 ==" -ForegroundColor Cyan
Write-Host ("  package.json version : {0}" -f $sourceVersion)
Write-Host ("  HEAD                 : {0}" -f (git -C $RepoRoot rev-parse --short HEAD))
Write-Host ("  工作树是否脏         : {0}" -f (@(git -C $RepoRoot status --porcelain).Count -gt 0))

foreach ($identity in @($distIdentity, $servedIdentity)) {
    Write-Host ("== {0} ==" -f $identity.Label) -ForegroundColor Cyan
    if ($identity.Found) {
        Write-Host ("  版本/短哈希/构建时间 : {0} / {1} / {2}" -f $identity.Version, $identity.CommitShort, $identity.BuiltAt)
        Write-Host ("  dirty                : {0}" -f $identity.Dirty)
        Write-Host ("  产物文件             : {0}" -f $identity.Source)
    } else {
        Write-Host "  未找到注入身份（未构建 / 已清理 / 注入被禁用）" -ForegroundColor Yellow
    }
}
if ($servedRoot) { Write-Host ("  提供服务目录         : {0}" -f ($servedRoot -replace [regex]::Escape($RepoRoot + '\'), '')) }

$problems = New-Object System.Collections.Generic.List[string]

if (-not $distIdentity.Found) {
    $problems.Add('前端 dist 没有构建身份：尚未 pnpm run build（或 dist 已清理）')
} else {
    if ($distIdentity.Version -ne $sourceVersion) {
        $problems.Add("dist 版本 $($distIdentity.Version) ≠ package.json $sourceVersion（前端未重新构建）")
    }
    if ($distIdentity.Dirty -eq 'true') {
        $problems.Add('dist 带 +dirty：产物不可复现，交付构建必须在**干净检出**上重新构建')
    }
}

if (-not $servedIdentity.Found) {
    $problems.Add('Core 提供服务目录里没有构建身份：静态产物尚未部署（需重新构建 Core 以复制 dist）')
} else {
    if (-not $distIdentity.Found -or $servedIdentity.Version -ne $distIdentity.Version) {
        $problems.Add("served 版本 $($servedIdentity.Version) ≠ dist 版本 $($distIdentity.Version)（Core 未重新构建，静态产物未重新部署）")
    }
    if ($servedIdentity.CommitShort -ne $distIdentity.CommitShort) {
        $problems.Add("served 短哈希 $($servedIdentity.CommitShort) ≠ dist 短哈希 $($distIdentity.CommitShort)")
    }
}

if ($problems.Count -eq 0) {
    Write-Host ''
    Write-Host 'PREFLIGHT OK：源码 / dist / served 三者身份一致，且交付产物不带 dirty。' -ForegroundColor Green
    Write-Host '注意：本脚本只核对**静态产物身份**；重启/退出回收/真实模型 smoke 由进程外控制器判定。' -ForegroundColor Yellow
    exit 0
}

Write-Host ''
Write-Host 'PREFLIGHT FAILED：' -ForegroundColor Red
foreach ($problem in $problems) { Write-Host ("  - {0}" -f $problem) -ForegroundColor Red }
Write-Host ''
Write-Host '结论：当前构建**不能**作为「运行中实例已加载新代码」的证据；按 Docs/13_runbooks 的外部部署步骤重做。' -ForegroundColor Yellow
exit 1
