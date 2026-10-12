<#
.SYNOPSIS
  外部部署身份预检（只读）：把「**artifact 阶段**（源码 / 前端 dist / 显式构件根）」与
  「**live 阶段**（运行中 Core 实际加载的 DLL / HTTP 提供的 admin 资源）」分开核对，各自独立判定。

.DESCRIPTION
  为什么需要它（AGENTS.md「前端版本管理」+「两段式验收」，复核 §6 的 R5）：
  - 旧脚本按 LastWriteTime 从仓库里所有 admin 目录挑「最新」，把**磁盘最新目录**当成
    「运行中实例正在提供服务的目录」——这只证明磁盘上有新产物，不证明运行实例已加载；
  - 旧脚本只比版本与短哈希，没有 pin 住 commit，served 的 dirty 也没有单独拒绝。

  本脚本改成：
  1. **必须显式给 `-ArtifactRoot`**（不再猜目录）；同一构件根下出现多个 `wwwroot/admin`
     （相邻 Debug/Release 等）一律判为歧义而非取最新。
  2. **必须显式给 `-ExpectedCommit`**：artifact 阶段要求 `git rev-parse HEAD` 与之一致，
     且 dist / 构件根的注入 commitShort 都与它一致；工作树脏也直接拒绝。
  3. **每一环独立拒绝 dirty**（dist / 构件根 / 工作树）。
  4. 用**逐文件 SHA256 清单**比较 `dist` 与 `ArtifactRoot\wwwroot\admin`（不只是版本号），
     并 pin 住构件根里 `PuddingAgent.dll` 的 SHA256 作为「本次构建的 Core 身份」。
  5. **live 阶段**要求 `-BaseUri`：读取运行中进程路径下的 `PuddingAgent.dll` 哈希（与实际加载的
     构建比对），并 HTTP 拉取 `/admin/index.html` 及其引用的脚本，比较
     「HTTP 资源字节 == 构件清单字节」且注入身份 == dist 身份。
  6. **读不到运行身份就是 unknown + 非零退出**，绝不打印 live 通过；没有 `-BaseUri` 时
     live 阶段一律 unknown（要跳过 live 必须显式 `-ArtifactOnly`，且结论里明确标注未验证运行实例）。

  退出码：0 = 全部要求阶段通过；1 = artifact 阶段失败；2 = live 阶段失败或 unknown；
  3 = 两者都失败/unknown。脚本不修改任何文件、不重启任何进程。

.PARAMETER RepoRoot
  仓库根目录；默认取脚本所在目录的上一级。

.PARAMETER ArtifactRoot
  **必填**：本次要交付的构件根（如 `temp\build\deploy` 或某个 publish 目录）。
  其下必须恰好有一个 `wwwroot\admin`。

.PARAMETER ExpectedCommit
  **必填**：本次部署 pin 住的提交（完整或短哈希）。

.PARAMETER BaseUri
  运行中实例的根地址（如 `http://127.0.0.1:5280`）。给了才做 live 阶段。

.PARAMETER CoreProcessName
  运行中 Core 的进程名，默认 `PuddingAgent`。

.PARAMETER ArtifactOnly
  显式声明「本次只验证 artifact 阶段」。此时 live 结论标记为 `skipped`（不是 passed），
  且脚本不会声称运行实例已加载新构建。

.EXAMPLE
  pwsh -File TestScripts/verify-external-deploy-identity.ps1 -ArtifactRoot temp\build\deploy -ExpectedCommit 555dd10

.EXAMPLE
  pwsh -File TestScripts/verify-external-deploy-identity.ps1 -ArtifactRoot temp\build\deploy -ExpectedCommit 555dd10 -BaseUri http://127.0.0.1:5280
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactRoot,
    [string]$ExpectedCommit,
    [string]$BaseUri,
    [string]$CoreProcessName = 'PuddingAgent',
    [switch]$ArtifactOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

