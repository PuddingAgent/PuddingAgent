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

    A gate's PASS is only meaningful next to WHAT IT ACTUALLY LOOKED AT, so the summary
    echoes each gate's own denominator line (FILES= / PROJECT_COUNT=). A gate that PASSes
    because it scanned nothing must be visible as such, never hidden behind a green verdict.

    In scan mode that contract is also ENFORCED: a gate that exits PASS while reporting no
    denominator line is promoted to an instrument failure (exit 3), because such a PASS is
    indistinguishable from "it scanned nothing". Self-test mode is exempt, since the gates'
    controls intentionally produce no file counts. The detector itself has controls which run
    under -SelfTest, so it cannot rot silently. The enforcement decision itself has
    in-memory controls too (DENOM_ENFORCE_CTRL_*), so it does not depend on the external
    fixture directory to stay honest.

    The label contract is DECLARED per gate in the registration table and enforced: only a
    gate knows what its own denominator line looks like, so guessing it in the runner - as
    three hardcoded prefixes used to do - meant a new gate with a different label lost its
    denominator in silence. A registration that declares no label is now refused at
    startup (exit 3), and a gate that emits none of its declared labels names them in the
    message.

.PARAMETER SelfTest
    Run each gate's own -SelfTest instead of its real scan. All controls are in-memory
    and write no files. This is the runner's own meta-check: it answers "are the gates
    themselves still detecting planted defects?" rather than "is the repo clean?".

.PARAMETER RepoWide
    Pass the repository root instead of TestScripts as the scan root to the gates that
    accept a script-scan root (C-5, C-6). Without it those gates only see the TestScripts
    folder; with it they see every .ps1 in the repository that their own ExcludeDir
    defaults do not filter out. Measured on 2026-10-08: that is 28 files, not 1179 -
    the rest sit under bin/obj/node_modules/temp and are filtered by the gates themselves.

    Those gates always receive -Recurse, so this switch cannot silently degrade into a
    scan of one flat folder - without -Recurse the repository root yields about one file
    and an empty scan still reports PASS.

    Gates that do not accept a script-scan root (C-1/C-4, which always analyses the
    repository) are unaffected by this switch.

.PARAMETER Skip
    Gate name fragments to skip, for example -Skip C-5. Matched case-insensitively
    against the gate id and script name. A comma- or semicolon-separated list works too
    (-Skip C-5,C-6): when the script is launched with -File the whole list arrives as ONE
    string, so the runner splits it itself instead of relying on argument binding - which
    would match nothing and skip nothing while appearing to have worked.

    A pattern matching no registered gate is reported as SKIP_UNMATCHED rather than
    ignored: "that gate was skipped" and "that gate ran anyway" are very different things
    to believe while reading a verdict.

.PARAMETER MaxTail
    Lines of each gate's output to echo. Default 8. Does not affect the verdict.

.EXIT CODES
    0 = every registered gate reported PASS
    1 = at least one gate reported FAIL
    3 = no gate FAILed, but a gate failed closed (broken instrument), or a PASS carried no
        denominator line
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
    [switch]$RepoWide,
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
$repoRoot = Split-Path -Parent $here
$scanRoot = $here
if ($RepoWide) {
    if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = $here }
    $scanRoot = $repoRoot
}

