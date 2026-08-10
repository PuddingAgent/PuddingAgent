# Runs orchestration Jest suites and writes a summary to .tmp-test-out/orchestration-jest.txt
param(
    [string]$Pattern = "src/pages/orchestration",
    [string]$OutFile = ".tmp-test-out/orchestration-jest.txt"
)
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $OutFile
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out)
Push-Location $root
$env:CI = "1"
& pnpm exec jest $Pattern --runInBand 2>&1 | Out-File -FilePath $out -Encoding utf8
$code = $LASTEXITCODE
Pop-Location
Write-Host "JEST_EXIT=$code"
Write-Host "OUTPUT=$out"
exit $code