function Get-InjectedIdentityFromText {
    param([string]$Text, [string]$Label, [string]$Source)

    if ([string]::IsNullOrEmpty($Text)) { return $null }

    # 注入形态有两种，必须都认：
    #   ① 开发/未压缩：JSON 字面量 {"version":"6.4.4","commitShort":"abc1234","dirty":false,"builtAt":"..."}
    #   ② 生产压缩（umi/terser）：键不带引号、true 变 !0、键顺序任意
    # 仍要求 version + commitShort + dirty + builtAt 同时出现在**同一个对象字面量**里，
    # 避免把依赖包的 version 或源码兜底常量（0.0.0 / unknown）误认成构建身份。
    $versionPattern = '(?:"version"|version)\s*:\s*"(?<v>\d+\.\d+\.\d+[^"]*)"'
    $shortPattern = '(?:"commitShort"|commitShort)\s*:\s*"(?<s>[^"]*)"'
    $dirtyPattern = '(?:"dirty"|dirty)\s*:\s*(?<d>!0|!1|true|false)'
    $builtPattern = '(?:"builtAt"|builtAt)\s*:\s*"(?<b>[^"]*)"'

    foreach ($versionMatch in [regex]::Matches($Text, $versionPattern)) {
        $open = $Text.LastIndexOf('{', $versionMatch.Index)
        if ($open -lt 0) { continue }

        $depth = 0
        $close = -1
        $limit = [Math]::Min($Text.Length, $open + 4000)
        for ($i = $open; $i -lt $limit; $i++) {
            $ch = $Text[$i]
            if ($ch -eq '{') { $depth++ }
            elseif ($ch -eq '}') {
                $depth--
                if ($depth -eq 0) { $close = $i; break }
            }
        }
        if ($close -lt 0) { continue }

        $object = $Text.Substring($open, $close - $open + 1)
        $short = [regex]::Match($object, $shortPattern)
        $dirty = [regex]::Match($object, $dirtyPattern)
        $built = [regex]::Match($object, $builtPattern)
        if (-not ($short.Success -and $dirty.Success -and $built.Success)) { continue }

        $version = $versionMatch.Groups['v'].Value
        $commitShort = $short.Groups['s'].Value
        if ($version -eq '0.0.0' -or $commitShort -eq 'unknown' -or $commitShort -eq '') { continue }

        $dirtyValue = switch ($dirty.Groups['d'].Value) {
            '!0' { 'true' }
            '!1' { 'false' }
            default { $dirty.Groups['d'].Value }
        }

        return [pscustomobject]@{
            Label       = $Label
            Found       = $true
            Version     = $version
            CommitShort = $commitShort
            Dirty       = $dirtyValue
            BuiltAt     = $built.Groups['b'].Value
            Source      = $Source
            Note        = $null
        }
    }

    return $null
}

function Get-InjectedIdentity {
    param([string]$Root, [string]$Label)

    $empty = [pscustomobject]@{
        Label = $Label; Found = $false; Version = $null; CommitShort = $null
        Dirty = $null; BuiltAt = $null; Source = $null
        Note = if (Test-Path $Root) { $null } else { '目录不存在' }
    }
    if (-not (Test-Path $Root)) { return $empty }

    $found = $null
    $fallbackOnly = $false
    foreach ($file in (Get-ChildItem $Root -Recurse -Include *.js -File -ErrorAction SilentlyContinue | Select-Object -First 400)) {
        $text = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
        if (-not $text) { continue }

        $identity = Get-InjectedIdentityFromText -Text $text -Label $Label -Source $file.Name
        if ($identity) { $found = $identity; break }

        if ($fallbackOnly -eq $false -and $text -match '0\.0\.0') { $fallbackOnly = $true }
    }

    if ($found) { return $found }
    if ($fallbackOnly) {
        return [pscustomobject]@{
            Label = $Label; Found = $false; Version = $null; CommitShort = $null
            Dirty = $null; BuiltAt = $null; Source = $null
            Note = '产物里只有 0.0.0/unknown 兜底常量：构建期注入缺失，不能作为「已构建」的证据'
        }
    }

    return $empty
}

function Get-DirectoryManifest {
    param([string]$Root)

    if (-not (Test-Path $Root)) { return $null }

    $rootFull = (Resolve-Path $Root).Path.TrimEnd('\')
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($file in (Get-ChildItem $Root -Recurse -File -ErrorAction SilentlyContinue | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        $lines.Add($relative + '|' + $hash)
    }

    return [pscustomobject]@{
        FileCount = $lines.Count
        Digest    = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) -Algorithm SHA256).Hash
        Lines     = $lines
    }
}

function Get-FileSha256 {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    return (Get-FileHash $Path -Algorithm SHA256).Hash
}

