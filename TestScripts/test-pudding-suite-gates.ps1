#requires -Version 7.0
<#
.SYNOPSIS
  统一测试门禁：跑「声明过的套件列表」，按**用例身份**（而非失败数量）判定，输出结构化结论。

.DESCRIPTION
  为什么需要它：本仓库曾有整套测试**长期红而无人知**（`PuddingCoreTests` 的契约冻结测试自 2026-07-23
  起就红、前端 jest 亦有既有红），红测试只是症状，**门禁缺失才是病因**。本脚本把"跑哪些、哪些是已知红"
  写成**可执行的单一事实源**，避免"我跑的那几套"被误当成"全部"。

  ── 判据（2026-09-21 重写：S0 评测门禁可信化）──────────────────────────────
  **旧判据的洞（本次修复对象）**：
    ① 只比**失败数量**（`failed <= AllowedFailures`）⇒「**修掉一个旧红、引入一个新红**」计数不变 ⇒ 误判 PASS。
       该风险在 RSI 语境下叫「**优化器学会移动球门**」：判据可被"换掉红的"满足，而不是真的变好。
    ② `KnownRed = $null`（名单未登记）+ 手填预算 ⇒ 等于放弃身份判据，退回只数数量。
    ③ 必测套件**被锁 / 未测**一律 exit 0 ⇒ "没测"被当成"通过"。
    ④ **从人类可读文案里正则抓取**失败名与计数 ⇒ 受**语言与编码**支配。2026-09-21 实测踩中：
       `dotnet test` 输出以 CP936 落盘、控制台按 UTF-8 解码 ⇒ 日志成为 mojibake
       （`失败: 1，通过: 910` → `澶辫触: 1锛岄€氳繃: 910`），中文正则**全部匹配不到**
       ⇒ 计数变 `$null`、失败名变空 ⇒ 三套 dotnet 套件被静默判为"未测"。

  **新判据**：
    · **结构化证据优先**：计数与失败名一律来自**机器可读产物** —— dotnet 用 **TRX**（UTF-8 XML）、
      jest 用 **--json**。**不再从文案抓取**（消灭洞④：语言、编码、`● Console` 之类的启发式全部退场）。
      结构化产物缺失或不可解析 ⇒ `counts = $null` ⇒ `UNMEASURED`（**fail-closed，不回退到文案猜测**）。
    · **身份判据**：`预算 = KnownRed.Count`（**派生，不手填**）。观测失败名必须 ⊂ `KnownRed`，
      否则 FAIL（`unexpected_failures`）⇒ ① 被结构性消灭（"换一个红"必然不在名单里）。
    · **名单必须登记**：`KnownRed = $null` 且本次有失败 ⇒ 不得声称通过。
    · **名单防腐**：`KnownRedMeasuredAt` 超过 `KnownRedMaxAgeDays`（默认 30）⇒ FAIL；
      名单里声明但本次未出现 ⇒ 报 `known_red_stale`（提示收紧，不 FAIL）。
    · **必测未测 ⇒ 非零退出**：`Required = $true`（默认）的套件若 `UNMEASURED`/`SKIPPED_LOCKED` ⇒ FAIL。
    · **豁免带到期日**：`Required = $false` 必须给 `Exemption`（原因 + 到期日 + 责任人）；
      到期即 FAIL；**缺 Exemption 也 FAIL**（不留"静默豁免"后门）。
    · **输入指纹**：记录 `HEAD` + 工作区脏哈希，与 `BaselineCommit` 一并留档，使"某条基线是在哪个版本上
      测的"可被事后追溯（漂移只报告，不阻断——否则门禁天天红就会被无视）。
    · **机器可读**：结构化 JSON（含每个套件的 Status / Reasons / Unexpected / StaleKnownRed / Budget / 证据路径）。

  **自证（`-SelfTest`）**：用**同一判定函数**跑一组合成负例，并同时给出**旧判据**的结果，
  以证明"旧脚本误放、新脚本拦住"（S0 验收要求）。自检失败 ⇒ 非零退出。

  退出码：全部套件通过（且算子架构门禁为 0）⇒ 0；否则 ⇒ 1。

.PARAMETER Only
  只跑指定套件（逗号分隔的名字）。默认跑全部。

