param([switch]$SkipBuild, [int]$TimeoutSeconds = 240, [string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    if (-not $SkipBuild) {
        dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --artifacts-path temp/build/desktop-kernel --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    }
    $runRoot = Join-Path $repoRoot ('temp/test-out/kernel-winui-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $exe = Join-Path $repoRoot 'temp/build/winui3/bin/PuddingDesktop/release_win-x64/PuddingDesktop.exe'
    if ($ExecutablePath) { $exe = [IO.Path]::GetFullPath($ExecutablePath) }
    $report = Join-Path $runRoot 'report.json'
    $process = Start-Process -FilePath $exe -ArgumentList @('--state-root', ('"' + $runRoot + '"'), '--data-root', ('"' + (Join-Path $runRoot 'data') + '"'), '--kernel-smoke-report', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        # This process uses only the isolated test DataRoot created above.
        $process.Kill()
        throw "Kernel smoke timed out: $runRoot"
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "Kernel smoke failed: $runRoot" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success -or $result.processId -ne $process.Id) { throw "Invalid smoke result: $report" }
    # Successful process exit must also release its shared data-directory lease.
    $lease = [IO.File]::Open((Join-Path $runRoot 'data/.pudding-host.lock'), 'Open', 'ReadWrite', 'None')
    $lease.Dispose()
    $result | ConvertTo-Json
    Write-Output "Evidence: $report"
}
finally { Pop-Location }
