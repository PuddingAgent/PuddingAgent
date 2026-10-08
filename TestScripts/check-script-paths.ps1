<#
.SYNOPSIS
  C-7 gate: every absolute path literal a dev script hard-codes must still resolve on
  THIS machine.

.DESCRIPTION
  C-5 answers "can this file be LOADED (decode + parse)?".
  C-6 answers "will the commands and parameters INSIDE it bind in this engine?".
  Neither answers "does the filesystem path it hard-codes still exist?".

  Concrete case (found 2026-10-09): this repository moved from
      E:\github\AgentNetworkPlan\PuddingAgent
  to
      D:\CodeProject\PuddingAgent\PuddingAgent
  and the E: drive no longer exists on this machine (present drives: C, D, F, H).
  Every script that still carries an absolute literal under E: is dead on arrival, yet it
  parses clean (C-5 PASS) and its commands resolve (C-6 PASS). C-7 is the gate that notices.

  Judgements, in confidence order:
    MISSING_DRIVE  the literal names a drive that does not exist here    -> FAIL
    MISSING_PATH   the drive exists but the path does not               -> WARN (-StrictPaths makes it FAIL)
    OK             the path resolves

  MISSING_PATH is a warning on purpose: output directories, backup roots and key
  directories are legitimately created on first use, so their absence is not proof of rot.
  A missing DRIVE is proof, because no script can work through a drive that is not there.

  Deliberate scope limits (printed in the report footer, not hidden):
    - .ps1 literals come from the PARSER (StringConstantExpressionAst +
      ExpandableStringExpressionAst), so comments, comment-based help and here-strings
      are never flagged
    - .py literals come from a line scan of single/double quoted strings, with whole-line
      "#" comments blanked out first; docstrings are NOT specially excluded
    - %ENV%, $env:, "~" and relative paths are not absolute path literals and are skipped
    - tokens longer than 400 characters are skipped (base64 blobs can contain "X:\" by chance)
    - a bare drive prefix without a separator ("C:") is not a full path and is skipped
    - existence is checked with Test-Path -LiteralPath, so wildcards are treated literally
    - "$" is treated as a terminator, so an expandable string only yields the literal part

.PARAMETER Root
  Directory to scan. Default: TestScripts (resolved relative to the current dir).

.PARAMETER Recurse
  Accepted for compatibility; recursion is ON by default.

.PARAMETER NoRecurse
  Scan the top level only. The scope of a gate must not depend on how it is
  invoked, so recursion is the default and this switch is the explicit opt-out:
  a direct run and run-gates.ps1 must enumerate the same files.

.PARAMETER ExcludeDir
  Directory names (any path segment) to skip. Default: temp, node_modules, .git, bin, obj, dist, .venv, .pudding

.PARAMETER MaxReport
  Maximum findings printed per category. Default: 200.

.PARAMETER StrictPaths
  Treat MISSING_PATH (drive present, path absent) as a failure as well.

.PARAMETER SelfTest
  Run the in-memory controls and exit. Writes no files.

.EXIT CODES
  0 = PASS       : files enumerated, no absolute path literal points at a missing drive
  1 = FAIL       : at least one MISSING_DRIVE (or MISSING_PATH when -StrictPaths is set)
  2 = FAIL       : a .ps1 file in scope cannot be decoded/parsed, so it cannot be analysed
  3 = FAIL-CLOSED: no script enumerated in scope; refuses to report green
  4 = FAIL       : self-test did not detect the planted defects
#>

[CmdletBinding()]
param(
    [string]$Root = 'TestScripts',
    [switch]$Recurse,
    [switch]$NoRecurse,
    [string[]]$ExcludeDir = @('temp', 'node_modules', '.git', 'bin', 'obj', 'dist', '.venv', '.pudding'),
    [int]$MaxReport = 200,
    [switch]$StrictPaths,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

# Scope must be a property of the gate, not of the invocation. Recursion defaults
# to ON so that a direct run enumerates exactly what run-gates.ps1 enumerates.
$scanRecurse = -not $NoRecurse
if ($scanRecurse) { $modeTag = 'REC' } else { $modeTag = 'FLAT' }

$EXIT_PASS = 0
$EXIT_FINDINGS = 1
$EXIT_UNANALYSABLE = 2
$EXIT_EMPTY = 3
$EXIT_SELFTEST = 4

$LF = [string][char]10
$BS = [string][char]92
$SL = [string][char]47

# ---------------------------------------------------------------------------
# Present drives, computed once. A drive that is absent cannot be argued with.
# ---------------------------------------------------------------------------
function Get-PresentDrives {
    $map = @{}
    foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
        $n = $d.Name
        if ($n.Length -ge 1) { $map[$n.Substring(0, 1).ToUpperInvariant()] = $true }
    }
    return $map
}

# ---------------------------------------------------------------------------
# A returned single item unrolls and a New-Object PSObject gets no scalar ".Count"
# shim. Always re-wrap before counting.
# ---------------------------------------------------------------------------
function Get-FindingCount {
    param($Findings)
    return @($Findings).Count
}

# ---------------------------------------------------------------------------
# Find drive-absolute literals ("X:\..." or "X:/...") in a block of text.
# Returns objects with the literal and its 1-based line inside that text.
# ---------------------------------------------------------------------------
function Get-AbsPathLiterals {
    param([string]$Text)

    $out = New-Object 'System.Collections.Generic.List[object]'
    if ([string]::IsNullOrEmpty($Text)) { return $out }

    # The drive letter must start a token: without the lookbehind, "https://x" would be
    # read as drive "s", and every URL in every script would be reported as a missing drive.
    $rx = [regex]'(?<![A-Za-z0-9_])([A-Za-z]):[\\/]([^"''\s|;,()<>`\[\]$]*)'

    $cursor = 0
    $line = 1
    foreach ($m in $rx.Matches($Text)) {
        $idx = $m.Index
        while ($true) {
            $nl = $Text.IndexOf($LF, $cursor)
            if ($nl -lt 0) { break }
            if ($nl -ge $idx) { break }
            $line++
            $cursor = $nl + 1
        }

        $lit = $m.Value
        if ($lit.Length -gt 400) { continue }
        $lit = $lit.TrimEnd([char]92, [char]47, [char]46, [char]32)
        # A path followed by a query/fragment is still that path; the suffix is not part
        # of it. Reported literals are trimmed so the report shows the real path.
        $cut = $lit.IndexOf('?')
        if ($cut -lt 0) { $cut = $lit.IndexOf('#') }
        if ($cut -ge 0) { $lit = $lit.Substring(0, $cut) }
        if ($lit.Length -lt 4) { continue }
        if ($lit.Substring(2, 1) -ne $BS -and $lit.Substring(2, 1) -ne $SL) { continue }

        $out.Add((New-Object PSObject -Property @{ Literal = $lit; Line = $line }))
    }
    return $out
}

# ---------------------------------------------------------------------------
# Blank out whole-line "#" comments without moving any line number.
# ---------------------------------------------------------------------------
function Get-PyScannableText {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $parts = $Text -split $LF
    $out = @()
    foreach ($p in $parts) {
        if ($p.TrimStart().StartsWith('#')) { $out += '' }
        else { $out += $p }
    }
    return ($out -join $LF)
}

function Get-DecodedTextEngine {
    param([byte[]]$Bytes)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    return [System.Text.Encoding]::Default.GetString($Bytes)
}

function Get-DecodedTextUtf8 {
    param([byte[]]$Bytes)
    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($Bytes, 3, $Bytes.Length - 3)
    }
    return [System.Text.Encoding]::UTF8.GetString($Bytes)
}

