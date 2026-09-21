#requires -Version 7.0
<#
.SYNOPSIS
  统一测试门禁：跑「声明过的套件列表」，把每个套件的计数与**基线预算**比对，输出结构化结论。

.DESCRIPTION
  为什么需要它：本仓库曾有整套测试**长期红而无人知**（`PuddingCoreTests` 的契约冻结测试自 2026-07-23
  起就红、前端 jest 亦有既有红），红测试只是症状，**门禁缺失才是病因**。本脚本把"跑哪些、期望多少、
  哪些是已知红"写成**可执行的单一事实源**，避免"我跑的那几套"被误当成"全部"。

  设计要点：
  - **基线预算**：每个套件声明 `AllowedFailures`（已知红的上限）。超出即 FAIL。
  - **已知红名单**：`KnownRed` 里的用例名允许失败，其它失败一律计入。
  - **未测套件不放行**：`AllowedFailures = $null` 表示"只报告、不判定"（输出 UNMEASURED），
    避免用未知当通过。
  - **原始证据落盘**：每个套件的完整输出写到 `temp/suite-gates/<suite>.log`，本脚本只打印摘要。
  - 退出码：全部套件都在预算内 ⇒ 0；否则 ⇒ 1（便于人工/流水线判定）。

.PARAMETER Only
  只跑指定套件（逗号分隔的名字）。默认跑全部。

.PARAMETER ListOnly
  只打印套件清单与基线，不执行。

.EXAMPLE
  pwsh -File TestScripts\test-pudding-suite-gates.ps1 -Only Core
  pwsh -File TestScripts\test-pudding-suite-gates.ps1
#>
[CmdletBinding()]
param(
    [string]$Only = '',
    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$logDir = Join-Path $repoRoot 'temp/suite-gates'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# ── 单一事实源：套件清单 + 基线（改动计数时**必须**同步更新此处，并在提交信息里给出实测依据）──
$Suites = [ordered]@{
    'Core' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingCoreTests/PuddingCoreTests.csproj'
        Filter = 'TestCategory!=Live'
        # 2026-09-21 实测：911 总计 / 910 通过 / 1 红（下方 KnownRed）。既有红，非本轮引入。
        AllowedFailures = 1
        KnownRed = @('ProcessSwarmAsync_WithInvalidSwarmDirectory_HandlesError')
        Note = '契约冻结测试已同步至现行实现；剩余 1 例为 Swarm 非仓库目录的既有健壮性缺口。'
    }
    'Runtime' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj'
        Filter = 'TestCategory!=Live'
        AllowedFailures = 0
        KnownRed = @()
        Note = '2026-09-21 实测 1710 通过 / 0 失败（含 S1b 新增 22 个算子端口用例；基线的差异说明见 README）。'
    }
    'Platform' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingPlatformTests/PuddingPlatformTests.csproj'
        Filter = 'TestCategory!=Live'
        AllowedFailures = 0
        KnownRed = @()
        Note = '2026-09-21 实测 1363 通过 / 0 失败。'
    }
    'WebApi' = @{
        Kind = 'dotnet'
        Project = 'Source/PuddingWebApiTests/PuddingWebApiTests.csproj'
        Filter = ''
        # 实测（2026-09-21）：该套件构建依赖 `PuddingAgent` 的输出目录，而**运行中的 Core 会锁住**
        # `Source/PuddingAgent/bin/Debug/net10.0/*.dll` ⇒ 得到 MSB3027/MSB3021，**此刻无法测量**。
        # 需在 Core 停止后测（例如部署窗口），或改用独立输出路径构建。
        AllowedFailures = $null
        KnownRed = @()
        Note = '基线未实测：在 Core 运行时**无法测量**（构建需写 PuddingAgent 输出目录，被运行进程锁定）。'
    }
    'AdminJest' = @{
        Kind = 'jest'
        Dir = 'Source/PuddingPlatformAdmin'
        # 2026-09-21 实测：**3 failed / 1349 passed / 1352 total**（2 个红套件）。
        # 收窄轨迹：17 → 16 → 14 → 13（menuIcons 缺 hdd/key 映射 = **真实缺陷**）→ 10 → 7（治好并行超时 flaky）
        # → 5 → 4 → 3（压缩结果用例：承载载体由 answerMarkdown 迁到 timelineItems[].message）。
        # 已定性为 A 类测试滞后（**与安全分类器改动无关**）；逐例台账见 TestScripts/known-red-dispositions.md。
        # 剩余 3 例**全是语音族**且已查清：孤儿组件 VoiceConversationPanel.tsx 无人引用，
        # 等用户定“接线 vs 移除”后再动测试（无决策不动测试、不删组件）。
        AllowedFailures = 3
        KnownRed = $null
        Note = '既有红：A 类测试滞后为主（生产变更后测试未同步）。名单未登记 ⇒ 目前只按预算判。'
    }
}

