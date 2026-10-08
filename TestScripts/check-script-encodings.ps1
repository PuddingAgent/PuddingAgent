<#
.SYNOPSIS
  C-5 gate: every PowerShell script in scope must be loadable AND runnable by the
  PowerShell engines actually available on this machine.

.DESCRIPTION
  Two independent blockers are checked, because either one makes a script dead weight
  even though it looks perfectly fine in an editor:

  (1) LOADABILITY - Windows PowerShell 5.1 decides how to decode a .ps1 file from a
      UTF-8 BOM:
        - bytes begin with EF BB BF  -> decoded as UTF-8 (BOM stripped)
        - otherwise                  -> decoded with [Text.Encoding]::Default (ANSI)
      This script reproduces that rule in-process and runs the real PowerShell parser
      over the decoded text. A parse error means the script CANNOT execute here.

  (2) ENGINE REQUIREMENT - a "#requires -Version X.Y" statement is enforced by the
      engine BEFORE the script runs. If X.Y is higher than the running engine, the file
      is rejected with ScriptRequiresUnmatchedPSVersion no matter how clean it parses.
      Such scripts are reported as UNRUNNABLE.

  The gate is NOT "every script must have a BOM", and NOT "every script must avoid
  #requires". An ASCII-only script with no BOM is perfectly valid. BOM presence,
  non-ASCII byte presence and the declared engine are reported as facts.

  Use -AllowUnrunnable to downgrade UNRUNNABLE findings to informational (exit code
  then follows LOADABILITY only). That is appropriate when a script targets another
  engine on purpose; it is NOT appropriate when the gate must run on this machine.

.PARAMETER Root
  Directory to scan. Default: TestScripts (resolved relative to the current dir).

.PARAMETER Recurse
  Scan subdirectories too. Default: top level only.

.PARAMETER ExcludeDir
  Directory names (any path segment) to skip. Default: temp, node_modules, .git, bin, obj, dist, .venv, .pudding

.PARAMETER AllowUnrunnable
  Do not fail on satisfied-by-another-engine findings; report them as INFO.

.PARAMETER SelfTest
  Run the in-memory controls and exit. Writes no files.

.EXIT CODES
  0 = PASS       : files enumerated, no parse errors, no unrunnable engine requirement
  1 = FAIL       : a script does not parse, or declares an engine this machine lacks
  3 = FAIL-CLOSED: no script enumerated in scope; refuses to report green
  4 = FAIL       : self-test did not detect the planted defects
#>

[CmdletBinding()]
param(
    [string]$Root = 'TestScripts',
    [switch]$Recurse,
    [string[]]$ExcludeDir = @('temp', 'node_modules', '.git', 'bin', 'obj', 'dist', '.venv', '.pudding'),
    [switch]$AllowUnrunnable,
    [switch]$SelfTest,
    [int]$MaxReport = 100
)

$ErrorActionPreference = 'Stop'

$EXIT_PASS = 0
$EXIT_BROKEN = 1
$EXIT_EMPTY = 3
$EXIT_SELFTEST = 4

# Core: mirror exactly how Windows PowerShell 5.1 decodes a script file, then ask the
# real parser, then read the declared engine requirement.
function Get-LoadStateFromBytes {
    param([byte[]]$Bytes)

    $hasBom = $false
    $text = $null
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        $hasBom = $true
        $text = [System.Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    else {
        $text = [System.Text.Encoding]::Default.GetString($Bytes)
    }

    $hasNonAscii = $false
    for ($i = 0; $i -lt $Bytes.Length; $i++) {
        if ($Bytes[$i] -gt 0x7F) { $hasNonAscii = $true; break }
    }

    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$tokens, [ref]$errors)

    $errCount = 0
    $firstError = ''
    if ($null -ne $errors) {
        $errCount = $errors.Count
        if ($errCount -gt 0) {
            $e = $errors[0]
            $firstError = '{0}@L{1}C{2}' -f $e.ErrorId, $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber
        }
    }

    # #requires -Version X.Y is enforced by the engine before execution.
    $reqVersion = ''
    $reqUnsatisfiable = $false
    $reqMatches = [regex]::Matches($text, '(?m)^\s*#requires\s+-Version\s+([0-9][0-9.]*)')
    if ($reqMatches.Count -gt 0) {
        $reqVersion = $reqMatches[0].Groups[1].Value
        $parts = $reqVersion.Split('.')
        $reqMajor = 0
        [void][int]::TryParse($parts[0], [ref]$reqMajor)
        $curMajor = $PSVersionTable.PSVersion.Major
        if ($reqMajor -gt $curMajor) { $reqUnsatisfiable = $true }
    }

    return New-Object PSObject -Property @{
        HasBom           = $hasBom
        HasNonAscii      = $hasNonAscii
        ErrorCount       = $errCount
        FirstError       = $firstError
        RequiredVersion  = $reqVersion
        ReqUnsatisfiable = $reqUnsatisfiable
    }
}