.PARAMETER ListOnly
  只打印套件清单与判据配置，不执行。

.PARAMETER SelfTest
  只跑判定函数的合成负例自检，不执行任何套件。

.EXAMPLE
  pwsh -File TestScripts\test-pudding-suite-gates.ps1 -SelfTest
  pwsh -File TestScripts\test-pudding-suite-gates.ps1 -Only Core
  pwsh -File TestScripts\test-pudding-suite-gates.ps1
#>
[CmdletBinding()]
param(
    [string]$Only = '',
    [switch]$ListOnly,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'temp/suite-gates'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$today = (Get-Date).Date

# `dotnet test` 的**人类可读日志**受语言支配（编码事故的源头之一）。这里固定为英文，
# 使 .log 至少有稳定的 ASCII 证据可读；但**判定不再依赖它**（判定只用 TRX/JSON）。
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# ── 单一事实源：套件清单 + 判据配置 ──────────────────────────────────────────
# 字段说明：
#   KnownRed            = 已知红**用例名**列表（归一化形式：分隔符 `>`、空白折叠）。
#                         `@()` = 名单已登记且为空（要求全绿）；`$null` = 名单未登记。
#   KnownRedMeasuredAt  = 该名单的**观测日期**（超过 KnownRedMaxAgeDays 即失效 ⇒ FAIL）。
#   KnownRedMaxAgeDays  = 名单保鲜期（默认 30）。
#   Required            = 必测套件（默认 $true）；必测未测 ⇒ FAIL。
#   Exemption           = Required=$false 时**必须**给出：@{ Reason; ExpiresOn('yyyy-MM-dd'); Owner }。
#   BaselineCommit      = 该基线是在哪个提交上测得的（留档；与 HEAD 不一致只报告漂移，不阻断）。
# 注意：**没有 `AllowedFailures` 这个旋钮了** —— 预算一律由 KnownRed.Count 派生，杜绝"悄悄抬预算"。
$KnownRedMaxAgeDaysDefault = 30

$Suites = [ordered]@{
    'Core' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingCoreTests/PuddingCoreTests.csproj'
        Filter = 'TestCategory!=Live'
        KnownRed = @('ProcessSwarmAsync_WithInvalidSwarmDirectory_HandlesError')
        KnownRedMeasuredAt = '2026-09-21'
        BaselineCommit = '8ed4c5b'
        Note = '2026-09-21 实测：911 总计 / 910 通过 / 1 红。既有红，非本轮引入；为 Swarm 对非仓库目录的既有健壮性缺口。'
    }
    'Runtime' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj'
        Filter = 'TestCategory!=Live'
        KnownRed = @()
        KnownRedMeasuredAt = '2026-09-21'
        BaselineCommit = 'e74a424'
        Note = '期望 0 红（含 S1b/S2a/S2b 新增算子用例）；基线差异说明见 TestScripts/README.md。'
    }
    'Platform' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingPlatformTests/PuddingPlatformTests.csproj'
        Filter = 'TestCategory!=Live'
        KnownRed = @()
        KnownRedMeasuredAt = '2026-09-21'
        BaselineCommit = 'e74a424'
        Note = '期望 0 红。若 SKILL-Hub 在途改动使该套件变红，属并行协作者在途，需与其对齐后再判。'
    }
    'AdminJest' = @{
        Kind = 'jest'
        Dir = 'Source/PuddingPlatformAdmin'
        # 2026-09-21 实测：Tests: 3 failed, 1349 passed, 1352 total
        # 名单由「逐例定性台账」登记（TestScripts/known-red-dispositions.md）：
        #   #6 / #7 InputArea 语音族、#9 IntentConsole 语音族 —— 全部因**孤儿组件** VoiceConversationPanel.tsx
        #   生产内无任何引用；等用户裁定「接线 vs 移除」后再动测试（无决策不动测试、不删组件）。
        # 收窄轨迹：17 → 16 → 14 → 13（menuIcons 缺 hdd/key 映射 = 真实缺陷）→ 10 → 7 → 5 → 4 → 3。
        KnownRed = @(
            'InputArea status feedback > switches into voice mode and sends a transcript with voice metadata',
            'InputArea status feedback > shows the voice mode unavailable state when browser microphone capture is unavailable',
            'IntentConsole > sends voice transcript with voice metadata from the console boundary'
        )
        KnownRedMeasuredAt = '2026-09-21'
        BaselineCommit = '8ed4c5b'
        Note = '既有红 3 例（全为语音族，已定性为 A 类测试滞后；台账见 TestScripts/known-red-dispositions.md）。'
    }
    'WebApi' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingWebApiTests/PuddingWebApiTests.csproj'
        Filter = ''
        # 实测（2026-09-21）：该套件构建依赖 `PuddingAgent` 的输出目录，而**运行中的 Core 会锁住**
        # `Source/PuddingAgent/bin/Debug/net10.0/*.dll` ⇒ MSB3027/MSB3021，**此刻无法测量**。
        # 需在 Core 停止后测（部署窗口），或改用独立输出路径构建。
        # 因此这是**显式豁免**（带到期日）：到期仍未测 ⇒ 门禁 FAIL（不允许变成永久豁免）。
        KnownRed = $null
        KnownRedMeasuredAt = $null
        BaselineCommit = $null
        Required = $false
        Exemption = @{
            Reason = '基线未实测：在 Core 运行时**无法测量**（构建需写 PuddingAgent 输出目录，被运行进程锁定）。需在部署窗口（Core 停止）实测首批基线。'
            ExpiresOn = '2026-10-05'
            Owner = 'default.global_general-assistant.6a8'
        }
        Note = '基线未实测（显式豁免至 2026-10-05）。'
    }
}

