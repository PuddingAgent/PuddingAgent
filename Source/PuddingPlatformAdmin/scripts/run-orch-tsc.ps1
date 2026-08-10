# Runs TypeScript no-emit check and writes results to .tmp-test-out/tsc-check.txt
param(
    [string]$OutFile = ".tmp-test-out/tsc-check.txt"
)
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $OutFile
$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $out)
Push-Location $root
$env:CI = "1"
& pnpm exec tsc --noEmit 2>&1 | Out-File -FilePath $out -Encoding utf8
$code = $LASTEXITCODE
Pop-Location
Write-Host "TSC_EXIT=$code"
Write-Host "OUTPUT=$out"
exit $code