$gates = @(
    (New-Object PSObject -Property @{ Id = 'C-1/C-4'; Script = 'check-project-layering.ps1';      PassRoot = $false; Labels = @('PROJECT_COUNT', 'EDGE_COUNT') })
    (New-Object PSObject -Property @{ Id = 'C-5';     Script = 'check-script-encodings.ps1';      PassRoot = $true;  Labels = @('FILES=') })
    (New-Object PSObject -Property @{ Id = 'C-6';     Script = 'check-script-runtime-compat.ps1'; PassRoot = $true;  Labels = @('FILES=') })
    (New-Object PSObject -Property @{ Id = 'C-7';     Script = 'check-script-paths.ps1';          PassRoot = $true;  Labels = @('FILES=') })
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

function Get-SkipPatterns {
    param([string[]]$Patterns)
    $out = @()
    foreach ($p in @($Patterns)) {
        if ($null -eq $p) { continue }
        foreach ($piece in ($p -split '[,;]')) {
            $t = $piece.Trim()
            if (-not [string]::IsNullOrWhiteSpace($t)) { $out += $t }
        }
    }
    return $out
}

function Get-GateScope {
    param([object[]]$Output, [string[]]$Labels)
    if ($null -eq $Output) { return @() }
    $out = @()
    foreach ($raw in $Output) {
        $line = $raw.ToString().Trim()
        foreach ($lbl in @($Labels)) {
            if ([string]::IsNullOrWhiteSpace($lbl)) { continue }
            if ($line.StartsWith($lbl)) { $out += $line; break }
        }
    }
    return $out
}

function Get-DenomMissingCount {
    param([object[]]$Ran, [bool]$IsSelfTest)
    if ($IsSelfTest) { return 0 }
    if ($null -eq $Ran) { return 0 }
    return @($Ran | Where-Object { $_.ExitCode -eq 0 -and @($_.Scope).Count -eq 0 }).Count
}

function Get-UnlabelledGates {
    param([object[]]$Gates)
    if ($null -eq $Gates) { return @() }
    return @($Gates | Where-Object { @($_.Labels | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -eq 0 })
}

Write-Output ('RUNNER = ' + $MyInvocation.MyCommand.Name)
Write-Output ('MODE   = ' + $(if ($SelfTest) { 'SELFTEST (each gate runs its own controls)' } else { 'SCAN (each gate runs its real check)' }))
Write-Output ('ENGINE = ' + $PSVersionTable.PSVersion.ToString())
Write-Output ('SCOPE  = ' + $(if ($RepoWide) { 'REPO-WIDE (' + $scanRoot + ')' } else { 'TestScripts only (' + $here + ')' }))
Write-Output ('GATES  = ' + $gates.Count + ' registered')

# The label contract is the one thing the runner cannot infer: only the gate knows what
# its denominator line looks like. So every gate DECLARES it, and a registration that
# declares nothing is refused here instead of quietly producing a PASS with no evidence.
$unlabelled = @(Get-UnlabelledGates -Gates $gates)
if ($unlabelled.Count -gt 0) {
    Write-Output ''
    Write-Output ('REGISTRATION INCOMPLETE: ' + (@($unlabelled | ForEach-Object { $_.Id }) -join ', ') + ' declare no denominator label.')
    Write-Output 'A gate must declare the prefix(es) its own denominator line starts with, otherwise its'
    Write-Output 'PASS cannot be told apart from a scan that looked at nothing. Refusing to run.'
    exit $EXIT_INSTRUMENT
}
Write-Output ''

$results = New-Object System.Collections.ArrayList
$missing = 0

$effectiveSkip = Get-SkipPatterns -Patterns $Skip

foreach ($g in $gates) {
    $scope = @()
    if (Test-Skipped -Id $g.Id -Script $g.Script -Patterns $effectiveSkip) {
        Write-Output ('--- {0}  {1}  SKIPPED' -f $g.Id, $g.Script)
        [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = -1; Skipped = $true; Scope = @() }))
        continue
    }

    $path = Join-Path $here $g.Script
    Write-Output ('--- {0}  {1}' -f $g.Id, $g.Script)

    if (-not (Test-Path -LiteralPath $path)) {
        Write-Output ('    FAIL-CLOSED: gate script not found: ' + $path)
        $missing++
        [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = $EXIT_MISSING; Skipped = $false; Scope = @() }))
        continue
    }

    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $path)
    if ($SelfTest) { $childArgs += '-SelfTest' }
    elseif ($g.PassRoot) { $childArgs += @('-Root', $scanRoot, '-Recurse') }

    $output = @(& powershell @childArgs 2>&1)
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 0 }

    $verdict = 'PASS'
    if ($code -ne 0) { $verdict = 'FAIL' }

    Write-Output ('    exit={0}  {1}' -f $code, $verdict)

    $scope = Get-GateScope -Output $output -Labels $g.Labels
    if ($scope.Count -eq 0 -and -not $SelfTest) {
        Write-Output ('    scope: !! NO DENOMINATOR LINE RECOGNISED (declared label(s): ' + (@($g.Labels) -join ', ') + ') !!')
    }
    foreach ($s in $scope) { Write-Output ('    scope: ' + $s) }

    $tail = @($output | ForEach-Object { $_.ToString() })
    if ($tail.Count -gt $MaxTail) { $tail = @($tail[($tail.Count - $MaxTail)..($tail.Count - 1)]) }
    foreach ($line in $tail) { Write-Output ('    | ' + $line) }

    Write-Output ''
    [void]$results.Add((New-Object PSObject -Property @{ Id = $g.Id; Script = $g.Script; ExitCode = $code; Skipped = $false; Scope = $scope }))
}

$skipUnmatched = @()
foreach ($p in $effectiveSkip) {
    $hit = @($gates | Where-Object { $_.Id.ToLowerInvariant().Contains($p.ToLowerInvariant()) -or $_.Script.ToLowerInvariant().Contains($p.ToLowerInvariant()) }).Count
    if ($hit -eq 0) { $skipUnmatched += $p }
}
if ($skipUnmatched.Count -gt 0) {
    Write-Output ('SKIP PATTERNS MATCHED NO REGISTERED GATE: ' + ($skipUnmatched -join ', '))
    Write-Output 'Those patterns skipped nothing, so every gate still ran. Check the spelling against the ids above.'
    Write-Output ''
}

