<#
.SYNOPSIS
    Single entry point for the TestScripts gate suite.

.DESCRIPTION
    Runs each gate in TestScripts as its OWN child PowerShell process, records that
    gate's exit code, prints a summary table, and exits with an aggregate verdict.

    Why a child process per gate: every gate ends with "exit <code>". Running them
    in-process would let the first gate terminate the runner itself. Spawning
    powershell.exe per gate also keeps their scope, state and error preferences
    isolated, so one gate cannot contaminate the next.

    This runner deliberately adds NO policy of its own. It does not decide what is
    fatal, does not re-interpret a gate's output, and does not suppress a gate. It
    reports each gate's own verdict and forwards the aggregate. If a gate fails, the
    reason is in that gate's output and in that gate's documentation - not here.

    Registered gates:
      C-1/C-4  check-project-layering.ps1        dependency graph acyclic; no
                                                 production -> test references
      C-5      check-script-encodings.ps1        every script is LOADABLE and declares
                                                 no engine this machine lacks
      C-6      check-script-runtime-compat.ps1   every command / named parameter inside
                                                 a script RESOLVES in this engine

    NOT registered: check-circular-deps.ps1. It is a deprecated shim that only forwards
    to check-project-layering.ps1 (2026-10-08), so registering it would double-count
    C-1/C-4. Run it directly if you need to prove the shim still works.

.PARAMETER SelfTest
    Run each gate's own -SelfTest instead of its real scan. All controls are in-memory
    and write no files. This is the runner's own meta-check: it answers "are the gates
    themselves still detecting planted defects?" rather than "is the repo clean?".

.PARAMETER Skip
    Gate name fragments to skip, for example -Skip C-5. Matched case-insensitively
    against the gate id and script name.

.PARAMETER MaxTail
    Lines of each gate's output to echo. Default 8. Does not affect the verdict.

.EXIT CODES
    0 = every registered gate reported PASS
    1 = at least one gate reported FAIL
    3 = no gate FAILed, but at least one gate failed closed (broken instrument)
    4 = no gate FAILed, but at least one gate's SELF-TEST did not detect the planted
        defect (expected mainly in -SelfTest mode)
  255 = the runner could not find or start a registered gate script

.NOTE
    Keep this file ASCII-ONLY. Windows PowerShell 5.1 decodes BOM-less script files as
    ANSI, so a UTF-8 file containing non-ASCII text fails to parse on this machine.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\run-gates.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\run-gates.ps1 -SelfTest
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\run-gates.ps1 -Skip C-6
#>

[CmdletBinding()]
param(
    [switch]$SelfTest,
    [string[]]$Skip = @(),
    [int]$MaxTail = 8
)

$ErrorActionPreference = 'Stop'

$EXIT_PASS = 0
$EXIT_FAIL = 1
$EXIT_INSTRUMENT = 3
$EXIT_SELFTEST = 4
$EXIT_MISSING = 255

$here = $PSScriptRoot
if (-not $here) { $here = (Get-Location).Path }

$gates = @(
    (New-Object PSObject -Property @{ Id = 'C-1/C-4'; Script = 'check-project-layering.ps1';        PassRoot = $false })
    (New-Object PSObject -Property @{ Id = 'C-5';     Script = 'check-script-encodings.ps1';        PassRoot = $true  })
    (New-Object PSObject -Property @{ Id = 'C-6';     Script = 'check-script-runtime-compat.ps1';   PassRoot = $true  })
)

function Test-Skipped {
    param([string]$Id, [string]$Script, [string[]]$Patterns)
    foreach ($p in $Patterns) {
        if ([string]::IsNullOrWhiteSpace($p)) { continue }
        if ($Id.ToLowerInvariant().Contains($p.ToLowerInvariant())) { return $true }
        if ($Script.ToLowerInvariant().Contains($p.ToLowerInvariant())) { return $true }
    }
    return $false
}

