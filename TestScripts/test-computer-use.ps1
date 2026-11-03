param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repository ('.work/computer-use-smoke/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$artifacts = Join-Path $OutputDirectory 'artifacts'
Push-Location $repository
try {
    dotnet build TestScripts/ComputerUseSmoke/ComputerUseSmoke.csproj --artifacts-path $artifacts --nologo > (Join-Path $OutputDirectory 'build.log') 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $OutputDirectory/build.log" }
    $exe = Join-Path $artifacts 'bin/ComputerUseSmoke/debug/ComputerUseSmoke.exe'
    $probe = Start-Process -FilePath $exe -WorkingDirectory $repository -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'smoke.stdout.log') `
        -RedirectStandardError (Join-Path $OutputDirectory 'smoke.stderr.log')
    if (-not $probe.WaitForExit(90000)) {
        # Only this exact test process; never the desktop product or a process tree.
        $probe.Kill()
        throw "Smoke timed out; see $OutputDirectory"
    }
    $output = Get-Content -Raw -Encoding utf8 (Join-Path $OutputDirectory 'smoke.stdout.log')
    if ($probe.ExitCode -ne 0 -or $output -notmatch 'CHROMIUM_SMOKE_PASS') {
        throw "Chromium smoke failed; see $OutputDirectory/smoke.stdout.log and smoke.stderr.log"
    }
    ($output -split "`n") | Where-Object { $_ -match '^(PASS|SCREEN VERIFIED|CHROMIUM_SMOKE_PASS)' }
    Write-Output "Evidence: $OutputDirectory"
}
finally { Pop-Location }
