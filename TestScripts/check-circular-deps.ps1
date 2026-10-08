<#
.SYNOPSIS
    DEPRECATED shim. Superseded by check-project-layering.ps1 (C-1 + C-4).

.DESCRIPTION
    This file used to be a standalone circular-dependency checker. It was RETIRED on
    2026-10-08 because it could not be repaired into a trustworthy gate. Five measured
    defects, any one of which is disqualifying:

      (1) FALSE PASS BY CONSTRUCTION - the cycle counter was declared but never
          incremented anywhere, so the final branch always took the
          "no circular dependency found" path regardless of what the DFS saw.
      (2) WRONG ROOT - the repository root was derived with three Split-Path -Parent
          hops from the script path, landing one level ABOVE the repository, so the
          scanned directory did not exist.
      (3) PARTIAL SCOPE - it only scanned <root>\Source, so Tests\ and external\ were
          invisible.
      (4) RUNTIME INCOMPATIBILITY - it called Split-Path -LeafBase, a PowerShell 6+
          parameter. Under Windows PowerShell 5.1 that call throws
          ParameterBindingException. Measured 2026-10-08 by
          TestScripts/check-script-runtime-compat.ps1 (C-6):
              SPLITPATH_HAS_LEAFBASE=False   CALL_ERR=ParameterBindingException
          Note that the script PARSES cleanly and declares no engine requirement, so
          C-5 cannot see this defect - only C-6 can.
      (5) NO EXIT CODE - it never called exit, so no gate could consume its verdict.

    The file NAME is preserved so that any existing caller or document keeps working.
    It now forwards to check-project-layering.ps1 and propagates that script's exit
    code. No verdict is produced here.

.PARAMETER Root
    Repository root. Default: empty, which lets check-project-layering.ps1 derive it
    from its own PSScriptRoot.

.PARAMETER SelfTest
    Forwarded to check-project-layering.ps1.

.EXIT CODES
    0 = PASS / 1 = FAIL / 4 = SELFTEST_FAIL, exactly as returned by
    check-project-layering.ps1. Additionally 3 = FAIL-CLOSED, returned here when the
    successor script cannot be found, so a missing successor can never look green.

.NOTE
    Keep this file ASCII-ONLY. Windows PowerShell 5.1 decodes BOM-less script files as
    ANSI, so a UTF-8 file containing non-ASCII text fails to parse on this machine.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-circular-deps.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File TestScripts\check-circular-deps.ps1 -SelfTest
#>

[CmdletBinding()]
param(
    [string]$Root = '',
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$successor = Join-Path $PSScriptRoot 'check-project-layering.ps1'
if (-not (Test-Path -LiteralPath $successor)) {
    Write-Output ('FAIL-CLOSED: successor gate not found: ' + $successor)
    exit 3
}

Write-Output 'DEPRECATED (2026-10-08): check-circular-deps.ps1 now delegates to check-project-layering.ps1'
Write-Output 'Retired because of 5 measured defects, including a false PASS and a PS 6+ parameter'
Write-Output 'that made it die at runtime under Windows PowerShell 5.1. See the file header.'
Write-Output ''

$forward = @{}
if (-not [string]::IsNullOrWhiteSpace($Root)) { $forward['Root'] = $Root }
if ($SelfTest) { $forward['SelfTest'] = $true }

& $successor @forward
exit $LASTEXITCODE