# ── 归一化：让名单可以用纯 ASCII 书写，避免 `›` 等字符在脚本/控制台间的编码漂移 ──
function ConvertTo-NormalizedCaseName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    # 【为什么不一枚举分隔符】2026-09-21 实测：jest 的 `fullName` 分隔符到底是不是 U+203A，
    # 取决于版本/终端；按字符枚举就必然会漏（第一次就漏了）。改为**结构性归一**：
    #   把任意「非字母非数字非下划线」的字符（含各种空格、`›`、`>`、标点）**折叠为单个空格**。
    # 两侧（名单与观测）用**同一函数**归一 ⇒ 天然对齐；且脚本内**不再出现任何非 ASCII**。
    # 代价：仅以标点区分的两个用例名会被视为同一（对本用途可接受；且失败计数仍受名单长度约束）。
    $n = [regex]::Replace($Name, '[^\p{L}\p{N}_]+', ' ')
    return $n.Trim()
}

function Get-EvidencePaths {
    param([string]$Name)
    $ext = if ($Name -eq 'AdminJest') { 'jest.json' } else { 'trx' }
    $raw = Join-Path $logDir "$Name.raw.log"
    $structured = Join-Path $logDir "$Name.$ext"
    return @{ Raw = $raw; Structured = $structured; StructuredExt = $ext }
}

function Get-SuiteCommand {
    param([string]$Name, [hashtable]$Spec, [hashtable]$Evidence)
    if ($Spec.Kind -eq 'dotnet') {
        $filterArg = if ([string]::IsNullOrWhiteSpace($Spec.Filter)) { '' } else { "--filter `"$($Spec.Filter)`"" }
        # TRX 是**结构化、UTF-8、语言无关**的产物 ⇒ 判定只认它。
        $logger = "--logger `"trx;LogFileName=$Name.trx`""
        return "cd '$repoRoot'; dotnet test `"$($Spec.Project)`" $filterArg $logger --results-directory '$logDir' 2>&1 | Out-String"
    }
    # jest：--json + --outputFile ⇒ 精确计数与 fullName（`describe › test`），不再解析 `●` 启发式。
    return "cd '$repoRoot/$($Spec.Dir)'; npx jest --json --outputFile='$($Evidence.Structured)' 2>&1 | Out-String"
}

# ── 结构化读取（判定唯一依据）────────────────────────────────────────────────
function Read-TrxEvidence {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        [xml]$xml = Get-Content -LiteralPath $Path -Raw -Encoding utf8
        # ⚠️ TRX 的元素全在命名空间 `http://microsoft.com/schemas/VisualStudio/TeamTest/2010` 下，
        # 因此 `//ResultSummary/Counters` **永远匹配不到**（无前缀 XPath 不匹配命名空间元素）。
        # 2026-09-21 实测踩中：三套 dotnet 全部静默变成 UNMEASURED。改用 `local-name()`（命名空间无关）。
        $counters = $xml.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -eq $counters) { return $null }
        $failed = [int]$counters.GetAttribute('failed')
        $passed = [int]$counters.GetAttribute('passed')
        $names = New-Object System.Collections.Generic.List[string]
        foreach ($r in $xml.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='Failed']")) {
            $n = ConvertTo-NormalizedCaseName ([string]$r.GetAttribute('testName'))
            if ($n) { $names.Add($n) }
        }
        return @{ Counts = @{ Failed = $failed; Passed = $passed }; FailedNames = @($names | Select-Object -Unique) }
    }
    catch { return $null }
}