function Get-ScriptLoadState {
    param([string]$Path)
    return Get-LoadStateFromBytes -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

if ($SelfTest) {
    $failures = @()

    # Control A: UTF-8, no BOM, one non-ASCII char inside a single-quoted string.
    # 5.1 decodes the UTF-8 bytes as ANSI, swallows the closing quote and fails.
    $cn = [string][char]0x4E2D
    $srcA = '$s = ' + "'" + $cn + "'" + [char]10
    $noBom = [System.Text.Encoding]::UTF8.GetBytes($srcA)

    $withBom = New-Object byte[] ($noBom.Length + 3)
    $withBom[0] = 0xEF
    $withBom[1] = 0xBB
    $withBom[2] = 0xBF
    [Array]::Copy($noBom, 0, $withBom, 3, $noBom.Length)

    # Control C: pure ASCII, no BOM, must stay clean.
    $asciiOnly = [System.Text.Encoding]::ASCII.GetBytes('$x = 1' + [char]10)

    # Control D: engine requirement parsing (version chosen relative to this engine so
    # the assertion holds under both Windows PowerShell 5.1 and PowerShell 7+).
    $curMajor = $PSVersionTable.PSVersion.Major
    $futureMajor = $curMajor + 2
    $reqFuture = [System.Text.Encoding]::ASCII.GetBytes('#requires -Version ' + $futureMajor + '.0' + [char]10 + '$x = 1' + [char]10)
    $reqCurrent = [System.Text.Encoding]::ASCII.GetBytes('#requires -Version ' + $curMajor + '.0' + [char]10 + '$x = 1' + [char]10)

    $sA = Get-LoadStateFromBytes -Bytes $noBom
    $sB = Get-LoadStateFromBytes -Bytes $withBom
    $sC = Get-LoadStateFromBytes -Bytes $asciiOnly
    $sD = Get-LoadStateFromBytes -Bytes $reqFuture
    $sE = Get-LoadStateFromBytes -Bytes $reqCurrent

    if ($sA.ErrorCount -lt 1) { $failures += 'no-BOM + non-ASCII control was NOT detected' }
    if (-not $sA.HasNonAscii) { $failures += 'no-BOM control: non-ASCII flag not set' }
    if ($sA.HasBom) { $failures += 'no-BOM control reported a BOM' }
    if ($sB.ErrorCount -ne 0) { $failures += 'BOM control did not parse clean' }
    if (-not $sB.HasBom) { $failures += 'BOM control: BOM flag not set' }
    if ($sC.ErrorCount -ne 0) { $failures += 'ASCII control did not parse clean' }
    if ($sD.RequiredVersion -ne ($futureMajor.ToString() + '.0')) { $failures += 'engine requirement not parsed' }
    if (-not $sD.ReqUnsatisfiable) { $failures += 'future engine requirement not flagged unsatisfiable' }
    if ($sD.ErrorCount -ne 0) { $failures += 'engine requirement fixture did not parse clean' }
    if ($sE.ReqUnsatisfiable) { $failures += 'current engine requirement wrongly flagged unsatisfiable' }

    Write-Output ('POSCTRL   errors={0} first={1}' -f $sA.ErrorCount, $sA.FirstError)
    Write-Output ('BOMCTRL   errors={0}' -f $sB.ErrorCount)
    Write-Output ('ASCIICTRL errors={0}' -f $sC.ErrorCount)
    Write-Output ('REQFUTURE req={0} unsat={1}' -f $sD.RequiredVersion, $sD.ReqUnsatisfiable)
    Write-Output ('REQCURRENT req={0} unsat={1}' -f $sE.RequiredVersion, $sE.ReqUnsatisfiable)

    if ($failures.Count -gt 0) {
        foreach ($f in $failures) { Write-Output ('SELFTEST FAIL: ' + $f) }
        exit $EXIT_SELFTEST
    }
    Write-Output 'SELFTEST PASS'
    exit $EXIT_PASS
}

if (-not (Test-Path -LiteralPath $Root)) {
    Write-Output ('FAIL-CLOSED: scope path not found: ' + $Root)
    exit $EXIT_EMPTY
}

$all = @(Get-ChildItem -LiteralPath $Root -Filter *.ps1 -File -Recurse:$Recurse)
$files = @()
foreach ($f in $all) {
    $skip = $false
    foreach ($seg in ($f.FullName -split '[\\/]')) {
        if ($ExcludeDir -contains $seg) { $skip = $true; break }
    }
    if (-not $skip) { $files += $f }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $repoRoot) { $repoRoot = (Get-Location).Path }

if ($files.Count -eq 0) {
    Write-Output ('FAIL-CLOSED: no .ps1 enumerated under ' + $Root + ' (recurse=' + [string]$Recurse + ')')
    exit $EXIT_EMPTY
}

Write-Output ('ENGINE=' + $PSVersionTable.PSVersion.ToString() + ' PWSH_PRESENT=' + [string][bool](Get-Command pwsh -ErrorAction SilentlyContinue))

$broken = @()
$unrunnable = @()
$bomCount = 0
$nonAsciiCount = 0
$parseClean = 0

foreach ($f in $files) {
    $state = Get-ScriptLoadState -Path $f.FullName
    $full = $f.FullName
    $rel = $full
    if ($full.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rel = $full.Substring($repoRoot.Length).TrimStart('\', '/')
    }
    if ($state.HasBom) { $bomCount++ }
    if ($state.HasNonAscii) { $nonAsciiCount++ }

    $tag = 'OK'
    if ($state.ErrorCount -gt 0) {
        $tag = 'BROKEN'
        $broken += $rel
    }
    elseif ($state.ReqUnsatisfiable) {
        $tag = 'UNRUNNABLE'
        $unrunnable += ($rel + ' (requires PS ' + $state.RequiredVersion + ')')
    }
    else {
        $parseClean++
    }

    $req = '-'
    if ($state.RequiredVersion -ne '') { $req = $state.RequiredVersion }

    Write-Output ('{0,-11} bom={1,-5} nonascii={2,-5} errors={3,-3} req={4,-5} {5}' -f `
        $tag, $state.HasBom, $state.HasNonAscii, $state.ErrorCount, $req, $rel)
    if ($state.ErrorCount -gt 0 -and $state.FirstError) {
        Write-Output ('            first: ' + $state.FirstError)
    }
}

Write-Output ''
Write-Output ('FILES={0} BOM={1} NONASCII={2} BROKEN={3} UNRUNNABLE={4} RUNNABLE={5}' -f `
    $files.Count, $bomCount, $nonAsciiCount, $broken.Count, $unrunnable.Count, $parseClean)

$failed = $false
if ($broken.Count -gt 0) {
    Write-Output ('FAIL: {0} script(s) cannot be LOADED (decode/parse defect)' -f $broken.Count)
    $shown = 0
    foreach ($b in $broken) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $b)
        $shown++
    }
    $failed = $true
}

if ($unrunnable.Count -gt 0) {
    $level = 'FAIL'
    if ($AllowUnrunnable) { $level = 'INFO' }
    Write-Output ('{0}: {1} script(s) declare an engine this machine does not have (cannot RUN here)' -f $level, $unrunnable.Count)
    $shown = 0
    foreach ($u in $unrunnable) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $u)
        $shown++
    }
    if (-not $AllowUnrunnable) { $failed = $true }
}

if ($failed) {
    Write-Output 'RESULT: FAIL'
    exit $EXIT_BROKEN
}

Write-Output 'RESULT: PASS'
exit $EXIT_PASS