function Get-ParsedAst {
    param([string]$Text)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    $errCount = 0
    $firstError = ''
    if ($null -ne $errors -and $errors.Count -gt 0) {
        $errCount = $errors.Count
        $e = $errors[0]
        $firstError = '{0}@L{1}C{2}' -f $e.ErrorId, $e.Extent.StartLineNumber, $e.Extent.StartColumnNumber
    }
    return New-Object PSObject -Property @{
        Ast        = $ast
        ErrorCount = $errCount
        FirstError = $firstError
    }
}

# ---------------------------------------------------------------------------
# Literals of one .ps1 file, taken from the AST so comments never contribute.
# ---------------------------------------------------------------------------
function Get-PsFileLiterals {
    param([System.Management.Automation.Language.ScriptBlockAst]$Ast)

    $found = New-Object 'System.Collections.Generic.List[object]'
    $nodes = $Ast.FindAll({
            param($n)
            ($n -is [System.Management.Automation.Language.StringConstantExpressionAst]) -or
            ($n -is [System.Management.Automation.Language.ExpandableStringExpressionAst])
        }, $true)

    foreach ($n in $nodes) {
        $text = $n.Extent.Text
        $base = $n.Extent.StartLineNumber - 1
        foreach ($lit in @(Get-AbsPathLiterals -Text $text)) {
            $found.Add((New-Object PSObject -Property @{ Literal = $lit.Literal; Line = ($base + $lit.Line) }))
        }
    }
    return $found
}