Write-Output ('RUNNER = ' + $MyInvocation.MyCommand.Name)
Write-Output ('MODE   = ' + $(if ($SelfTest) { 'SELFTEST (each gate runs its own controls)' } else { 'SCAN (each gate runs its real check)' }))
Write-Output ('ENGINE = ' + $PSVersionTable.PSVersion.ToString())
Write-Output ('GATES  = ' + $gates.Count + ' registered')
Write-Output ''

$results = New-Object System.Collections.ArrayList
$missing = 0

foreach ($g in $gates) {
    if (Test-Skipped -Id $g.Id -Script $g.Script -Patterns $Skip) {
        Write-Output ('--- {0}  {1}  SKIPPED' -f $g.Id, $g.Script)
        [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = -1; Skipped = $true }))
        continue
    }

    $path = Join-Path $here $g.Script
    Write-Output ('--- {0}  {1}' -f $g.Id, $g.Script)

    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output ('    FAIL-CLOSED: gate script not found: ' + $path)
        $missing++
        [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = $EXIT_MISSING; Skipped = $false }))
        continue
    }

    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path)
    if ($SelfTest) { $childArgs += '-SelfTest' }
    elseif ($g.PassRoot) { $childArgs += @('-Root', $here) }

    $output = @(& powershell @childArgs 2>&1)
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }

    $verdict = 'PASS'
    if ($code -ne 0) { $verdict = 'FAIL' }

    Write-Output ('    exit={0}  {1}' -f $code, $verdict)

    $tail = @($output | ForEach-Object { $_.ToString() })
    if ($tail.Count -gt $MaxTail) { $tail = @($tail[($tail.Count - $MaxTail)..($tail.Count - 1)]) }
    foreach ($line in $tail) { Write-Output ('    | ' + $line) }

    Write-Output ''
    [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = $code; Skipped = $false }))
}

Write-Output '=== SUMMARY ==='
foreach ($r in $results) {
    $tag = 'PASS'
    if ($r.Skipped) { $tag = 'SKIPPED' }
    elseif ($r.ExitCode -eq $EXIT_FAIL) { $tag = 'FAIL' }
    elseif ($r.ExitCode -eq $EXIT_INSTRUMENT) { $tag = 'FAIL-CLOSED' }
    elseif ($r.ExitCode -eq $EXIT_SELFTEST) { $tag = 'SELFTEST_FAIL' }
    elseif ($r.ExitCode -ne 0) { $tag = 'ERROR' }
    Write-Output ('{0,-16} {1,-10} exit={2}' -f $r.Id, $tag, $r.ExitCode)
}

$ran = @($results | Where-Object { -not $_.Skipped })
$codes = @($ran | ForEach-Object { $_.ExitCode })

$aggregate = $EXIT_PASS
if ($missing -gt 0) {
    $aggregate = $EXIT_MISSING
}
elseif ($codes -contains $EXIT_FAIL) {
    $aggregate = $EXIT_FAIL
}
elseif ($codes -contains $EXIT_INSTRUMENT) {
    $aggregate = $EXIT_INSTRUMENT
}
elseif ($codes -contains $EXIT_SELFTEST) {
    $aggregate = $EXIT_SELFTEST
}
else {
    $other = @($codes | Where-Object { $_ -ne 0 })
    if ($other.Count -gt 0) { $aggregate = $EXIT_FAIL }
}

Write-Output ''
Write-Output ('GATES_RUN={0} SKIPPED={1} MISSING={2}' -f $ran.Count, (@($results | Where-Object { $_.Skipped }).Count), $missing)
Write-Output ('AGGREGATE_EXIT={0}' -f $aggregate)
if ($aggregate -eq $EXIT_PASS) {
    Write-Output 'RESULT: PASS'
}
else {
    Write-Output 'RESULT: FAIL'
    Write-Output 'This is the GATES verdict, not a runner error. Read each gate output above;'
    Write-Output 'each gate documents its own exit codes and its own scope limits in its header.'
}

exit $aggregate
