<#
.SYNOPSIS
  PA-08A 离线组件验收：构建并运行八份任务书的独立组件与 PA-01/03/06/07 S5 持久化切片。
  默认使用隔离 artifacts 目录，不接触运行中的 Core/Desktop，不读取真实凭据，不调用真实模型。

.DESCRIPTION
  每个工程：dotnet build --artifacts-path <run>/<project>/artifacts → 只在构建成功后运行该产物 DLL。
  任一步非零立即停止并记录命令；不使用 catch 伪装总成功。

.PARAMETER RunId
  证据目录标识，默认按时间生成；输出位于 .work/pa08/components-<RunId>/。

.EXAMPLE
  pwsh -NoProfile -File TestScripts/test-agent-goal-components.ps1
#>
param(
    [string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'),
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = Join-Path $repoRoot ".work/pa08/components-$RunId"
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$summary = Join-Path $runRoot 'summary.txt'

function Invoke-Project {
    param([string]$Name, [string]$ProjectPath, [string]$Filter)
    $logDir = Join-Path $runRoot $Name
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $artifacts = Join-Path $logDir 'artifacts'
    dotnet build $ProjectPath --artifacts-path $artifacts `
        -p:UseVSTest=false -p:CollectCoverage=false --nologo *> (Join-Path $logDir 'build.log')
    if ($LASTEXITCODE -ne 0) {
        throw "BUILD FAILED: $Name (exit $LASTEXITCODE). Log: $(Join-Path $logDir 'build.log')"
    }
    $dll = Get-ChildItem -Path (Join-Path $artifacts 'bin') -Recurse -Filter "$Name.dll" |
        Select-Object -First 1
    if (-not $dll) { throw "MISSING TEST DLL: $Name under $artifacts" }
    $arguments = @($dll.FullName, '--no-ansi', '--no-progress', '--results-directory',
        (Join-Path $logDir 'results'))
    if ($Filter) { $arguments += @('--filter', $Filter) }
    & dotnet @arguments *> (Join-Path $logDir 'test.log')
    if ($LASTEXITCODE -ne 0) {
        throw "TEST FAILED: $Name (exit $LASTEXITCODE). Log: $(Join-Path $logDir 'test.log')"
    }
    $stats = (Select-String -Path (Join-Path $logDir 'test.log') -Pattern '总计:|失败:|成功:|已跳过:' |
        ForEach-Object { $_.Line.Trim() }) -join ' | '
    Add-Content -Path $summary -Value "$Name`t$stats" -Encoding utf8
}

try {
    $projects = @(
        'PuddingExecutionResourcesTests',
        'PuddingAgentsTests',
        'PuddingAgentWorkTests',
        'PuddingReviewsTests',
        'PuddingGoalPolicyTests',
        'PuddingCapabilityCatalogTests',
        'PuddingModelsTests',
        'PuddingEventingTests',
        'PuddingAuthorizationTests',
        'PuddingWorkspaceTests',
        'PuddingContextTests',
        'PuddingMcpTests',
        'PuddingToolsTests',
        'PuddingInvocationPipelineTests',
        'Pudding.ToolPlugins.AgentSdkTests',
        'Pudding.Api.SdkTests'
    )
    foreach ($project in $projects) {
        $dir = Join-Path $repoRoot "Source/$project"
        $csproj = Get-ChildItem -LiteralPath $dir -Filter '*.csproj' | Select-Object -First 1
        if (-not $csproj) { throw "MISSING PROJECT: Source/$project" }
        Invoke-Project -Name $project -ProjectPath $csproj.FullName -Filter $null
    }

    $platformTests = Join-Path $repoRoot 'Source/PuddingPlatformTests/PuddingPlatformTests.csproj'
    Invoke-Project -Name 'PuddingPlatformTests' -ProjectPath $platformTests `
        -Filter 'ExecutionBudget|AgentModelPolicyMapper|RuntimeBudgetBoundary|PuddingToolCatalogAdapter|GoalReviewCompletionGate|ReviewJobStore|ModelAttemptBudgetGate|BudgetBoundaryClosure|WorkPublicClosure|BudgetBoundaryClosure|PlatformAgentWorkControlApi|PlatformAgentFactoryApi|WorkspaceAgentAuthoringBackend|AgentDirectoryPublicApi|DurableModelResourcePool|GoalReviewSubmission|PlatformModelCatalogApi|GoalReviewCompletionGate|PlatformReviewModelProvider|LlmConfigModelCatalog|ReviewModelLeaseSource|WorkspaceAgentModelPolicyStore|AgentModelPolicyApi|ReviewJob|ReviewBudgetReconciliation|ReviewModelRouter|ReviewEvaluationBinder|ReviewEvent|ReviewExecutor|ReviewEvidence|ReviewOutbox|EventOutbox|OutboxUnitOfWork|PluginEventDispatch|ModelAttemptBudget|GoalReviewObjective|ConversationReviewCompletion|ExecutionReview|PluginJob|SubscriptionStore|EventDelivery|ModelCatalog|CatalogModelQuote|SchemaBootstrappers|ReviewPipeline'

    if (-not $SkipFrontend) {
        $frontendDir = Join-Path $repoRoot 'Source/PuddingPlatformAdmin'
        $nodeModules = Join-Path $frontendDir 'node_modules'
        if (-not (Test-Path -LiteralPath $nodeModules)) {
            Write-Output 'NotRun: frontend checks require Source/PuddingPlatformAdmin/node_modules (run pnpm install first).'
            exit 3
        }
        $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
        if (-not $pnpm) {
            Write-Output 'NotRun: frontend checks require pnpm on PATH.'
            exit 3
        }
        $frontendLog = Join-Path $runRoot 'frontend'
        New-Item -ItemType Directory -Force -Path $frontendLog | Out-Null
        Push-Location -LiteralPath $frontendDir
        try {
            $jestTargets = @(
                'src/pages/chat/components/ExecutionReviewPanel.test.tsx',
                'src/pages/chat/components/ExecutionReviewSection.test.tsx',
                'src/pages/chat/components/GoalBanner.test.tsx',
                'src/pages/chat/hooks/useExecutionReview.test.ts',
                'src/pages/chat/utils/executionReviewView.test.ts'
            )
            & pnpm exec jest @jestTargets --runInBand *> (Join-Path $frontendLog 'jest.log')
            if ($LASTEXITCODE -ne 0) {
                throw "FRONTEND JEST FAILED (exit $LASTEXITCODE). Log: $(Join-Path $frontendLog 'jest.log')"
            }
            & pnpm exec tsc --noEmit *> (Join-Path $frontendLog 'tsc.log')
            if ($LASTEXITCODE -ne 0) {
                throw "FRONTEND TSC FAILED (exit $LASTEXITCODE). Log: $(Join-Path $frontendLog 'tsc.log')"
            }
        }
        finally {
            Pop-Location
        }
        $jestStats = (Select-String -Path (Join-Path $frontendLog 'jest.log') -Pattern 'Tests:|Test Suites:' |
            ForEach-Object { $_.Line.Trim() }) -join ' | '
        Add-Content -Path $summary -Value "frontend (Jest/tsc)`t$jestStats" -Encoding utf8
    }
    Write-Output "PASS: PA-08A offline components ($RunId). Evidence: $runRoot"
    Get-Content -Path $summary
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    Add-Content -Path $summary -Value "FAILED: $($_.Exception.Message)" -Encoding utf8
    exit 1
}
