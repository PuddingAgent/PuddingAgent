param([int]$TimeoutSeconds = 60)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $repoRoot ('temp/test-out/winui3-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
Push-Location $repoRoot
try {
    dotnet test Source/PuddingDesktop.FoundationTests/PuddingDesktop.FoundationTests.csproj --nologo "-p:CoverletOutput=$runRoot/coverage" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw 'Foundation tests failed.' }
    dotnet build Source/PuddingDesktop/PuddingDesktop.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }
    $exe = Join-Path $repoRoot 'temp/build/winui3/bin/PuddingDesktop/Release/net10.0-windows10.0.19041.0/win-x64/PuddingDesktop.exe'
    $report = Join-Path $runRoot 'smoke.json'
    $process = Start-Process -FilePath $exe -ArgumentList @('--state-root', ('"' + $runRoot + '"'), '--smoke-report', ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        # Only terminate the process created by this test; never an existing Desktop/Core.
        $process.Kill()
        throw "Smoke timed out. Diagnostics: $runRoot"
    }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw "Smoke did not complete. Diagnostics: $runRoot" }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.success) { throw "Smoke failed: $($result.error)" }
    $result | ConvertTo-Json -Depth 5
    Write-Output "Evidence: $report"
}
finally { Pop-Location }