function Find-AdminRoots {
    param([string]$Root)
    return @(
        Get-ChildItem $Root -Recurse -Directory -Filter 'admin' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'wwwroot\\admin$' }
    )
}

function Resolve-ExpectedCommitShort {
    param([string]$Commit)
    if ([string]::IsNullOrWhiteSpace($Commit)) { return $null }
    $value = $Commit.Trim()
    if ($value.Length -le 8) { return $value }
    return $value.Substring(0, 8)
}

$problems = New-Object System.Collections.Generic.List[string]
$liveProblems = New-Object System.Collections.Generic.List[string]

# ── 参数前置校验 ────────────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    Write-Host '-ArtifactRoot 必填：显式给出本次要交付的构件根（不再从仓库里猜最新目录）。' -ForegroundColor Red
    exit 1
}
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    Write-Host '-ExpectedCommit 必填：显式 pin 住本次部署的提交（完整或短哈希）。' -ForegroundColor Red
    exit 1
}
$artifactRootFull = if (Test-Path $ArtifactRoot) { (Resolve-Path $ArtifactRoot).Path } else { $null }
if (-not $artifactRootFull) {
    Write-Host ("-ArtifactRoot 不存在：{0}" -f $ArtifactRoot) -ForegroundColor Red
    exit 1
}

$expectedShort = Resolve-ExpectedCommitShort $ExpectedCommit

Write-Host '== 源码 ==' -ForegroundColor Cyan
$sourcePackageJson = Join-Path $RepoRoot 'Source/PuddingPlatformAdmin/package.json'
if (-not (Test-Path $sourcePackageJson)) {
    Write-Host ("找不到 {0}（-RepoRoot 是否指向仓库根？）" -f $sourcePackageJson) -ForegroundColor Red
    exit 1
}
$sourceVersion = (Get-Content $sourcePackageJson -Raw | ConvertFrom-Json).version
$headCommit = (git -C $RepoRoot rev-parse HEAD 2>$null)
$headShort = (git -C $RepoRoot rev-parse --short HEAD 2>$null)
$worktreeDirty = (@(git -C $RepoRoot status --porcelain 2>$null).Count -gt 0)
Write-Host ("  package.json version : {0}" -f $sourceVersion)
Write-Host ("  HEAD                 : {0} ({1})" -f $headCommit, $headShort)
Write-Host ("  ExpectedCommit       : {0} (short {1})" -f $ExpectedCommit, $expectedShort)
Write-Host ("  工作树是否脏         : {0}" -f $worktreeDirty)
Write-Host ("  构件根               : {0}" -f $artifactRootFull)

if ($worktreeDirty) {
    $problems.Add('工作树脏：交付构建必须在干净检出上完成（否则无法复现，也不能 pin commit）')
}
if ($headCommit -and -not ($headCommit.StartsWith($ExpectedCommit) -or $headShort -eq $ExpectedCommit -or $headShort -eq $expectedShort)) {
    $problems.Add(("HEAD {0} 与 -ExpectedCommit {1} 不一致：这不是本次要部署的那次提交" -f $headShort, $ExpectedCommit))
}

# ── ARTIFACT 阶段 ────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '== ARTIFACT 阶段 ==' -ForegroundColor Cyan

$distPath = Join-Path $RepoRoot 'Source/PuddingPlatformAdmin/dist'
$distIdentity = Get-InjectedIdentity -Root $distPath -Label 'frontend-dist'
$distManifest = Get-DirectoryManifest -Root $distPath

$distIdentityText = '未找到'
if ($distIdentity.Found) {
    $distIdentityText = "{0}/{1}/dirty={2}/{3}" -f $distIdentity.Version, $distIdentity.CommitShort, $distIdentity.Dirty, $distIdentity.BuiltAt
}
Write-Host ("  dist 身份            : {0}" -f $distIdentityText)
if (-not $distIdentity.Found) {
    $problems.Add(("前端 dist 没有构建身份：{0}" -f $(if ($distIdentity.Note) { $distIdentity.Note } else { '尚未 pnpm run build' })))
} else {
    if ($distIdentity.Version -ne $sourceVersion) {
        $problems.Add(("dist 版本 {0} ≠ package.json {1}（前端未重新构建）" -f $distIdentity.Version, $sourceVersion))
    }
    if ($distIdentity.Dirty -eq 'true') {
        $problems.Add('dist 带 +dirty：产物不可复现（dist 环独立拒绝）')
    }
    if ($expectedShort -and $distIdentity.CommitShort -ne $expectedShort) {
        $problems.Add(("dist commitShort {0} ≠ ExpectedCommit short {1}" -f $distIdentity.CommitShort, $expectedShort))
    }
}