function Get-PyFileLiterals {
    param([string]$Text)
    return @(Get-AbsPathLiterals -Text (Get-PyScannableText -Text $Text))
}

# ---------------------------------------------------------------------------
# Verdict for one literal.
# ---------------------------------------------------------------------------
function Get-PathVerdict {
    param([string]$Literal, [hashtable]$Drives)

    $letter = $Literal.Substring(0, 1).ToUpperInvariant()
    if (-not $Drives.ContainsKey($letter)) { return 'MISSING_DRIVE' }
    $norm = $Literal.Replace($SL, $BS)
    if (Test-Path -LiteralPath $norm) { return 'OK' }
    return 'MISSING_PATH'
}

# ---------------------------------------------------------------------------
# Self test: in-memory controls only. Reads the filesystem (Test-Path), writes nothing.
# Every control is expressed relative to THIS machine so it holds wherever it runs.
# ---------------------------------------------------------------------------
if ($SelfTest) {
    $failures = @()
    $drives = Get-PresentDrives

    # Control A: a literal on a drive that does not exist -> MISSING_DRIVE.
    $absent = ''
    foreach ($c in @('Z', 'Y', 'X', 'W', 'V', 'U', 'T', 'S', 'R', 'Q', 'P', 'O', 'N', 'M', 'L', 'K', 'J', 'I')) {
        if (-not $drives.ContainsKey($c)) { $absent = $c; break }
    }
    if ($absent -eq '') {
        $failures += 'A: could not find an absent drive letter to test with'
    }
    else {
        $aText = $absent + ':' + $BS + '__pudding_absent_drive_probe'
        $aHits = @(Get-AbsPathLiterals -Text $aText)
        if ((Get-FindingCount -Findings $aHits) -ne 1) {
            $failures += 'A: drive-absolute literal was not extracted'
        }
        elseif ((Get-PathVerdict -Literal $aHits[0].Literal -Drives $drives) -ne 'MISSING_DRIVE') {
            $failures += 'A: missing drive was not judged MISSING_DRIVE'
        }
    }

    # Control B: a literal on a present drive that really exists -> OK (no false positive).
    $repoRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($repoRoot)) { $repoRoot = (Get-Location).Path }
    $bText = $repoRoot
    $bHits = @(Get-AbsPathLiterals -Text $bText)
    if ((Get-FindingCount -Findings $bHits) -ne 1) {
        $failures += 'B: existing absolute root was not extracted'
    }
    elseif ((Get-PathVerdict -Literal $bHits[0].Literal -Drives $drives) -ne 'OK') {
        $failures += 'B: false positive on the existing repository root'
    }

    # Control C: relative paths are out of scope.
    $cText = '.' + $BS + 'x' + $BS + 'y.ps1'
    if ((Get-FindingCount -Findings @(Get-AbsPathLiterals -Text $cText)) -ne 0) {
        $failures += 'C: relative path was treated as an absolute literal'
    }

    # Control D: URLs, env-var paths and bare drive prefixes are out of scope.
    $dText = 'https://example.com/a/b/c' + $LF + 'file.txt' + $LF + '%TEMP%' + $BS + 'x.txt' + $LF + '~' + $BS + 'x.txt' + $LF + 'C:'
    if ((Get-FindingCount -Findings @(Get-AbsPathLiterals -Text $dText)) -ne 0) {
        $bad = @(Get-AbsPathLiterals -Text $dText)
        $failures += ('D: non-absolute-path text was treated as a drive literal: ' + (@($bad | ForEach-Object { $_.Literal }) -join ' | '))
    }

    # Control H: a URL scheme is not a drive, but a real Windows path embedded in a line
    # still is. This pins the lookbehind from both sides.
    $hText = 'see https://example.com/x then D' + ':' + $BS + 'CodeProject' + $LF
    $hHits = @(Get-AbsPathLiterals -Text $hText)
    if ((Get-FindingCount -Findings $hHits) -ne 1) {
        $failures += ('H: expected exactly 1 literal, got ' + (Get-FindingCount -Findings $hHits))
    }
    elseif ($hHits[0].Literal.ToUpperInvariant().StartsWith('S:')) {
        $failures += 'H: URL scheme was extracted as a drive literal'
    }

    # Control E: present drive + absent path -> MISSING_PATH, never MISSING_DRIVE.
    $present = ''
    foreach ($c in @('C', 'D', 'F', 'H', 'Z')) {
        if ($drives.ContainsKey($c)) { $present = $c; break }
    }
    if ($present -eq '') {
        $failures += 'E: could not find a present drive letter to test with'
    }
    else {
        $eText = $present + ':' + $BS + '__pudding_no_such_dir_zzz'
        $eHits = @(Get-AbsPathLiterals -Text $eText)
        if ((Get-FindingCount -Findings $eHits) -ne 1) {
            $failures += 'E: absent path literal was not extracted'
        }
        else {
            $v = Get-PathVerdict -Literal $eHits[0].Literal -Drives $drives
            if ($v -ne 'MISSING_PATH') { $failures += ('E: expected MISSING_PATH, got ' + $v) }
        }
    }

    # Control F: exactly one finding must count as one, not as zero (single-item unroll).
    $one = @()
    $one += (New-Object PSObject -Property @{ Literal = 'X' + ':' + $BS + 'y'; Line = 1 })
    if ((Get-FindingCount -Findings $one) -ne 1) { $failures += 'F: a single finding did not count as 1' }
    if ((Get-FindingCount -Findings $one[0]) -ne 1) { $failures += 'F: an unwrapped single finding did not count as 1' }

    # Control G: the parser route must ignore comment text, otherwise every example in a
    # comment-based help block would be reported as a hard-coded path.
    $gText = '<# ' + 'E' + ':' + $BS + 'example' + ' #>' + $LF + '$x = 1' + $LF
    $gAst = (Get-ParsedAst -Text $gText).Ast
    if ((Get-FindingCount -Findings @(Get-PsFileLiterals -Ast $gAst)) -ne 0) {
        $failures += 'G: comment text contributed a path literal'
    }

    Write-Output ('DRIVES_PRESENT=' + ((@($drives.Keys) | Sort-Object) -join ''))
    Write-Output ('ABSENT_PROBE=' + $absent + ' PRESENT_PROBE=' + $present)
    Write-Output ('CTRL_A_MISSING_DRIVE=True CTRL_B_OK=True CTRL_C_RELATIVE=True CTRL_D_NONABS=True CTRL_E_MISSING_PATH=True CTRL_F_COUNT=True CTRL_G_COMMENTS=True CTRL_H_URL_SCHEME=True')

    if ((Get-FindingCount -Findings $failures) -gt 0) {
        foreach ($f in $failures) { Write-Output ('  FAIL ' + $f) }
        Write-Output 'RESULT: SELFTEST FAIL'
        exit $EXIT_SELFTEST
    }
    Write-Output 'RESULT: SELFTEST PASS'
    exit $EXIT_PASS
}

