param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/plugin-agent-api/$RunId"
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
function Run-Tests([string]$Name, [switch]$UseAppHost) {
    # Use the same apphost launch form for the test controller and real Worker.
    if ($UseAppHost) {
        Invoke-Checked $Name { & "$artifacts/bin/$Name/debug/$Name.exe" --no-ansi --no-progress --results-directory "$run/$Name-results" }
    } else {
        Invoke-Checked $Name { dotnet "$artifacts/bin/$Name/debug/$Name.dll" --no-ansi --no-progress --results-directory "$run/$Name-results" }
    }
}
$previousRoot = $env:PUDDING_PLUGIN_TEST_ROOT
$previousWorker = $env:PUDDING_PLUGIN_WORKER
$previousFixture = $env:PUDDING_PLUGIN_FIXTURE
Push-Location $repo
try {
    foreach ($name in @('PuddingAgentDelegationTests', 'Pudding.ToolPlugins.AgentSdkTests', 'Pudding.ToolPlugins.RuntimeTests')) {
        Build-Project "Source/$name/$name.csproj" "build-$name"
        Run-Tests $name
    }
    Build-Project 'Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj' 'build-worker'
    Build-Project 'Tests/PuddingPluginAgentApi.IntegrationTests/PuddingPluginAgentApi.IntegrationTests.csproj' 'build-agent-api-integration'
    Build-Project 'Tests/PuddingToolPlugins.IntegrationTests/PuddingToolPlugins.IntegrationTests.csproj' 'build-worker-regression'
    $env:PUDDING_PLUGIN_TEST_ROOT = $run
    $env:PUDDING_PLUGIN_WORKER = "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe"
    $env:PUDDING_PLUGIN_FIXTURE = "$artifacts/bin/PuddingToolPlugins.Fixture/debug/Sample.PluginFixture.dll"
    Run-Tests 'PuddingPluginAgentApi.IntegrationTests' -UseAppHost
    Run-Tests 'PuddingToolPlugins.IntegrationTests' -UseAppHost
    Write-Host "Plugin Agent API acceptance passed. Evidence: $run"
}
finally {
    $env:PUDDING_PLUGIN_TEST_ROOT = $previousRoot
    $env:PUDDING_PLUGIN_WORKER = $previousWorker
    $env:PUDDING_PLUGIN_FIXTURE = $previousFixture
    Pop-Location
}
