param([string]$RunId = (Get-Date -Format 'yyyyMMdd-HHmmss'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if ($RunId -notmatch '^[a-zA-Z0-9_-]+$') { throw 'RunId must be a simple directory name.' }
$run = Join-Path $repo ".work/i1-06-mutation/$RunId"
$artifacts = Join-Path $run 'artifacts'
New-Item -ItemType Directory -Force $run | Out-Null
New-Item -ItemType Directory -Force (Join-Path $run 'temp') | Out-Null
$target = Join-Path $repo 'Source/PuddingRuntime/Services/LlmInvocationService.cs'
$client = Join-Path $repo 'Source/PuddingRuntime/Services/DirectLlmClient.cs'
$originalTarget = [IO.File]::ReadAllText($target)
$originalClient = [IO.File]::ReadAllText($client)
$targetHash = (Get-FileHash $target -Algorithm SHA256).Hash
$clientHash = (Get-FileHash $client -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $run 'LlmInvocationService.cs.orig'), $originalTarget, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $run 'DirectLlmClient.cs.orig'), $originalClient, [Text.UTF8Encoding]::new($false))
function Build-Tests {
    dotnet build "$repo/Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj" --artifacts-path $artifacts `
        -p:UseVSTest=false --nologo > (Join-Path $run 'build.log') 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'RuntimeTests build failed.' }
}
function Run-TransportTests([string]$Name) {
    $dll = Join-Path $artifacts 'bin/PuddingRuntimeTests/debug/PuddingRuntimeTests.dll'
    & dotnet $dll --no-ansi --no-progress --results-directory (Join-Path $run "$Name-results") `
        --filter 'TransportAttemptBudget' > (Join-Path $run "$Name.log") 2>&1
    return $LASTEXITCODE
}
function Assert-MutationDetected([string]$Name, [string]$MutatedText, [string]$Path, [string]$Original,
    [string]$ExpectedHash) {
    [IO.File]::WriteAllText($Path, $MutatedText, [Text.UTF8Encoding]::new($false))
    try {
        Build-Tests
        $exit = Run-TransportTests $Name
        if ($exit -eq 0) { throw "$Name was NOT detected: transport tests still passed." }
        Write-Host "$Name detected (test exit $exit)"
    }
    finally {
        [IO.File]::WriteAllText($Path, $Original, [Text.UTF8Encoding]::new($false))
        if ((Get-FileHash $Path -Algorithm SHA256).Hash -ne $ExpectedHash) { throw "$Name restore hash mismatch." }
    }
}
Push-Location $repo
$previousTemp = $env:TEMP; $previousTmp = $env:TMP
try {
    $env:TEMP = Join-Path $run 'temp'; $env:TMP = $env:TEMP

    # M1: managed buffered path falls back to the legacy single-attempt gate.
    $m1 = $originalTarget.Replace('if (_llmClient is IBufferedSendAttemptBudgetClient)',
        'if (false && _llmClient is IBufferedSendAttemptBudgetClient)')
    if ($m1 -eq $originalTarget) { throw 'M1 anchor not found.' }
    Assert-MutationDetected 'M1-buffered-fallback' $m1 $target $originalTarget $targetHash

    # M2: marker-gateway streaming skips the transport-held concurrency lease.
    $m2 = $originalClient.Replace('if (_concurrencyPort is null)' + "`r`n        {",
        "if (true)`r`n        {")
    if ($m2 -eq $originalClient) {
        $m2 = $originalClient.Replace('if (_concurrencyPort is null)' + "`n        {", "if (true)`n        {")
    }
    if ($m2 -eq $originalClient) { throw 'M2 anchor not found.' }
    Assert-MutationDetected 'M2-stream-lease-bypass' $m2 $client $originalClient $clientHash

    Build-Tests
    $restoredExit = Run-TransportTests 'restored'
    if ($restoredExit -ne 0) { throw 'Restored tests failed.' }
    if ((Get-FileHash $target -Algorithm SHA256).Hash -ne $targetHash) { throw 'LlmInvocationService restore mismatch.' }
    if ((Get-FileHash $client -Algorithm SHA256).Hash -ne $clientHash) { throw 'DirectLlmClient restore mismatch.' }
    Write-Host "restore verified; TransportAttemptBudget tests pass (exit 0)"
    Write-Host "Mutation probe passed (2 mutations). Evidence: $run"
}
finally {
    [IO.File]::WriteAllText($target, $originalTarget, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($client, $originalClient, [Text.UTF8Encoding]::new($false))
    $env:TEMP = $previousTemp; $env:TMP = $previousTmp
    Pop-Location
}