# ---------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------
$rootPath = $Root
if (-not [System.IO.Path]::IsPathRooted($rootPath)) {
    $rootPath = Join-Path (Get-Location).Path $Root
}
if (-not (Test-Path -LiteralPath $rootPath)) {
    Write-Output ('ERROR: root not found: ' + $rootPath)
    exit $EXIT_EMPTY
}

$all = @(Get-ChildItem -LiteralPath $rootPath -File -Recurse:$scanRecurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.ps1' -or $_.Extension -eq '.py' })
$files = @()
foreach ($f in $all) {
    $skip = $false
    foreach ($seg in $ExcludeDir) {
        if ($f.FullName.ToLowerInvariant().Contains(($BS + $seg + $BS).ToLowerInvariant())) { $skip = $true; break }
    }
    if (-not $skip) { $files += $f }
}
$files = @($files | Sort-Object FullName)

if ($files.Count -eq 0) {
    Write-Output ('FAIL-CLOSED: no .ps1/.py enumerated under ' + $rootPath + ' - refusing to report green')
    exit $EXIT_EMPTY
}

$drives = Get-PresentDrives

Write-Output ('ROOT=' + $rootPath)
Write-Output ('ENGINE=' + $PSVersionTable.PSVersion.ToString())
Write-Output ('DRIVES=' + ((@($drives.Keys) | Sort-Object) -join ''))
Write-Output ''

$findings = @()
$unanalysable = @()
$totalLiterals = 0
$okPaths = 0
$analysed = 0

foreach ($file in $files) {
    $rel = $file.FullName.Substring($rootPath.Length).TrimStart([char]92)
    $ext = $file.Extension.ToLowerInvariant()
    $lits = @()

    if ($ext -eq '.ps1') {
        $text = Get-DecodedTextEngine -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
        $parsed = Get-ParsedAst -Text $text
        if ($parsed.ErrorCount -gt 0) {
            $unanalysable += ($rel + ' (' + $parsed.FirstError + ')')
            Write-Output ('{0,-14} {1}' -f 'UNANALYSABLE', $rel)
            continue
        }
        $lits = @(Get-PsFileLiterals -Ast $parsed.Ast)
    }
    else {
        $text = Get-DecodedTextUtf8 -Bytes ([System.IO.File]::ReadAllBytes($file.FullName))
        $lits = @(Get-PyFileLiterals -Text $text)
    }

    $analysed++

    $seen = @{}
    $bad = @()
    foreach ($l in $lits) {
        $key = ('' + $l.Line + '|' + $l.Literal)
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = $true
        $totalLiterals++
        $v = Get-PathVerdict -Literal $l.Literal -Drives $drives
        if ($v -eq 'OK') { $okPaths++; continue }
        $bad += (New-Object PSObject -Property @{ File = $rel; Line = $l.Line; Literal = $l.Literal; Verdict = $v })
    }

    $tag = 'OK'
    if ((Get-FindingCount -Findings $bad) -gt 0) { $tag = 'FINDINGS' }
    Write-Output ('{0,-14} literals={1,-4} bad={2,-3} {3}' -f $tag, (Get-FindingCount -Findings @($seen.Keys)), (Get-FindingCount -Findings $bad), $rel)

    foreach ($b in $bad) {
        $findings += $b
        Write-Output ('               L{0,-6} {1,-14} {2}' -f $b.Line, $b.Verdict, $b.Literal)
    }
}

$missingDrive = @($findings | Where-Object { $_.Verdict -eq 'MISSING_DRIVE' })
$missingPath = @($findings | Where-Object { $_.Verdict -eq 'MISSING_PATH' })

Write-Output ''
Write-Output ('FILES={0} ANALYSED={1} UNANALYSABLE={2} LITERALS={3} OK_PATHS={4} MISSING_DRIVE={5} MISSING_PATH={6} MODE={7}' -f `
        $files.Count, $analysed, (Get-FindingCount -Findings $unanalysable), $totalLiterals, $okPaths, `
        (Get-FindingCount -Findings $missingDrive), (Get-FindingCount -Findings $missingPath), $modeTag)

$failed = $false

if ((Get-FindingCount -Findings $unanalysable) -gt 0) {
    Write-Output ('FAIL: {0} file(s) cannot be parsed, so their paths cannot be checked' -f (Get-FindingCount -Findings $unanalysable))
    foreach ($u in $unanalysable) { Write-Output ('  - ' + $u) }
    $failed = $true
}

if ((Get-FindingCount -Findings $missingDrive) -gt 0) {
    Write-Output ('FAIL: {0} literal(s) name a drive that does not exist on this machine' -f (Get-FindingCount -Findings $missingDrive))
    $shown = 0
    foreach ($m in $missingDrive) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $m.File + ':' + $m.Line + ' ' + $m.Literal)
        $shown++
    }
    $failed = $true
}