if ($SelfTest) {
    $cEmpty  = @(Get-GateScope -Output @('hello', 'world') -Labels @('PROJECT_COUNT', 'FILES='))
    $cSpaced = @(Get-GateScope -Output @('  PROJECT_COUNT = 81  ') -Labels @('PROJECT_COUNT'))
    $cPlain  = @(Get-GateScope -Output @('FILES=22 BOM=4') -Labels @('FILES='))
    $cOther  = @(Get-GateScope -Output @('EDGE_COUNT    = 131') -Labels @('FILES='))
    Write-Output ('DENOM_CTRL_EMPTY={0} DENOM_CTRL_SPACED={1} DENOM_CTRL_PLAIN={2} DENOM_CTRL_UNDECLARED={3}' -f ($cEmpty.Count -eq 0), ($cSpaced.Count -eq 1), ($cPlain.Count -eq 1), ($cOther.Count -eq 0))
    if ($cEmpty.Count -ne 0 -or $cSpaced.Count -ne 1 -or $cPlain.Count -ne 1 -or $cOther.Count -ne 0) {
        Write-Output 'RESULT: SELFTEST FAIL (denominator detector did not classify its own controls)'
        exit $EXIT_SELFTEST
    }

    # Controls for the ENFORCEMENT itself, not just the detector: they live in-memory so a
    # regression is caught here rather than only by the external fixture directory.
    $eSilent = New-Object PSObject -Property @{ ExitCode = 0; Scope = @() }
    $eGood   = New-Object PSObject -Property @{ ExitCode = 0; Scope = @('FILES=22') }
    $eFail   = New-Object PSObject -Property @{ ExitCode = 1; Scope = @() }
    $nSilent = Get-DenomMissingCount -Ran @($eSilent) -IsSelfTest:$false
    $nGood   = Get-DenomMissingCount -Ran @($eGood)   -IsSelfTest:$false
    $nFail   = Get-DenomMissingCount -Ran @($eFail)   -IsSelfTest:$false
    $nExempt = Get-DenomMissingCount -Ran @($eSilent) -IsSelfTest:$true
    Write-Output ('DENOM_ENFORCE_CTRL_SILENT_PASS={0} WITH_DENOM={1} FAIL_NO_DENOM={2} SELFTEST_EXEMPT={3}' -f ($nSilent -eq 1), ($nGood -eq 0), ($nFail -eq 0), ($nExempt -eq 0))
    if ($nSilent -ne 1 -or $nGood -ne 0 -or $nFail -ne 0 -or $nExempt -ne 0) {
        Write-Output 'RESULT: SELFTEST FAIL (denominator enforcement control did not classify its own cases)'
        exit $EXIT_SELFTEST
    }

    # Label contract: a registration with no declared label must be refused, and a
    # whitespace-only label counts as absent.
    $uMissing = @(Get-UnlabelledGates -Gates @((New-Object PSObject -Property @{ Id = 'X-GATE'; Script = 'x.ps1' })))
    $uOk      = @(Get-UnlabelledGates -Gates @((New-Object PSObject -Property @{ Id = 'Y-GATE'; Script = 'y.ps1'; Labels = @('FILES=') })))
    $uBlank   = @(Get-UnlabelledGates -Gates @((New-Object PSObject -Property @{ Id = 'Z-GATE'; Script = 'z.ps1'; Labels = @('   ') })))
    Write-Output ('LABEL_CTRL_MISSING={0} DECLARED={1} BLANK={2}' -f ($uMissing.Count -eq 1), ($uOk.Count -eq 0), ($uBlank.Count -eq 1))
    if ($uMissing.Count -ne 1 -or $uOk.Count -ne 0 -or $uBlank.Count -ne 1) {
        Write-Output 'RESULT: SELFTEST FAIL (label contract control did not classify its own cases)'
        exit $EXIT_SELFTEST
    }
    Write-Output ''
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
    foreach ($s in @($r.Scope)) { Write-Output ('    scope: ' + $s) }
}

$ran = @($results | Where-Object { -not $_.Skipped })
$codes = @($ran | ForEach-Object { $_.ExitCode })

# A PASS carrying no denominator cannot be told apart from "it scanned nothing". That is
# the exact false-PASS class this suite exists to eliminate, so it is not allowed to stay
# invisible: in scan mode it is counted and promoted to an instrument failure.
$denomMissing = Get-DenomMissingCount -Ran $ran -IsSelfTest:$SelfTest

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

if (-not $SelfTest -and $denomMissing -gt 0 -and $aggregate -eq $EXIT_PASS) {
    $aggregate = $EXIT_INSTRUMENT
}

Write-Output ''
Write-Output ('GATES_RUN={0} SKIPPED={1} MISSING={2}' -f $ran.Count, (@($results | Where-Object { $_.Skipped }).Count), $missing)
Write-Output ('DENOM_MISSING={0}' -f $denomMissing)
Write-Output ('SKIP_UNMATCHED={0}' -f $skipUnmatched.Count)
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