$adminRoots = @(Find-AdminRoots -Root $artifactRootFull)
if ($adminRoots.Count -eq 0) {
    $problems.Add('构件根下没有 wwwroot\admin：静态产物未被复制（检查 PuddingHostContent.props）')
    $artifactAdminRoot = $null
} elseif ($adminRoots.Count -gt 1) {
    $paths = ($adminRoots | ForEach-Object { $_.FullName }) -join ' ; '
    $problems.Add(("构件根下有 {0} 个 wwwroot\admin（Debug/Release 等相邻目录）：不得按最新时间猜测，请给更精确的 -ArtifactRoot。命中：{1}" -f $adminRoots.Count, $paths))
    $artifactAdminRoot = $null
} else {
    $artifactAdminRoot = $adminRoots[0].FullName
}

$artifactIdentity = $null
$artifactManifest = $null
$artifactDllHash = $null
if ($artifactAdminRoot) {
    Write-Host ("  admin 目录           : {0}" -f ($artifactAdminRoot -replace [regex]::Escape($RepoRoot + '\'), ''))
    $artifactIdentity = Get-InjectedIdentity -Root $artifactAdminRoot -Label 'artifacts(wwwroot/admin)'
    $artifactManifest = Get-DirectoryManifest -Root $artifactAdminRoot

    if (-not $artifactIdentity.Found) {
        $problems.Add(("构件根的 wwwroot\admin 没有构建身份：{0}" -f $(if ($artifactIdentity.Note) { $artifactIdentity.Note } else { '未部署静态产物' })))
    } else {
        Write-Host ("  artifact 身份        : {0}/{1}/dirty={2}/{3}" -f $artifactIdentity.Version, $artifactIdentity.CommitShort, $artifactIdentity.Dirty, $artifactIdentity.BuiltAt)
        if (-not $distIdentity.Found -or $artifactIdentity.Version -ne $distIdentity.Version) {
            $problems.Add(("artifact 版本 {0} ≠ dist 版本 {1}（Core 构建早于前端构建）" -f $artifactIdentity.Version, $distIdentity.Version))
        }
        if ($artifactIdentity.CommitShort -ne $distIdentity.CommitShort) {
            $problems.Add(("artifact commitShort {0} ≠ dist commitShort {1}" -f $artifactIdentity.CommitShort, $distIdentity.CommitShort))
        }
        if ($artifactIdentity.Dirty -eq 'true') {
            $problems.Add('artifact 带 +dirty：产物来自脏工作树（artifact 环独立拒绝）')
        }
        if ($expectedShort -and $artifactIdentity.CommitShort -ne $expectedShort) {
            $problems.Add(("artifact commitShort {0} ≠ ExpectedCommit short {1}" -f $artifactIdentity.CommitShort, $expectedShort))
        }
    }

    # 逐文件清单比对：版本号相同不代表内容相同（复核 §6：只比版本与短哈希不够）。
    if ($distManifest -and $artifactManifest) {
        if ($distManifest.FileCount -ne $artifactManifest.FileCount) {
            $problems.Add(("artifact 静态产物文件数 {0} ≠ dist {1}（复制不完整）" -f $artifactManifest.FileCount, $distManifest.FileCount))
        }
        if ($distManifest.Digest -ne $artifactManifest.Digest) {
            $problems.Add('artifact 静态产物清单摘要 ≠ dist 清单摘要（内容不一致：复制陈旧或构建期改写）')
        } else {
            Write-Host ("  清单摘要             : {0}（{1} 个文件，与 dist 一致）" -f $artifactManifest.Digest.Substring(0, 16), $artifactManifest.FileCount)
        }
    }

    # pin 住本次构建的 Core 身份（供 live 阶段比对「实际加载的 DLL」）。
    $artifactDll = Join-Path $artifactRootFull 'PuddingAgent.dll'
    $artifactDllHash = Get-FileSha256 -Path $artifactDll
    if (-not $artifactDllHash) {
        $problems.Add(("构件根里找不到 PuddingAgent.dll：无法 pin Core 身份（{0}）" -f $artifactDll))
    } else {
        Write-Host ("  Core DLL SHA256      : {0}" -f $artifactDllHash)
    }
}

# ── LIVE 阶段 ────────────────────────────────────────────────────────────────
Write-Host ''
Write-Host '== LIVE 阶段 ==' -ForegroundColor Cyan

$liveState = 'unknown'
$liveNote = $null

if ($ArtifactOnly) {
    $liveState = 'skipped'
    $liveNote = '-ArtifactOnly：本次未验证运行实例（不得据此声称运行实例已加载新构建）'
    Write-Host ("  {0}" -f $liveNote) -ForegroundColor Yellow
} elseif ([string]::IsNullOrWhiteSpace($BaseUri)) {
    $liveNote = $null
    $liveProblems.Add('未提供 -BaseUri：无法读取运行实例身份 ⇒ unknown（绝不视为通过）')
    Write-Host ("  {0}" -f $liveNote) -ForegroundColor Red
} elseif (-not $artifactDllHash) {
    $liveNote = '构件根未 pin 出 Core DLL 身份：live 阶段无法比对 ⇒ unknown'
    $liveProblems.Add($liveNote)
    Write-Host ("  {0}" -f $liveNote) -ForegroundColor Red
} else {
    # ① 运行中进程的 Core DLL 哈希（实际加载的构建）。
    $process = @(Get-Process -Name $CoreProcessName -ErrorAction SilentlyContinue) | Select-Object -First 1
    if (-not $process) {
        $liveProblems.Add(("找不到进程 {0}：运行实例不存在 ⇒ live unknown" -f $CoreProcessName))
        Write-Host ("  进程 {0}           : 不存在" -f $CoreProcessName) -ForegroundColor Red
    } else {
        $liveProcessPath = $null
        try { $liveProcessPath = $process.Path } catch { $liveProcessPath = $null }
        if ([string]::IsNullOrWhiteSpace($liveProcessPath)) {
            $liveProblems.Add('无法读取运行进程的可执行路径 ⇒ live unknown（不猜）')
        } else {
            Write-Host ("  进程 {0}           : PID {1} @ {2}" -f $CoreProcessName, $process.Id, $liveProcessPath)
            $liveDll = Join-Path (Split-Path -Parent $liveProcessPath) 'PuddingAgent.dll'
            $liveDllHash = Get-FileSha256 -Path $liveDll
            if (-not $liveDllHash) {
                $liveProblems.Add(("运行实例目录里找不到 PuddingAgent.dll：{0} ⇒ live unknown" -f $liveDll))
            } else {
                Write-Host ("  运行中 DLL SHA256    : {0}" -f $liveDllHash)
                if ($liveDllHash -ne $artifactDllHash) {
                    $liveProblems.Add('运行中 Core DLL ≠ 构件根 Core DLL：旧进程仍在运行（磁盘已构建≠已部署）')
                }
            }
        }
    }

    # ② HTTP 资源：字节与清单一致 + 注入身份一致。
    $base = $BaseUri.TrimEnd('/')
    $indexUrl = $base + '/admin/index.html'
    try {
        $response = Invoke-WebRequest -Uri $indexUrl -UseBasicParsing -TimeoutSec 15
        $servedBytes = $response.Content
        if ($servedBytes -is [string]) {
            $servedBytes = [System.Text.Encoding]::UTF8.GetBytes($servedBytes)
        }
        $servedHash = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new($servedBytes)) -Algorithm SHA256).Hash
        $localIndexHash = if ($artifactAdminRoot) { Get-FileSha256 -Path (Join-Path $artifactAdminRoot 'index.html') } else { $null }
        Write-Host ("  HTTP /admin/index.html: {0} bytes, SHA256 {1}" -f $servedBytes.Length, $servedHash)
        if (-not $localIndexHash) {
            $liveProblems.Add('构件根里没有 index.html：无法比对 HTTP 资源与清单 ⇒ live unknown')
        } elseif ($servedHash -ne $localIndexHash) {
            $liveProblems.Add('HTTP /admin/index.html 与构件清单不一致：运行实例提供的不是本次构件根的内容')
        }

        # 拉取页面引用的脚本，核对注入身份（真正的「运行中前端身份」）。
        $html = [System.Text.Encoding]::UTF8.GetString($servedBytes)
        $scriptSources = @([regex]::Matches($html, 'src="(?<s>[^"]+\.js)"') | ForEach-Object { $_.Groups['s'].Value } | Select-Object -First 6)
        $servedIdentity = $null
        foreach ($source in $scriptSources) {
            $url = if ($source -match '^https?://') { $source } elseif ($source.StartsWith('/')) { $base + $source } else { $base + '/admin/' + $source }
            try {
                $scriptResponse = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20
                $scriptText = if ($scriptResponse.Content -is [string]) { $scriptResponse.Content } else { [System.Text.Encoding]::UTF8.GetString($scriptResponse.Content) }
                $servedIdentity = Get-InjectedIdentityFromText -Text $scriptText -Label 'live(served js)' -Source $source
                if ($servedIdentity) { break }
            } catch {
                # 单个脚本取不到不致命：继续找下一个；全失败会在下面判 unknown/fail。
            }
        }

        if (-not $servedIdentity) {
            $liveProblems.Add('HTTP 提供的 admin 脚本里读不到注入身份 ⇒ live unknown（不得视为通过）')
        } else {
            Write-Host ("  live 前端身份        : {0}/{1}/dirty={2}/{3}" -f $servedIdentity.Version, $servedIdentity.CommitShort, $servedIdentity.Dirty, $servedIdentity.BuiltAt)
            if (-not $distIdentity.Found -or $servedIdentity.Version -ne $distIdentity.Version) {
                $liveProblems.Add(("live 版本 {0} ≠ dist 版本 {1}（运行实例提供的是旧前端）" -f $servedIdentity.Version, $distIdentity.Version))
            }
            if ($servedIdentity.CommitShort -ne $distIdentity.CommitShort) {
                $liveProblems.Add(("live commitShort {0} ≠ dist commitShort {1}" -f $servedIdentity.CommitShort, $distIdentity.CommitShort))
            }
            if ($servedIdentity.Dirty -eq 'true') {
                $liveProblems.Add('live 提供的产物带 +dirty：不可交付（live 环独立拒绝）')
            }
        }

        if ($liveProblems.Count -eq 0) { $liveState = 'passed' } else { $liveState = 'failed' }
    } catch {
        $liveProblems.Add(("HTTP 读取失败（{0}）：运行实例不可达 ⇒ live unknown" -f $indexUrl))
        Write-Host ("  HTTP 读取失败        : {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

# ── 结论 ────────────────────────────────────────────────────────────────────
$artifactPassed = ($problems.Count -eq 0)
$livePassed = ($liveState -eq 'passed')

Write-Host ''
Write-Host ("ARTIFACT 阶段：{0}" -f $(if ($artifactPassed) { 'PASSED' } else { 'FAILED' })) -ForegroundColor $(if ($artifactPassed) { 'Green' } else { 'Red' })
foreach ($problem in $problems) { Write-Host ("  - {0}" -f $problem) -ForegroundColor Red }

Write-Host ("LIVE 阶段    ：{0}" -f $liveState.ToUpperInvariant()) -ForegroundColor $(if ($livePassed) { 'Green' } elseif ($liveState -eq 'skipped') { 'Yellow' } else { 'Red' })
foreach ($problem in $liveProblems) { Write-Host ("  - {0}" -f $problem) -ForegroundColor Red }
if ($liveNote) { Write-Host ("  - {0}" -f $liveNote) -ForegroundColor Yellow }

if ($artifactPassed -and $livePassed) {
    Write-Host ''
    Write-Host 'PREFLIGHT OK：构件身份已 pin 且**运行实例已加载同一构建**。' -ForegroundColor Green
    Write-Host '后续：按 Docs/13_runbooks 外部部署与产品验收执行新会话真实 smoke（分别记录受理→首推理、首正文、浏览器首绘）。' -ForegroundColor Yellow
    exit 0
}

if ($artifactPassed -and $liveState -eq 'skipped') {
    Write-Host ''
    Write-Host 'PREFLIGHT ARTIFACT-ONLY：构件阶段通过，但**未验证运行实例**（不得声称 ready-for-external-deploy）。' -ForegroundColor Yellow
    exit 0
}

Write-Host ''
Write-Host 'PREFLIGHT FAILED：' -ForegroundColor Red
if (-not $artifactPassed -and -not $livePassed) { exit 3 }
if (-not $artifactPassed) { exit 1 }
exit 2