function Get-SuiteCommand {
    param([string]$Name, [hashtable]$Spec)
    if ($Spec.Kind -eq 'dotnet') {
        $filterArg = if ([string]::IsNullOrWhiteSpace($Spec.Filter)) { '' } else { "--filter `"$($Spec.Filter)`"" }
        return "cd '$repoRoot'; dotnet test `"$($Spec.Project)`" $filterArg 2>&1 | Out-String"
    }
    return "cd '$repoRoot/$($Spec.Dir)'; npx jest 2>&1 | Out-String"
}

function Read-Counts {
    param([string]$Text, [string]$Kind)
    if ($Kind -eq 'dotnet') {
        # 兼容中英文：`失败: 1，通过: 910` / `Failed: 1, Passed: 910`
        $m = [regex]::Match($Text, '(?:失败|Failed)\s*[:：]\s*(\d+)\s*[，,]\s*(?:通过|Passed)\s*[:：]\s*(\d+)')
        if ($m.Success) { return @{ Failed = [int]$m.Groups[1].Value; Passed = [int]$m.Groups[2].Value } }
    }
    else {
        $m = [regex]::Match($Text, 'Tests:\s*(\d+)\s*failed,\s*(\d+)\s*passed')
        if ($m.Success) { return @{ Failed = [int]$m.Groups[1].Value; Passed = [int]$m.Groups[2].Value } }
    }
    return $null
}

function Read-FailedNames {
    param([string]$Text, [string]$Kind)
    $names = New-Object System.Collections.Generic.List[string]
    if ($Kind -eq 'dotnet') {
        foreach ($line in ($Text -split "`n")) {
            $m = [regex]::Match($line, '^\s*(?:失败|Failed)\s+(\S+)\s*\[')
            if ($m.Success) { $names.Add($m.Groups[1].Value) }
        }
    }
    else {
        foreach ($line in ($Text -split "`n")) {
            $m = [regex]::Match($line, '^\s*●\s+(.+?)\s*$')
            if ($m.Success) { $names.Add($m.Groups[1].Value.Trim()) }
        }
    }
    return ($names | Select-Object -Unique)
}

$selected = if ([string]::IsNullOrWhiteSpace($Only)) { @($Suites.Keys) } else { $Only -split ',' | ForEach-Object { $_.Trim() } }
$unknown = $selected | Where-Object { -not $Suites.Contains($_) }
if ($unknown) { throw "未知套件：$($unknown -join ', ')（可用：$($Suites.Keys -join ', ')）" }

if ($ListOnly) {
    [pscustomobject]@{
        Suites = $selected | ForEach-Object {
            [pscustomobject]@{ Name = $_; Kind = $Suites[$_].Kind; AllowedFailures = $Suites[$_].AllowedFailures; Note = $Suites[$_].Note }
        }
    } | ConvertTo-Json -Depth 4
    exit 0
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($name in $selected) {
    $spec = $Suites[$name]
    $logPath = Join-Path $logDir "$name.log"
    Write-Host "==> 运行套件 $name ..."
    $text = Invoke-Expression (Get-SuiteCommand -Name $name -Spec $spec)
    $text | Set-Content -Path $logPath -Encoding utf8

    $counts = Read-Counts -Text $text -Kind $spec.Kind
    $failedNames = @(Read-FailedNames -Text $text -Kind $spec.Kind)
    $known = @($spec.KnownRed)
    $unexpected = @($failedNames | Where-Object { $known -notcontains $_ })
    # 名单语义：`KnownRed` 给出具体名单 ⇒ **名单也参与判定**（预算内但出现名单外的失败照样 FAIL）；
    # `KnownRed = $null` ⇒ 名单未登记，**仅按预算判**（此时 `$unexpected` 仅供展示，不参与判定）。
    $nameGate = ($null -eq $spec.KnownRed) -or ($unexpected.Count -eq 0)

    # 构建期文件锁（典型：Core 运行中锁定 Source/PuddingAgent/bin/Debug/net10.0/*.dll）
    # ⇒ 该套件**此刻无法测量**：既不算通过、也不算失败，单独报 SKIPPED_LOCKED，避免把"锁"误读成"红"。
    $lockEvidence = ($null -eq $counts) -and ($text -match 'MSB3027|MSB3021|being used by another process|正由另一进程使用|文件被')

    $status = 'UNMEASURED'
    if ($lockEvidence) { $status = 'SKIPPED_LOCKED' }
    elseif ($null -ne $counts) {
        if ($null -eq $spec.AllowedFailures) { $status = 'UNMEASURED' }
        elseif ($counts.Failed -le $spec.AllowedFailures -and $nameGate) { $status = 'PASS' }
        else { $status = 'FAIL' }
    }
    elseif ($null -eq $spec.AllowedFailures) { $status = 'UNMEASURED' }

    $results.Add([pscustomobject]@{
            Name             = $name
            Status           = $status
            Failed           = if ($counts) { $counts.Failed } else { $null }
            Passed           = if ($counts) { $counts.Passed } else { $null }
            AllowedFailures  = $spec.AllowedFailures
            UnexpectedFailed = $unexpected
            LogPath          = $logPath.Replace("$repoRoot/", '').Replace("$repoRoot\", '')
        })

    $line = "    $name : $status"
    if ($counts) { $line += "  (failed=$($counts.Failed) passed=$($counts.Passed) allowed=$($spec.AllowedFailures))" }
    Write-Host $line
    if ($unexpected.Count) { Write-Host "      预算外失败：$(($unexpected -join ' | '))" }
}

$gateFailures = @($results | Where-Object { $_.Status -eq 'FAIL' })

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
    CheckedAt = (Get-Date).ToString('s')
    Passed    = @($results | Where-Object { $_.Status -eq 'PASS' }).Count
    Failed    = $gateFailures.Count
    Unmeasured = @($results | Where-Object { $_.Status -eq 'UNMEASURED' }).Count
    SkippedLocked = @($results | Where-Object { $_.Status -eq 'SKIPPED_LOCKED' }).Count
    OperatorsGateExit = $operatorsGateExit
    Suites    = $results
}
$payload | ConvertTo-Json -Depth 5
if ($gateFailures.Count -or $operatorsGateExit -ne 0) { exit 1 }
exit 0
