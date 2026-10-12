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
# git 事实必须容错读取：-RepoRoot 可能指向一个**非 git** 的构件/发布目录（运维常见）。
# 那种情况下不能 crash，也不能假装干净 —— 显式记为问题（无法 pin 提交 ⇔ artifact 阶段不得通过）。
$headCommit = $null
$headShort = $null
$worktreeDirty = $false
$gitResolvable = $false
try {
    $headCommit = (git -C $RepoRoot rev-parse HEAD 2>&1 | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $headCommit -match '^[0-9a-f]{7,40}$') {
        $headShort = (git -C $RepoRoot rev-parse --short HEAD 2>&1 | Select-Object -First 1)
        $worktreeDirty = (@(git -C $RepoRoot status --porcelain 2>&1).Count -gt 0)
        $gitResolvable = $true
    } else {
        $headCommit = $null
        $headShort = $null
    }
} catch {
    $headCommit = $null
    $headShort = $null
    $gitResolvable = $false
}

Write-Host ("  package.json version : {0}" -f $sourceVersion)
Write-Host ("  HEAD                 : {0} ({1})" -f $headCommit, $headShort)
Write-Host ("  ExpectedCommit       : {0} (short {1})" -f $ExpectedCommit, $expectedShort)
Write-Host ("  工作树是否脏         : {0}" -f $worktreeDirty)
Write-Host ("  构件根               : {0}" -f $artifactRootFull)
Write-Host ("  git 可判定           : {0}" -f $gitResolvable)