function Read-JestEvidence {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $doc = Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json
        $failed = [int]$doc.numFailedTests
        $passed = [int]$doc.numPassedTests
        $names = New-Object System.Collections.Generic.List[string]
        foreach ($suite in @($doc.testResults)) {
            foreach ($a in @($suite.assertionResults)) {
                if ($a.status -eq 'failed') {
                    $n = if ($a.fullName) { $a.fullName } else { $a.title }
                    $nn = ConvertTo-NormalizedCaseName ([string]$n)
                    if ($nn) { $names.Add($nn) }
                }
            }
        }
        $suiteFailures = [int]$doc.numFailedTestSuites
        return @{ Counts = @{ Failed = $failed; Passed = $passed }; FailedNames = @($names | Select-Object -Unique); FailedSuites = $suiteFailures }
    }
    catch { return $null }
}

# ── 判定函数（纯函数：主流程与 -SelfTest 共用同一份逻辑）────────────────────
function Resolve-SuiteVerdict {
    param(
        [hashtable]$Spec,
        [hashtable]$Observed,   # @{ Counts=$null|@{Failed;Passed}; FailedNames=@(); LockEvidence=[bool] }
        [datetime]$Today
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    $declared = $Spec.KnownRed
    if ($null -eq $declared) { $budget = $null } else { $budget = @($declared).Count }
    $knownRedStale = @()

    if ($null -eq $Observed.Counts) {
        if ($Observed.LockEvidence) { $status = 'SKIPPED_LOCKED' } else { $status = 'UNMEASURED' }
        $unexpected = @()
    }
    else {
        $unexpected = @()
        $hasFails = ($Observed.Counts.Failed -gt 0) -or (@($Observed.FailedNames).Count -gt 0)

        if ($null -eq $declared) {
            # 名单未登记：有失败就**不得声称通过**（这替换了旧的"仅按预算判"）。
            if ($hasFails) {
                $status = 'UNMEASURED'
                $reasons.Add('known_red_unregistered')
                $unexpected = @($Observed.FailedNames)
            }
            else {
                $status = 'PASS'
            }
        }
        else {
            $declaredNorm = @($declared | ForEach-Object { ConvertTo-NormalizedCaseName $_ })
            $unexpected = @($Observed.FailedNames | Where-Object { $declaredNorm -notcontains $_ })
            $knownRedStale = @($declaredNorm | Where-Object { @($Observed.FailedNames) -notcontains $_ })

            if ($unexpected.Count -gt 0) { $status = 'FAIL'; $reasons.Add('unexpected_failures') }
            elseif ($Observed.Counts.Failed -gt $declaredNorm.Count) { $status = 'FAIL'; $reasons.Add('count_exceeds_list') }
            else { $status = 'PASS' }

            # 名单里声明、本次未出现的用例 ⇒ 提示可以收紧（**任何状态下都提示**）：
            # 否则「修好了但名单没收缩」会长期占着预算位，慢慢把门禁变松。
            if ($knownRedStale.Count -gt 0) { $reasons.Add('known_red_stale') }

            # 名单保鲜：已知红不得变成永久豁免
            $maxAge = if ($Spec.KnownRedMaxAgeDays) { [int]$Spec.KnownRedMaxAgeDays } else { $KnownRedMaxAgeDaysDefault }
            if ($declaredNorm.Count -gt 0) {
                if ([string]::IsNullOrWhiteSpace($Spec.KnownRedMeasuredAt)) {
                    $status = 'FAIL'; $reasons.Add('known_red_measured_at_missing')
                }
                else {
                    $measured = [datetime]::Parse($Spec.KnownRedMeasuredAt)
                    if (($Today - $measured.Date).TotalDays -gt $maxAge) {
                        $status = 'FAIL'; $reasons.Add('known_red_expired')
                    }
                }
            }
        }
    }

    # 必测 / 豁免
    $required = if ($Spec.ContainsKey('Required')) { [bool]$Spec.Required } else { $true }
    $exemptButNotMeasured = $false
    if ($required) {
        if ($status -eq 'UNMEASURED' -or $status -eq 'SKIPPED_LOCKED') {
            $status = 'FAIL'; $reasons.Add('required_not_measured')
        }
    }
    else {
        if (-not $Spec.ContainsKey('Exemption') -or $null -eq $Spec.Exemption) {
            $status = 'FAIL'; $reasons.Add('exemption_missing')
        }
        else {
            if ([string]::IsNullOrWhiteSpace($Spec.Exemption.ExpiresOn)) {
                $status = 'FAIL'; $reasons.Add('exemption_expiry_missing')
            }
            elseif ($Today -gt [datetime]::Parse($Spec.Exemption.ExpiresOn).Date) {
                $status = 'FAIL'; $reasons.Add('exemption_expired')
            }
            elseif ($status -eq 'UNMEASURED' -or $status -eq 'SKIPPED_LOCKED') {
                $exemptButNotMeasured = $true   # 显式豁免且未到期：未测可接受，但**记录在案**
            }
        }
        if ($status -eq 'FAIL' -and $reasons.Contains('known_red_unregistered')) {
            # 非必测套件 + 名单未登记：不因"名单未登记"判死，但仍未测
            $status = 'UNMEASURED'
            $exemptButNotMeasured = $true
        }
    }

    return [pscustomobject]@{
        Status            = $status
        Reasons           = @($reasons)
        Unexpected        = @($unexpected)
        KnownRedStale     = @($knownRedStale)
        Budget            = $budget
        Required          = $required
        ExemptNotMeasured = $exemptButNotMeasured
    }
}

# ── 旧判据（仅用于 -SelfTest 对照：证明"旧脚本误放、新脚本拦住"）──────────────
function Resolve-SuiteVerdictLegacy {
    param([hashtable]$Spec, [hashtable]$Observed)
    if ($null -eq $Observed.Counts) {
        if ($Observed.LockEvidence) { return 'SKIPPED_LOCKED(exit0)' }
        return 'UNMEASURED(exit0)'
    }
    $legacyBudget = $Spec.LegacyAllowedFailures
    if ($null -eq $legacyBudget) {
        if ($null -eq $Spec.KnownRed) { $legacyBudget = 0 } else { $legacyBudget = @($Spec.KnownRed).Count }
    }
    if ($Observed.Counts.Failed -le $legacyBudget) { return 'PASS' }
    return 'FAIL'
}

function Get-SourceFingerprint {
    param([string]$RepoRoot)
    $head = ''
    $dirtyHash = 'n/a'
    $dirtyCount = 0
    try {
        $head = (& git -C $RepoRoot rev-parse --short HEAD 2>$null | Out-String).Trim()
        $porcelain = (& git -C $RepoRoot status --porcelain 2>$null | Out-String)
        $lines = @($porcelain -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
        $dirtyCount = $lines.Count
        if ($dirtyCount -gt 0) {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
            $dirtyHash = ([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 16)
        }
    }
    catch { }
    return [pscustomobject]@{ Head = $head; DirtyEntryCount = $dirtyCount; DirtyHash = $dirtyHash }
}

# ── 自检：合成负例（用同一判定函数）────────────────────────────────────────
function Invoke-SelfTest {
    Write-Host '==> 门禁判定函数自检（合成负例）'
    $t = [datetime]'2026-09-21'
    $specOk = @{ Kind = 'jest'; KnownRed = @('A', 'B', 'C'); KnownRedMeasuredAt = '2026-09-21' }
    $cases = @(
        @{ Id = 'N1 修一红换一新红'; Spec = $specOk; Obs = @{ Counts = @{ Failed = 3; Passed = 10 }; FailedNames = @('A', 'B', 'D'); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'unexpected_failures'; LegacyAllowed = 3 },
        @{ Id = 'N2 名单未登记(必测)'; Spec = @{ Kind = 'jest'; KnownRed = $null; KnownRedMeasuredAt = $null }; Obs = @{ Counts = @{ Failed = 3; Passed = 10 }; FailedNames = @('A', 'B', 'C'); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'known_red_unregistered'; LegacyAllowed = 3 },
        @{ Id = 'N3 已知红过期'; Spec = @{ Kind = 'jest'; KnownRed = @('A'); KnownRedMeasuredAt = '2026-08-01' }; Obs = @{ Counts = @{ Failed = 1; Passed = 10 }; FailedNames = @('A'); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'known_red_expired' },
        @{ Id = 'N4 必测套件被锁'; Spec = @{ Kind = 'dotnet'; KnownRed = @(); KnownRedMeasuredAt = '2026-09-21' }; Obs = @{ Counts = $null; FailedNames = @(); LockEvidence = $true }; Expect = 'FAIL'; ExpectReason = 'required_not_measured' },
        @{ Id = 'N5 必测套件未测'; Spec = @{ Kind = 'dotnet'; KnownRed = @(); KnownRedMeasuredAt = '2026-09-21' }; Obs = @{ Counts = $null; FailedNames = @(); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'required_not_measured' },
        @{ Id = 'N6 豁免过期'; Spec = @{ Kind = 'dotnet'; KnownRed = $null; Required = $false; Exemption = @{ ExpiresOn = '2026-09-20'; Reason = 'x'; Owner = 'x' } }; Obs = @{ Counts = $null; FailedNames = @(); LockEvidence = $true }; Expect = 'FAIL'; ExpectReason = 'exemption_expired' },
        @{ Id = 'N7 非必测但缺豁免声明'; Spec = @{ Kind = 'dotnet'; KnownRed = $null; Required = $false }; Obs = @{ Counts = $null; FailedNames = @(); LockEvidence = $true }; Expect = 'FAIL'; ExpectReason = 'exemption_missing' },
        @{ Id = 'N8 名单 3 例但只红 2 例'; Spec = $specOk; Obs = @{ Counts = @{ Failed = 2; Passed = 10 }; FailedNames = @('A', 'B'); LockEvidence = $false }; Expect = 'PASS'; ExpectReason = 'known_red_stale' },
        @{ Id = 'N9 失败超出名单'; Spec = @{ Kind = 'jest'; KnownRed = @('A'); KnownRedMeasuredAt = '2026-09-21' }; Obs = @{ Counts = @{ Failed = 2; Passed = 10 }; FailedNames = @('A', 'B'); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'unexpected_failures' },
        @{ Id = 'N10 全绿(名单为空)'; Spec = @{ Kind = 'dotnet'; KnownRed = @(); KnownRedMeasuredAt = '2026-09-21' }; Obs = @{ Counts = @{ Failed = 0; Passed = 10 }; FailedNames = @(); LockEvidence = $false }; Expect = 'PASS'; ExpectReason = $null },
        @{ Id = 'N11 结构化产物缺失'; Spec = @{ Kind = 'dotnet'; KnownRed = @(); KnownRedMeasuredAt = '2026-09-21' }; Obs = @{ Counts = $null; FailedNames = @(); LockEvidence = $false }; Expect = 'FAIL'; ExpectReason = 'required_not_measured' }
    )

    $failed = 0
    $rows = New-Object System.Collections.Generic.List[object]
    foreach ($c in $cases) {
        $spec = @{} + $c.Spec
        if ($c.ContainsKey('LegacyAllowed')) { $spec.LegacyAllowedFailures = $c.LegacyAllowed }
        $v = Resolve-SuiteVerdict -Spec $spec -Observed $c.Obs -Today $t
        $ok = ($v.Status -eq $c.Expect) -and ((-not $c.ExpectReason) -or ($v.Reasons -contains $c.ExpectReason))
        if (-not $ok) { $failed++ }
        $rows.Add([pscustomobject]@{
                Case     = $c.Id
                Expected = "$($c.Expect)$(if ($c.ExpectReason) { "($($c.ExpectReason))" })"
                Actual   = "$($v.Status)$(if ($v.Reasons.Count) { "($($v.Reasons -join ';'))" })"
                Legacy   = (Resolve-SuiteVerdictLegacy -Spec $spec -Observed $c.Obs)
                Result   = if ($ok) { 'ok' } else { 'SELFTEST-FAIL' }
            })
    }
    $rows | Format-Table -AutoSize | Out-String | Write-Host

    Write-Host '-- 对照（旧判据 vs 新判据）--'
    Write-Host '    N1 修一红换一新红：旧 PASS（3 <= 3 预算）／新 FAIL ⇒ 旧脚本会误放，新脚本拦住。'
    Write-Host '    N2 名单未登记：旧 PASS（3 <= 3 预算）／新 FAIL（known_red_unregistered）⇒ 名单未登记的必测套件不得声称通过。'
    Write-Host '    N4/N5 必测未测：旧 SKIPPED_LOCKED/UNMEASURED 且 **exit 0** ／新 FAIL ⇒ 未测不再等于通过。'
    Write-Host '    N11 结构化产物缺失：旧（文案正则抓不到时）静默计入未测／新 FAIL ⇒ 不再靠文案猜测。'

    if ($failed -gt 0) { Write-Host "SELFTEST: FAIL ($failed 例不符预期)"; return $false }
    Write-Host "SELFTEST: PASS（$($cases.Count) 例全部符合预期）"
    return $true
}

# ── 主流程 ──────────────────────────────────────────────────────────────────
if ($SelfTest) {
    $ok = Invoke-SelfTest
    if (-not $ok) { exit 1 }
    exit 0
}

$selected = if ([string]::IsNullOrWhiteSpace($Only)) { @($Suites.Keys) } else { $Only -split ',' | ForEach-Object { $_.Trim() } }
$unknown = $selected | Where-Object { -not $Suites.Contains($_) }
if ($unknown) { throw "未知套件：$($unknown -join ', ')（可用：$($Suites.Keys -join ', ')）" }

if ($ListOnly) {
    [pscustomobject]@{
        JudgementMode = 'structured-evidence (TRX/JSON) + case-identity + freshness + required/exemption'
        Suites        = $selected | ForEach-Object {
            $s = $Suites[$_]
            [pscustomobject]@{
                Name               = $_
                Kind               = $s.Kind
                Required           = if ($s.ContainsKey('Required')) { $s.Required } else { $true }
                Budget             = if ($null -eq $s.KnownRed) { $null } else { @($s.KnownRed).Count }
                KnownRedMeasuredAt = $s.KnownRedMeasuredAt
                BaselineCommit     = $s.BaselineCommit
                Note               = $s.Note
            }
        }
    } | ConvertTo-Json -Depth 4
    exit 0
}

$fingerprint = Get-SourceFingerprint -RepoRoot $repoRoot
Write-Host "==> 源码指纹：HEAD=$($fingerprint.Head) dirtyEntries=$($fingerprint.DirtyEntryCount) dirtyHash=$($fingerprint.DirtyHash)"

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $selected) {
    $spec = $Suites[$name]
    $evidence = Get-EvidencePaths -Name $name
    Write-Host "==> 运行套件 $name ..."
    $text = Invoke-Expression (Get-SuiteCommand -Name $name -Spec $spec -Evidence $evidence)
    $text | Set-Content -Path $evidence.Raw -Encoding utf8   # 原始人类可读证据（**不参与判定**）

    # 判定只认结构化产物；产物缺失/不可解析 ⇒ counts=$null（fail-closed，不回退到文案猜测）
    $parsed = if ($spec.Kind -eq 'dotnet') {
        Read-TrxEvidence -Path $evidence.Structured
    }
    else {
        Read-JestEvidence -Path $evidence.Structured
    }
    $counts = if ($parsed) { $parsed.Counts } else { $null }
    $failedNames = if ($parsed) { @($parsed.FailedNames) } else { @() }

    # 构建期文件锁（典型：Core 运行中锁定 Source/PuddingAgent/bin/Debug/net10.0/*.dll）
    # ⇒ 该套件**此刻无法测量**：既不算通过、也不算失败，单独报 SKIPPED_LOCKED，避免把"锁"误读成"红"。
    $lockEvidence = ($null -eq $counts) -and ($text -match 'MSB3027|MSB3021|being used by another process')

    $verdict = Resolve-SuiteVerdict -Spec $spec -Observed @{
        Counts       = $counts
        FailedNames  = $failedNames
        LockEvidence = $lockEvidence
    } -Today $today

    $results.Add([pscustomobject]@{
            Name              = $name
            Status            = $verdict.Status
            Failed            = if ($counts) { $counts.Failed } else { $null }
            Passed            = if ($counts) { $counts.Passed } else { $null }
            ObservedFailureNames = $failedNames
            Budget            = $verdict.Budget
            Required          = $verdict.Required
            Reasons           = $verdict.Reasons
            UnexpectedFailed  = $verdict.Unexpected
            KnownRedStale     = $verdict.KnownRedStale
            ExemptNotMeasured = $verdict.ExemptNotMeasured
            RawLog            = $evidence.Raw.Replace("$repoRoot/", '').Replace("$repoRoot\", '')
            StructuredEvidence = $evidence.Structured.Replace("$repoRoot/", '').Replace("$repoRoot\", '')
        })

    $line = "    $name : $($verdict.Status)"
    if ($counts) { $line += "  (failed=$($counts.Failed) passed=$($counts.Passed) budget=$($verdict.Budget))" }
    if ($verdict.Reasons.Count) { $line += "  reasons=$($verdict.Reasons -join ',')" }
    Write-Host $line
    if ($verdict.Unexpected.Count) { Write-Host "      预算外失败：$(($verdict.Unexpected -join ' | '))" }
    if ($verdict.KnownRedStale.Count) { Write-Host "      名单已可收紧（本次未出现）：$(($verdict.KnownRedStale -join ' | '))" }
}

$gateFailures = @($results | Where-Object { $_.Status -eq 'FAIL' })
$exemptUnmeasured = @($results | Where-Object { $_.ExemptNotMeasured })

# ── 算子架构门禁（S1b §5）：与套件判定并列的第二道门
# 为什么并入：算子抽象层的价值全在「契约层不依赖具体实现 / 不依赖具体供应商」这条边界上，
# 而这类破口在编译期完全合法、既有单元测试也不会红——只有架构门禁能拦住。
# 因此它不能是「一个可选脚本」：**非零退出必须传导为主门禁非零**（fail-closed），
# 否则守卫会退化成「没人跑就等于没有」。
$operatorsGateExit = 1
$operatorsGatePath = Join-Path $PSScriptRoot 'test-operators-architecture-gates.ps1'
if (Test-Path -LiteralPath $operatorsGatePath) {
    Write-Host '==> 运行算子架构门禁 test-operators-architecture-gates.ps1 ...'
    # 以**独立进程**运行：2026-09-21 实测——在「脚本调脚本」的嵌套场景下，
    # `& script.ps1` 内部的 `exit N` **不会**更新调用方的 $LASTEXITCODE，退出码会静默变成 0（守卫 fail-open）。
    # 原生进程退出码在「直接调用 / 嵌入管道 / 嵌套脚本」三种场景下均可靠（同一实测）。
    $pwshExe = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
    & $pwshExe -NoProfile -File $operatorsGatePath -RepoRoot $repoRoot
    $operatorsGateExit = $LASTEXITCODE
    Write-Host "    算子架构门禁退出码 = $operatorsGateExit（非零 ⇒ 主门禁非零）"
}
else {
    Write-Host "==> 算子架构门禁脚本缺失：$operatorsGatePath（fail-closed：按非零处理，避免守卫文件丢失后静默放行）"
}

$payload = [pscustomobject]@{
    CheckedAt         = (Get-Date).ToString('s')
    JudgementMode     = 'structured-evidence + case-identity + freshness + required/exemption'
    SourceFingerprint = $fingerprint
    Passed            = @($results | Where-Object { $_.Status -eq 'PASS' }).Count
    Failed            = $gateFailures.Count
    ExemptUnmeasured  = $exemptUnmeasured.Count
    OperatorsGateExit = $operatorsGateExit
    Suites            = $results
}
$payload | ConvertTo-Json -Depth 5
if ($gateFailures.Count -or $operatorsGateExit -ne 0) { exit 1 }
exit 0
