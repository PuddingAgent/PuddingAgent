param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/operator-api/$RunId"
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
Push-Location $repo
try {
    foreach ($name in @('PuddingOperatorAccessTests', 'Pudding.Api.SdkTests', 'Pudding.ToolPlugins.AgentSdkTests', 'Pudding.ToolPlugins.RuntimeTests')) {
        Build-Project "Source/$name/$name.csproj" "build-$name"
        Invoke-Checked $name { dotnet "$artifacts/bin/$name/debug/$name.dll" --no-ansi --no-progress --results-directory "$run/$name-results" }
    }
    Build-Project 'Source/Pudding.ToolPlugins.Worker/Pudding.ToolPlugins.Worker.csproj' 'build-worker'
    Build-Project 'Tests/PuddingOperatorApi.IntegrationTests/PuddingOperatorApi.IntegrationTests.csproj' 'build-integration'
    $env:PUDDING_PLUGIN_TEST_ROOT = $run
    $env:PUDDING_PLUGIN_WORKER = "$artifacts/bin/Pudding.ToolPlugins.Worker/debug/Pudding.ToolPlugins.Worker.exe"
    # Test controller and real Worker use the same Windows apphost launch form.
    Invoke-Checked 'integration' { & "$artifacts/bin/PuddingOperatorApi.IntegrationTests/debug/PuddingOperatorApi.IntegrationTests.exe" --no-ansi --no-progress --results-directory "$run/integration-results" }
    Write-Host "Operator API acceptance passed. Evidence: $run"
}
finally {
    $env:PUDDING_PLUGIN_TEST_ROOT = $previousRoot
    $env:PUDDING_PLUGIN_WORKER = $previousWorker
    Pop-Location
}