if (-not $gitResolvable) {
    $problems.Add('-RepoRoot 不是可判定的 git 工作树：无法核对 HEAD 与工作树干净度（无法 pin 提交）')
}

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
    # 允许构件侧多出**构建期派生的 `.gz`**（Core 内容管线为静态服务预压缩）：
    # 但 dist 的每个文件都必须在构件里逐字节一致，且额外文件只能是 `<dist 文件>.gz`。
    if ($distManifest -and $artifactManifest) {
        $distMap = @{}
        foreach ($line in $distManifest.Lines) {
            $parts = $line -split '\|', 2
            $distMap[$parts[0]] = $parts[1]
        }
        $artifactMap = @{}
        foreach ($line in $artifactManifest.Lines) {
            $parts = $line -split '\|', 2
            $artifactMap[$parts[0]] = $parts[1]
        }

        $missing = @($distMap.Keys | Where-Object { -not $artifactMap.ContainsKey($_) })
        $mismatched = @($distMap.Keys | Where-Object {
            $artifactMap.ContainsKey($_) -and $artifactMap[$_] -ne $distMap[$_]
        })
        $unexpected = @($artifactMap.Keys | Where-Object {
            if ($distMap.ContainsKey($_)) { return $false }
            # 派生文件只允许 `.gz`，且其基准文件必须存在于 dist。
            if ($_ -like '*.gz') { return -not $distMap.ContainsKey($_.Substring(0, $_.Length - 3)) }
            return $true
        })

        if ($missing.Count -gt 0) {
            $sample = ($missing | Select-Object -First 5) -join ', '
            $problems.Add(("artifact 缺少 dist 的 {0} 个文件（复制不完整），例如：{1}" -f $missing.Count, $sample))
        }
        if ($mismatched.Count -gt 0) {
            $sample = ($mismatched | Select-Object -First 5) -join ', '
            $problems.Add(("artifact 有 {0} 个文件与 dist 逐字节不一致（复制陈旧或构建期改写），例如：{1}" -f $mismatched.Count, $sample))
        }
        if ($unexpected.Count -gt 0) {
            $sample = ($unexpected | Select-Object -First 5) -join ', '
            $problems.Add(("artifact 有 {0} 个既不属于 dist、也不是 `.gz` 派生的文件（陈旧残留？），例如：{1}" -f $unexpected.Count, $sample))
        }
        if ($missing.Count -eq 0 -and $mismatched.Count -eq 0 -and $unexpected.Count -eq 0) {
            Write-Host ("  清单摘要             : dist {0} 个文件全部逐字节一致（构件另有 {1} 个 .gz 派生）" -f $distMap.Count, ($artifactMap.Count - $distMap.Count))
        }
    }

    # pin 住本次构建的 Core 身份（供 live 阶段比对「实际加载的 DLL」）。
    # `--artifacts-path` 布局下 DLL 在 `<root>\bin\<Project>\<config>\`，普通 publish 才在根：
    # 因此**递归查找并强制唯一**，不假设位置（歧义 ⇒ 明确要求更精确的 -ArtifactRoot）。
    $dllCandidates = @(
        Get-ChildItem $artifactRootFull -Recurse -Filter 'PuddingAgent.dll' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\ref\\' }
    )
    if ($dllCandidates.Count -eq 0) {
        $problems.Add(("构件根下找不到 PuddingAgent.dll（排除 obj/ref）：无法 pin Core 身份（{0}）" -f $artifactRootFull))
    } elseif ($dllCandidates.Count -gt 1) {
        $paths = ($dllCandidates | ForEach-Object { $_.FullName }) -join ' ; '
        $problems.Add(("构件根下有 {0} 个 PuddingAgent.dll（多配置/多目标）：不得按时间猜测，请给更精确的 -ArtifactRoot。命中：{1}" -f $dllCandidates.Count, $paths))
    } else {
        $artifactDll = $dllCandidates[0].FullName
        $artifactDllHash = Get-FileSha256 -Path $artifactDll
        Write-Host ("  Core DLL             : {0}" -f ($artifactDll -replace [regex]::Escape($artifactRootFull + '\'), ''))
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
    # ① 运行实例身份端点（复核 R5）：PID + 启动标识 + 关键程序集 MVID/文件 SHA256。
    #    端点由产品提供（api/system/build-identity），是"在线实例加载了哪份构建"的唯一权威来源。
    $base = $BaseUri.TrimEnd('/')
    $identityUrl = $base + '/api/system/build-identity'
    $liveIdentity = $null
    try {
        $identityResponse = Invoke-WebRequest -Uri $identityUrl -UseBasicParsing -TimeoutSec 15
        $identityText = if ($identityResponse.Content -is [string]) { $identityResponse.Content } else { [System.Text.Encoding]::UTF8.GetString($identityResponse.Content) }
        $liveIdentity = $identityText | ConvertFrom-Json
    } catch {
        $liveProblems.Add(("读不到运行实例身份端点（{0}）：{1} ⇒ live unknown（不得视为通过）" -f $identityUrl, $_.Exception.Message))
        Write-Host ("  身份端点             : 读取失败 ⇒ unknown") -ForegroundColor Red
    }

    if ($liveIdentity) {
        Write-Host ("  实例 PID             : {0}（启动 {1}）" -f $liveIdentity.processId, $liveIdentity.processStartedAtUtc)

        # 绑定端口对应进程：BaseUri 返回的 PID 必须真实存在，且启动标识吻合。
        $liveProcess = $null
        try { $liveProcess = Get-Process -Id ([int]$liveIdentity.processId) -ErrorAction Stop } catch { $liveProcess = $null }
        if (-not $liveProcess) {
            $liveProblems.Add(("BaseUri 指向的实例 PID {0} 不存在：端口未绑定到活的运行进程 ⇒ live unknown" -f $liveIdentity.processId))
        } else {
            $startMatches = $false
            try {
                if ($liveIdentity.processStartedAtUtc) {
                    $reportedStart = [datetimeoffset]::Parse($liveIdentity.processStartedAtUtc)
                    $actualStart = [datetimeoffset]$liveProcess.StartTime.ToUniversalTime()
                    $startMatches = ([math]::Abs(($reportedStart - $actualStart).TotalSeconds) -le 2)
                }
            } catch { $startMatches = $false }

            if (-not $startMatches) {
                $liveProblems.Add('PID 存在但启动标识不吻合：身份端点与进程不是同一实例 ⇒ live unknown（不猜）')
            }
        }

        # 逐程序集比对：MVID 必须存在，文件 SHA256 必须与构件根同名 DLL 一致（唯一命中，不按时间猜）。
        $assemblyRows = @($liveIdentity.assemblies)
        foreach ($row in $assemblyRows) {
            if (-not $row.loaded) {
                $liveProblems.Add(("实例未加载关键程序集 {0}：无法证明本次修复已在线 ⇒ live unknown" -f $row.name))
                continue
            }

            $candidates = @(
                Get-ChildItem $artifactRootFull -Recurse -Filter ($row.name + '.dll') -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\ref\\' }
            )
            if ($candidates.Count -eq 0) {
                $liveProblems.Add(("构件根里找不到 {0}.dll：无法比对在线程序集身份" -f $row.name))
                continue
            }
            if ($candidates.Count -gt 1) {
                $liveProblems.Add(("构件根里有 {0} 个 {1}.dll（多实例/多配置）：不得按时间猜测，请给更精确的 -ArtifactRoot" -f $candidates.Count, $row.name))
                continue
            }

            $artifactAssemblyHash = Get-FileSha256 -Path $candidates[0].FullName
            $reportedHash = if ($row.fileSha256) { ([string]$row.fileSha256).ToUpperInvariant() } else { $null }
            $artifactUpper = if ($artifactAssemblyHash) { $artifactAssemblyHash.ToUpperInvariant() } else { $null }

            if ([string]::IsNullOrWhiteSpace($row.mvid)) {
                $liveProblems.Add(("实例未报告 {0} 的 MVID：无法证明实际加载的程序集身份 ⇒ live unknown" -f $row.name))
            }
            if (-not $reportedHash -or $reportedHash -ne $artifactUpper) {
                $liveProblems.Add(("在线 {0} 的文件 SHA256 ≠ 构件根（旧进程仍在运行：磁盘已构建≠已部署）" -f $row.name))
            } else {
                Write-Host ("  {0,-18} : MVID {1} · SHA256 与构件一致" -f $row.name, $row.mvid)
            }
        }
    }

    # ② HTTP 资源：字节与清单一致 + 注入身份一致 + **每个 JS/CSS 字节**与构件一致。
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

        if (-not $artifactAdminRoot) {
            $liveProblems.Add('构件根里没有 wwwroot/admin：无法校验 JS/CSS 字节 ⇒ live unknown')
        } else {
            $html = [System.Text.Encoding]::UTF8.GetString($servedBytes)
            # 校验**实际返回的 JS/CSS 字节**，而不只是解析注入字段：
            # 身份字段可以相同而脚本内容不同（复核要求的拒绝用例）。
            $assetSources = @(
                [regex]::Matches($html, '(?:src|href)="(?<s>[^"]+\.(?:js|css))"') |
                    ForEach-Object { $_.Groups['s'].Value } |
                    Select-Object -Unique
            )
            $checkedAssets = 0
            $assetMismatches = New-Object System.Collections.Generic.List[string]
            foreach ($source in $assetSources) {
                $relative = if ($source -match '^https?://') { $null } elseif ($source.StartsWith('/')) { $source.TrimStart('/') } else { 'admin/' + $source }
                if (-not $relative) { continue }
                if ($relative.StartsWith('admin/')) { $relative = $relative.Substring('admin/'.Length) }

                $localPath = Join-Path $artifactAdminRoot $relative
                if (-not (Test-Path $localPath)) { continue }

                $url = $base + '/admin/' + $relative
                try {
                    $assetResponse = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20
                    $assetBytes = $assetResponse.Content
                    if ($assetBytes -is [string]) { $assetBytes = [System.Text.Encoding]::UTF8.GetBytes($assetBytes) }
                    $assetHash = (Get-FileHash -InputStream ([System.IO.MemoryStream]::new($assetBytes)) -Algorithm SHA256).Hash
                    $localHash = Get-FileSha256 -Path $localPath
                    $checkedAssets++
                    if ($assetHash -ne $localHash) {
                        $assetMismatches.Add($relative)
                    }
                } catch {
                    $assetMismatches.Add($relative + '(读取失败)')
                }
            }

            Write-Host ("  已校验 JS/CSS 字节   : {0} 个" -f $checkedAssets)
            if ($assetMismatches.Count -gt 0) {
                $liveProblems.Add(("在线 JS/CSS 与构件字节不一致（身份字段可能相同）：{0}" -f (($assetMismatches | Select-Object -First 5) -join ', ')))
            }
            if ($checkedAssets -eq 0) {
                $liveProblems.Add('没有校验到任何 JS/CSS 字节：无法证明在线前端与构件一致 ⇒ live unknown')
            }
        }

        # 拉取页面引用的脚本，核对注入身份（真正的「运行中前端身份」）。
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
