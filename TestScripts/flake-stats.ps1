# Flake observation counts: "how often has this test flaked?" (read-only; ASCII only).
#
# Input : an observations log written by delivery-manifest.ps1 (see TestScripts/flake-observations.txt).
# Output: one line per test, "<count><TAB><test name>", highest count first; then a TOTAL line.
#
# Exit codes: 0 = counted; 2 = log not found (so callers can tell "no data" from "zero occurrences").
param(
    [string]$Path = (Join-Path $PSScriptRoot "flake-observations.txt"),
    [string]$TestName = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Path)) {
    Write-Warning "flake observation log not found: $Path"
    exit 2
}

$observations = @(Get-Content $Path -Encoding UTF8 |
    Where-Object { $_ -and -not $_.TrimStart().StartsWith("#") } |
    ForEach-Object {
        # <date> <suite> <test name>  ->  take everything after the 2nd token as the test name
        $parts = $_.Trim() -split "\s+", 3
        if ($parts.Count -ge 3) { [pscustomobject]@{ Date = $parts[0]; Suite = $parts[1]; Test = $parts[2] } }
    })

$filtered = if ([string]::IsNullOrWhiteSpace($TestName)) {
    $observations
}
else {
    @($observations | Where-Object { $_.Test -eq $TestName })
}

$groups = $filtered | Group-Object Test | Sort-Object Count -Descending
foreach ($group in $groups) {
    "{0}`t{1}" -f $group.Count, $group.Name
}

"TOTAL`t{0}" -f @($filtered).Count
exit 0
