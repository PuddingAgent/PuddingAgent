param([Parameter(Mandatory=$true)][string]$ObserverExe)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repo ('temp/test-out/exit-observer-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$results = @()
foreach ($scenario in @('forced', 'nonzero', 'clean', 'uncoordinated', 'fatal')) {
    $dir = Join-Path $output $scenario
    New-Item -ItemType Directory -Path $dir | Out-Null
    $gate = Join-Path $dir 'release'
    $code = if ($scenario -eq 'nonzero') { 7 } else { 0 }
    $script = "while (-not (Test-Path -LiteralPath '$gate')) { Start-Sleep -Milliseconds 50 }; exit $code"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $target = Start-Process pwsh -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -WindowStyle Hidden -PassThru
    $observer = $null
    try {
        $state = @{schema='pudding.desktop-run/1';runId=$scenario;process=@{processId=$target.Id;startedUtc=$target.StartTime.ToUniversalTime().ToString('o')};updatedAt=[DateTimeOffset]::UtcNow.ToString('o');phase=$(if ($scenario -eq 'clean') {'shutdown_completed'} else {'running'});managedProcessExitObserved=($scenario -eq 'uncoordinated');fatalSource=$(if ($scenario -eq 'fatal') {'injected'} else {$null});scene=@{shellState='test';coreState='Unknown'}}
        $state | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dir 'state.json') -Encoding utf8
        $observer = Start-Process -FilePath $ObserverExe -ArgumentList ('"' + $dir + '"') -WindowStyle Hidden -PassThru
        $timeout = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path (Join-Path $dir 'observer.json'))) {
            if ($observer.HasExited -or [DateTime]::UtcNow -gt $timeout) { throw 'Observer failed to attach' }
            Start-Sleep -Milliseconds 50
        }
        if ($scenario -eq 'forced') { Stop-Process -Id $target.Id -Force } else { New-Item -ItemType File -Path $gate | Out-Null }
        if (-not $observer.WaitForExit(10000)) { throw 'Observer did not finish' }
        $exit = Get-Content (Join-Path $dir 'exit.json') -Raw | ConvertFrom-Json
        $expected = switch ($scenario) { 'forced' {'abnormal_exit_code'} 'nonzero' {'abnormal_exit_code'} 'clean' {'clean_shutdown'} 'fatal' {'unhandled_exception'} default {'uncoordinated_process_exit'} }
        if ($exit.cause -ne $expected) { throw "Expected $expected, got $($exit.cause)" }
        if ($scenario -eq 'nonzero' -and $exit.exitCode -ne 7) { throw 'Lost actual exit code' }
        if (($scenario -ne 'clean') -ne (Test-Path (Join-Path $dir 'incident.json'))) { throw 'Incorrect incident presence' }
        $results += @{scenario=$scenario;cause=$exit.cause;exitCode=$exit.exitCode;passed=$true}
    }
    finally {
        if (-not $target.HasExited) { Stop-Process -Id $target.Id -Force }
        if ($observer -and -not $observer.HasExited) { Stop-Process -Id $observer.Id -Force }
        $target.Dispose()
        if ($observer) { $observer.Dispose() }
    }
}
$results | ConvertTo-Json | Set-Content (Join-Path $output 'results.json')
Write-Output "Passed $($results.Count) isolated exit scenarios. Evidence: $output"
