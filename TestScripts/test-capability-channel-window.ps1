<#
.SYNOPSIS
    能力通道重启窗口检查（切片 C-3 验收的可执行部分）。

.DESCRIPTION
    只做「不需要人判断」的检查，且**不触碰运行中的进程**：
      1. 读 <DataRoot>/config/system.json，确认能力通道开关与传输配置（只打印形态，绝不打印凭据）；
      2. 用探针的 `--endpoint <描述> --dry-run` 验证就绪描述可被 Desktop 严格解析（可在重启前先跑）；
      3. 打印仍需外部控制器执行的手工步骤（重启、拨入、断连、回滚），不假装已完成。

    设计原则：脚本只报告**它真的验证过**的东西；无法自动化的步骤一律显式列为"需人工/外部控制器"，
    并以退出码 2 表示"尚未完成窗口流程"，避免被误读为通过。

.PARAMETER DataRoot
    Core 的 DataRoot（含 config/system.json）。缺省取 $env:PUDDING_DATA_ROOT，再缺省 D:\data。

.PARAMETER Endpoint
    Core 发布的能力端点描述（例如 named-pipe:pudding-capability-xxxx|1|core-123）。
    缺省时跳过第 2 步并提示如何获取。

.PARAMETER ProbeDll
    已构建探针的路径；缺省在 temp\build 下自动查找最新的一份。

.EXAMPLE
    pwsh -File TestScripts\test-capability-channel-window.ps1 -DataRoot D:\data -Endpoint "named-pipe:pudding-capability-abc|1|core-1"
#>
[CmdletBinding()]
param(
    [string]$DataRoot,
    [string]$Endpoint,
    [string]$ProbeDll
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $DataRoot) {
    if ($env:PUDDING_DATA_ROOT) { $DataRoot = $env:PUDDING_DATA_ROOT } else { $DataRoot = 'D:\data' }
}

$results = New-Object System.Collections.Generic.List[object]
function Add-Result([string]$Step, [string]$Status, [string]$Detail) {
    $results.Add([pscustomobject]@{ Step = $Step; Status = $Status; Detail = $Detail })
}

Write-Host "=== 能力通道重启窗口检查 ===" -ForegroundColor Cyan
Write-Host "DataRoot : $DataRoot"

# ── 步骤 1：配置形态（只读，不打印凭据） ─────────────────────────────
$configPath = Join-Path $DataRoot 'config\system.json'
if (-not (Test-Path -LiteralPath $configPath)) {
    Add-Result '配置存在' 'FAIL' "未找到 $configPath"
}
else {
    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        $section = $null
        if ($config.Desktop -and $config.Desktop.CapabilityChannel) { $section = $config.Desktop.CapabilityChannel }

        if ($null -eq $section) {
            Add-Result '能力通道开关' 'INFO' '未配置 Desktop:CapabilityChannel ⇒ 保持关闭（行为与今天一致；这是合法状态）'
        }
        elseif ($section.Enabled -ne $true) {
            Add-Result '能力通道开关' 'INFO' 'Enabled 非 true ⇒ 通道关闭（回滚状态）'
        }
        else {
            $transport = if ($section.Transport) { $section.Transport } else { 'named-pipe（缺省）' }
            Add-Result '能力通道开关' 'PASS' "Enabled=true；Transport=$transport（不打印任何凭据）"
        }
    }
    catch {
        Add-Result '配置解析' 'FAIL' "system.json 解析失败：$($_.Exception.Message)"
    }
}

# ── 步骤 2：就绪描述可解析（探针 dry-run；不连接、不启动服务端） ──────
if (-not $ProbeDll) {
    # 必须排除 obj\ 下的引用程序集（ref/refint）：它们不可运行（缺 hostpolicy/runtimeconfig）。
    $candidates = Get-ChildItem -Path (Join-Path $repoRoot 'temp\build') -Recurse -Filter 'Pudding.Rpc.IpcProbe.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\obj\\' } |
        Sort-Object LastWriteTime -Descending
    if ($candidates) { $ProbeDll = $candidates[0].FullName }
}

if (-not $Endpoint) {
    Add-Result '端点描述解析' 'SKIP' '未提供 -Endpoint（重启后从就绪输出/日志取得后重跑本步骤）'
}
elseif (-not $ProbeDll -or -not (Test-Path -LiteralPath $ProbeDll)) {
    Add-Result '端点描述解析' 'SKIP' '未找到已构建的探针（先 dotnet build Source\Pudding.Rpc.IpcProbe）'
}
else {
    # 原生命令的 stderr 在 ErrorActionPreference=Stop 下会变成终止错误（PS 5.1 行为），
    # 而探针失败时正是把 FAIL 写到 stderr ⇒ 局部降级为 Continue，用退出码判成败。
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $output = & dotnet $ProbeDll --endpoint $Endpoint --dry-run 2>&1
    $exit = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    $conclusion = ($output | Select-String -Pattern '结论' | Select-Object -Last 1).Line
    if ($exit -eq 0) { Add-Result '端点描述解析' 'PASS' $conclusion }
    else {
        # 失败时带上可诊断信息：探针路径、退出码、输出首行（不含凭据）。
        $first = ($output | Select-Object -First 1)
        Add-Result '端点描述解析' 'FAIL' "exit=$exit；探针=$ProbeDll；输出首行=$first"
    }
}

# ── 步骤 3：仍需外部控制器执行的部分（不假装完成） ───────────────────
Add-Result '重启并观察绑定' 'MANUAL' '重启 Core 后调用 CapabilityChannelPreflight.Check(地址列表, 期望 REST 地址, Describe(...))，IsHealthy 必须为真'
Add-Result 'Desktop 拨入' 'MANUAL' '桌面启动后应握手成功（世代≥1、能力交集符合 Grantable）'
Add-Result '无凭据拒绝' 'MANUAL' '缺少/错误 ControlToken 的连接必须被拒，且不产生会话'
Add-Result '断连收尾' 'MANUAL' 'Desktop 退出后 Core 注册表清空、管道释放、Core 存活'
Add-Result '回滚演练' 'MANUAL' 'Enabled=false 重启后回到"通道关闭"状态'

# ── 汇总 ─────────────────────────────────────────────────────────────
Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host

$failed = 0
$manual = 0
$passed = 0
foreach ($result in $results) {
    if ($result.Status -eq 'FAIL') { $failed = $failed + 1 }
    elseif ($result.Status -eq 'MANUAL') { $manual = $manual + 1 }
    elseif ($result.Status -eq 'PASS') { $passed = $passed + 1 }
}

if ($failed -gt 0) {
    Write-Host "结论：FAIL（$failed 项失败）" -ForegroundColor Red
    exit 1
}

if ($manual -gt 0) {
    Write-Host "结论：自动部分通过（$passed 项），窗口流程尚未完成（$manual 项需外部控制器执行）" -ForegroundColor Yellow
    exit 2
}

Write-Host "结论：自动部分全部通过（$passed 项）" -ForegroundColor Green
exit 0
