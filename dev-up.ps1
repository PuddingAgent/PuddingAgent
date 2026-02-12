# ============================================================
# Pudding Agent - local development startup wrapper
# ============================================================
# Python owns the process management and reverse proxy logic.
#
# Usage:
#   .\dev-up.ps1              # start
#   .\dev-up.ps1 -Status      # show status
#   .\dev-up.ps1 -Logs        # follow logs
#   .\dev-up.ps1 -Down        # stop
#   .\dev-up.ps1 -Restart     # restart
#   .\dev-up.ps1 -Rebuild     # stop, rebuild backend, start

param(
    [switch]$Down,
    [switch]$Logs,
    [switch]$Status,
    [switch]$Restart,
    [switch]$Rebuild,
    [switch]$NoInstall,
    [switch]$GuardOn,
    [switch]$GuardOff
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$ArgsList = @()

if ($Down) { $ArgsList += "--down" }
if ($Logs) { $ArgsList += "--logs" }
if ($Status) { $ArgsList += "--status" }
if ($Restart) { $ArgsList += "--restart" }
if ($Rebuild) { $ArgsList += "--rebuild" }
if ($NoInstall) { $ArgsList += "--no-install" }
if ($GuardOn) { $ArgsList += "--guard-on" }
if ($GuardOff) { $ArgsList += "--guard-off" }

python (Join-Path $Root "dev-up.py") @ArgsList
exit $LASTEXITCODE
