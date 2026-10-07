<#
.SYNOPSIS
  C-5 gate: every PowerShell script in scope must be loadable by the repo's only
  PowerShell engine, Windows PowerShell 5.1.

.DESCRIPTION
  Windows PowerShell 5.1 decides how to decode a .ps1 file by looking for a UTF-8 BOM:
    - bytes begin with EF BB BF  -> decoded as UTF-8 (BOM stripped)
    - otherwise                  -> decoded with [Text.Encoding]::Default (system ANSI code page)
  This script reproduces that rule in-process and then runs the real PowerShell
  parser over the decoded text. A parse error means the script CANNOT execute on
  this machine, even though it may look perfectly fine in a UTF-8 editor.

  This is the defect class observed on 2026-10-08: UTF-8 with no BOM plus non-ASCII
  content makes 5.1 swallow structural characters and fail with errors such as
  TerminatorExpectedAtEndOfString.

  The intended remedy is NOT "always add a BOM". The gate is "the script must parse
  when the engine reads it". An ASCII-only script with no BOM is perfectly valid and
  passes here. BOM presence and non-ASCII byte presence are reported as facts only.

.PARAMETER Root
  Directory to scan. Default: TestScripts (resolved relative to the current dir).

.PARAMETER Recurse
  Scan subdirectories too. Default: top level only.

.PARAMETER ExcludeDir
  Directory names (any path segment) to skip. Default: temp, node_modules, .git, bin, obj, dist, .pudding

.PARAMETER SelfTest
  Run the in-memory positive/negative controls and exit. Writes no files.

.EXIT CODES
  0 = PASS       : files were enumerated and every script parses clean
  1 = FAIL       : at least one script does not parse
  3 = FAIL-CLOSED: no script enumerated in scope; refuses to report green
  4 = FAIL       : self-test did not detect the planted defect
#>

[CmdletBinding()]
param(
    [string]$Root = 'TestScripts',
    [switch]$Recurse,
    [string[]]$ExcludeDir = @('temp', 'node_modules', '.git', 'bin', 'obj', 'dist', '.pudding'),
    [switch]$SelfTest,
    [int]$MaxReport = 100
)

$ErrorActionPreference = 'Stop'

$EXIT_PASS = 0
$EXIT_BROKEN = 1
$EXIT_EMPTY = 3
$EXIT_SELFTEST = 4

# Core: mirror exactly how Windows PowerShell 5.1 decodes a script file.
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

    return New-Object PSObject -Property @{
        HasBom      = $hasBom
        HasNonAscii = $hasNonAscii
        ErrorCount  = $errCount
        FirstError  = $firstError
    }
}

function Get-ScriptLoadState {
    param([string]$Path)
    return Get-LoadStateFromBytes -Bytes ([System.IO.File]::ReadAllBytes($Path))
}

if ($SelfTest) {
    # Positive control: UTF-8, no BOM, one non-ASCII char inside a single-quoted string.
    # 5.1 decodes the UTF-8 bytes as ANSI, swallows the closing quote and fails.
    $cn = [string][char]0x4E2D
    $src = '$s = ' + "'" + $cn + "'" + [char]10
    $noBom = [System.Text.Encoding]::UTF8.GetBytes($src)

    $withBom = New-Object byte[] ($noBom.Length + 3)
    $withBom[0] = 0xEF
    $withBom[1] = 0xBB
    $withBom[2] = 0xBF
    [Array]::Copy($noBom, 0, $withBom, 3, $noBom.Length)

    # Negative control: pure ASCII, no BOM, must stay clean.
    $asciiOnly = [System.Text.Encoding]::ASCII.GetBytes('$x = 1' + [char]10)

    $s1 = Get-LoadStateFromBytes -Bytes $noBom
    $s2 = Get-LoadStateFromBytes -Bytes $withBom
    $s3 = Get-LoadStateFromBytes -Bytes $asciiOnly

    $failures = @()
    if ($s1.ErrorCount -lt 1) { $failures += 'positive control (no BOM + non-ASCII) was NOT detected' }
    if (-not $s1.HasNonAscii) { $failures += 'positive control non-ASCII flag not set' }
    if ($s1.HasBom) { $failures += 'positive control reported a BOM' }
    if ($s2.ErrorCount -ne 0) { $failures += 'BOM control did not parse clean' }
    if (-not $s2.HasBom) { $failures += 'BOM control BOM flag not set' }
    if ($s3.ErrorCount -ne 0) { $failures += 'ASCII control did not parse clean' }

    Write-Output ('POSCTRL errors={0} first={1}' -f $s1.ErrorCount, $s1.FirstError)
    Write-Output ('BOMCTRL errors={0}' -f $s2.ErrorCount)
    Write-Output ('ASCIICTRL errors={0}' -f $s3.ErrorCount)

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

$broken = @()
$bomCount = 0
$nonAsciiCount = 0

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
    Write-Output ('{0,-7} bom={1,-5} nonascii={2,-5} errors={3,-3} {4}' -f `
        $tag, $state.HasBom, $state.HasNonAscii, $state.ErrorCount, $rel)
    if ($state.ErrorCount -gt 0 -and $state.FirstError) {
        Write-Output ('        first: ' + $state.FirstError)
    }
}

Write-Output ''
Write-Output ('FILES={0} BOM={1} NONASCII={2} BROKEN={3}' -f $files.Count, $bomCount, $nonAsciiCount, $broken.Count)

if ($broken.Count -gt 0) {
    Write-Output ('FAIL: {0} script(s) cannot be loaded by Windows PowerShell 5.1' -f $broken.Count)
    $shown = 0
    foreach ($b in $broken) {
        if ($shown -ge $MaxReport) { break }
        Write-Output ('  - ' + $b)
        $shown++
    }
    Write-Output 'RESULT: FAIL'
    exit $EXIT_BROKEN
}

Write-Output 'RESULT: PASS'
exit $EXIT_PASS
