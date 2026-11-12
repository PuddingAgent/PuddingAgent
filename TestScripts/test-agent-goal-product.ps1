<#
.SYNOPSIS
  PA-08C 真实模型/插件 smoke 的显式门禁脚本（默认 NotRun）。
  本脚本不会猜测配置、不会读取 D:\data 的 LLM secret、不会自动部署或重启宿主。

.DESCRIPTION
  只有同时提供以下环境变量时才允许进入“已配置”分支：
    PUDDING_PA08_TEST_AGENT_ID        测试 Agent 标识
    PUDDING_PA08_TEST_WORKSPACE_ID    测试 Workspace 标识
    PUDDING_PA08_TEST_PROVIDER        测试 provider/profile 标识
    PUDDING_PA08_MAX_BUDGET_USD       有上限的根预算（正数美元）
  缺少任一项 → 输出 NotRun 并 exit 2（缺测试条件，不是失败）。
  全部提供但产品态 smoke 尚未获授权/实现 → 输出 NotRun 并 exit 3（明确未执行）。
  真实执行前必须由架构师授权（08B/08C 暂未授权），脚本本身不启动 Core/Desktop。

.EXAMPLE
  pwsh -NoProfile -File TestScripts/test-agent-goal-product.ps1
#>
param()

$ErrorActionPreference = 'Stop'
$required = [ordered]@{
    'PUDDING_PA08_TEST_AGENT_ID' = '测试 Agent 标识'
    'PUDDING_PA08_TEST_WORKSPACE_ID' = '测试 Workspace 标识'
    'PUDDING_PA08_TEST_PROVIDER' = '测试 provider/profile 标识'
    'PUDDING_PA08_MAX_BUDGET_USD' = '有上限的根预算（正数美元）'
}
$missing = @()
foreach ($name in $required.Keys) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($value)) { $missing += "$name ($($required[$name]))" }
}
if ($missing.Count -gt 0) {
    Write-Output 'NotRun: PA-08C real model/plugin smoke requires explicit test configuration.'
    Write-Output 'Missing:'
    $missing | ForEach-Object { Write-Output "  - $_" }
    Write-Output 'See Docs/12_features/Agent组件化任务书-08-可见性与产品验收-2026-11-05.md §08C.'
    exit 2
}

$budget = 0.0
if (-not [double]::TryParse([Environment]::GetEnvironmentVariable('PUDDING_PA08_MAX_BUDGET_USD'),
        [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$budget) -or
    $budget -le 0 -or $budget -gt 100) {
    Write-Output 'NotRun: PUDDING_PA08_MAX_BUDGET_USD must be a positive value <= 100.'
    exit 2
}

Write-Output 'NotRun: PA-08C real model/plugin smoke is not authorized in this session.'
Write-Output '  Test configuration was provided, but 08B/08C remain NotRun until the architect authorizes deployment/real-model execution.'
Write-Output '  This script does not start, restart or deploy Core/Desktop, and does not read credentials from D:\data.'
exit 3