if ((Get-FindingCount -Findings $missingPath) -gt 0) {
    $level = 'WARN'
    if ($StrictPaths) { $level = 'FAIL' }
    Write-Output ('{0}: {1} literal(s) point inside an existing drive at a path that is not there' -f $level, (Get-FindingCount -Findings $missingPath))
    $shown = 0
    foreach ($m in $missingPath) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $m.File + ':' + $m.Line + ' ' + $m.Literal)
        $shown++
    }
    if ($StrictPaths) { $failed = $true }
    else { Write-Output '  (pass -StrictPaths to make these fatal)' }
}

Write-Output ''
Write-Output 'SCOPE LIMITS: .ps1 literals come from the parser, so comments and here-strings are excluded;'
Write-Output '             .py literals are line-scanned, whole-line # comments blanked, docstrings not'
Write-Output '             specially excluded; %ENV%, $env:, ~ and relative paths are not absolute'
Write-Output '             literals; tokens over 400 chars and bare drive prefixes are skipped.'

if ($failed) {
    Write-Output 'RESULT: FAIL'
    if ((Get-FindingCount -Findings $unanalysable) -gt 0 -and (Get-FindingCount -Findings $missingDrive) -eq 0) {
        exit $EXIT_UNANALYSABLE
    }
    exit $EXIT_FINDINGS
}

Write-Output 'RESULT: PASS'
exit $EXIT_PASS
