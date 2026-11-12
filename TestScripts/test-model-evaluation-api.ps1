param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/model-operator-api/$RunId"
$artifacts = Join-Path $run 'artifacts'
New-Item -ItemType Directory -Force $run | Out-Null
function Invoke-Checked([string]$Name, [scriptblock]$Action) {
    & $Action > (Join-Path $run "$Name.log") 2>&1
    if ($LASTEXITCODE -ne 0) { throw "$Name failed. See $run/$Name.log" }
    Write-Host "$Name passed"
}
function Build-Project([string]$Project, [string]$Name) {
    Invoke-Checked $Name { dotnet build $Project --artifacts-path $artifacts -p:UseVSTest=false -p:CollectCoverage=false --nologo -m:2 }
}
$previousRoot = $env:PUDDING_PLUGIN_TEST_ROOT
$previousWorker = $env:PUDDING_PLUGIN_WORKER
$previousFixture = $env:PUDDING_PLUGIN_FIXTURE
Push-Location $repo
try {
    foreach ($name in @('PuddingExecutionResourcesTests', 'PuddingModelEvaluationTests', 'Pudding.Api.SdkTests', 'Pudding.ToolPlugins.AgentSdkTests', 'Pudding.ToolPlugins.RuntimeTests')) {
        Build-Project "Source/$name/$name.csproj" "build-$name"
        Invoke-Checked $name { dotnet "$artifacts/bin/$name/debug/$name.dll" --no-ansi --no-progress --results-directory "$run/$name-results" }
    }
    Build-Project 'Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj' 'build-worker'
    $env:PUDDING_PLUGIN_TEST_ROOT = $run
    $env:PUDDING_PLUGIN_WORKER = "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe"
    $env:PUDDING_PLUGIN_FIXTURE = "$artifacts/bin/PuddingToolPlugins.Fixture/debug/Sample.PluginFixture.dll"
    foreach ($name in @('PuddingModelEvaluation.IntegrationTests', 'PuddingOperatorApi.IntegrationTests', 'PuddingPluginAgentApi.IntegrationTests', 'PuddingToolPlugins.IntegrationTests')) {
        Build-Project "Tests/$name/$name.csproj" "build-$name"
        Invoke-Checked $name { & "$artifacts/bin/$name/debug/$name.exe" --no-ansi --no-progress --results-directory "$run/$name-results" }
    }
    Build-Project 'Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj' 'build-runtime-tests'
    Invoke-Checked 'parent-budget-tests' { dotnet "$artifacts/bin/PuddingRuntimeTests/debug/PuddingRuntimeTests.dll" --no-ansi --no-progress --filter 'FullyQualifiedName~ExecutionUsageBudgetTrackerTests|FullyQualifiedName~ToolInvocationAmbientTraceTests' --results-directory "$run/parent-budget-results" }
    Write-Host "Model evaluation and existing plugin regressions passed. Evidence: $run"
}
finally {
    $env:PUDDING_PLUGIN_TEST_ROOT = $previousRoot
    $env:PUDDING_PLUGIN_WORKER = $previousWorker
    $env:PUDDING_PLUGIN_FIXTURE = $previousFixture
    Pop-Location
}
