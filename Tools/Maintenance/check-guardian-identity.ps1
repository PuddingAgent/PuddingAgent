# ============================================================
# Read-only: does the running guardian actually supervise the running desktop?
#
# Why this exists: the guardian is launched with --adopt-pid <desktop pid>. After a
# desktop restart the previous guardian keeps that stale pid, still answers on the
# control port, and still passes every installation check the desktop performs. The
# result is a control plane that looks healthy while nothing is supervised.
#
# This probe reads process state only (no HTTP, no writes) so it also works against a
# guardian build that predates the /guardian/status desktopPid field.
#
# Exit codes: 0 = every guardian supervises a live desktop
#             1 = stale adoption, or no guardian at all
#             3 = cannot determine (unparseable command line / access denied)
#
# Usage: pwsh -File Tools/Maintenance/check-guardian-identity.ps1 [-Json]
# ============================================================
param([switch]$Json)

$ErrorActionPreference = 'Stop'

function Get-ProcessState {
    $guardians = @(Get-CimInstance Win32_Process -Filter "name='PuddingDesktop.Guardian.exe'" -ErrorAction SilentlyContinue)
    $desktops = @(Get-Process -Name 'PuddingDesktop' -ErrorAction SilentlyContinue)
    [pscustomobject]@{
        Guardians = @($guardians | ForEach-Object {
                $adopt = $null
                if ($_.CommandLine -match '--adopt-pid\s+(\d+)') { $adopt = [int]$Matches[1] }
                [pscustomobject]@{ Id = [int]$_.ProcessId; AdoptPid = $adopt; CommandLine = $_.CommandLine }
            })
        DesktopIds = @($desktops | ForEach-Object { $_.Id })
    }
}

# A guardian whose adopted pid is a live desktop is healthy. A guardian whose adopted pid
# is gone supervises nothing; keeping it alive is strictly worse than reporting it, because
# the desktop then believes it is managed.
function Get-Verdicts {
    param($State)
    if ($State.Guardians.Count -eq 0) {
        return @([pscustomobject]@{ Verdict = 'NO_GUARDIAN'; GuardianId = $null; AdoptPid = $null; Reason = 'no PuddingDesktop.Guardian process' })
    }
    $live = @{}
    foreach ($id in $State.DesktopIds) { $live[[string]$id] = $true }
    $rows = foreach ($g in $State.Guardians) {
        if ($null -eq $g.AdoptPid) {
            [pscustomobject]@{ Verdict = 'UNKNOWN'; GuardianId = $g.Id; AdoptPid = $null; Reason = 'command line has no --adopt-pid' }
        }
        elseif ($live.ContainsKey([string]$g.AdoptPid)) {
            [pscustomobject]@{ Verdict = 'OK'; GuardianId = $g.Id; AdoptPid = $g.AdoptPid; Reason = 'adopted desktop is alive' }
        }
        else {
            [pscustomobject]@{ Verdict = 'STALE_ADOPTION'; GuardianId = $g.Id; AdoptPid = $g.AdoptPid; Reason = 'adopted desktop has exited; nothing is supervised' }
        }
    }
    return @($rows)
}

# Dot-sourcing this file with PUDDING_PROBE_SOURCE_ONLY=1 loads the functions and returns without
# probing, so the verdict branches can be exercised against synthetic process state.
if ($env:PUDDING_PROBE_SOURCE_ONLY -eq '1') { return }

try { $state = Get-ProcessState }
catch {
    $msg = $_.Exception.Message
    if ($Json) { [pscustomobject]@{ Verdict = 'UNKNOWN'; Error = $msg } | ConvertTo-Json -Compress | Write-Output }
    else { Write-Output 'UNKNOWN: cannot read process state' ; Write-Output ("  " + $msg) }
    exit 3
}

$verdicts = @(Get-Verdicts -State $state)

if ($Json) {
    [pscustomobject]@{
        Desktops = $state.DesktopIds
        Results  = $verdicts
    } | ConvertTo-Json -Depth 4 -Compress | Write-Output
}
else {
    Write-Output "[guardian identity]"
    Write-Output ("  live desktops = " + $(if ($state.DesktopIds.Count) { ($state.DesktopIds -join ',') } else { 'none' }))
    if ($verdicts.Count -eq 0) { Write-Output '  (no rows)' }
    foreach ($v in $verdicts) {
        $adopt = if ($null -eq $v.AdoptPid) { '-' } else { $v.AdoptPid }
        Write-Output ("  {0,-15} guardian={1,-7} adopt={2,-7} {3}" -f $v.Verdict, $v.GuardianId, $adopt, $v.Reason)
    }
}

$bad = @($verdicts | Where-Object { $_.Verdict -in @('STALE_ADOPTION', 'NO_GUARDIAN') })
$unknown = @($verdicts | Where-Object { $_.Verdict -eq 'UNKNOWN' })
if ($bad.Count -gt 0) {
    if (-not $Json) {
        Write-Output ''
        Write-Output 'FAIL: the control plane is not supervising the running desktop.'
        Write-Output '      Repair: pwsh -File Tools/Dev/redeploy-desktop.ps1 -Apply (stops guardian, desktop and core).'
    }
    exit 1
}
if ($unknown.Count -gt 0) { exit 3 }
if (-not $Json) { Write-Output 'OK: guardian supervises the running desktop.' }
exit 0
