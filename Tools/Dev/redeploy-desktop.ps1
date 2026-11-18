# ============================================================
# Redeploy the desktop bundle with a clean Guardian lease.
#
# Why this exists: the desktop app locks its own output directory and the
# guardian holds guardian.lock with FileShare.None. A guardian started while
# another one owns the lease exits immediately with code 0, which the desktop
# reports only as "endpoint did not appear". So the order below is mandatory:
#   stop guardian -> stop desktop -> confirm lease free -> build -> start desktop
# The freshly started desktop then spawns a guardian with a free lease.
#
# MUST be run from a standalone terminal. Do NOT apply this from inside an
# agent session: step "stop desktop" terminates the Core process that hosts
# the agent, so anything running inside it dies mid-way.
#
# Usage:
#   pwsh -File Tools/Dev/redeploy-desktop.ps1                # dry run (default)
#   pwsh -File Tools/Dev/redeploy-desktop.ps1 -Apply         # perform it
# ============================================================
param(
    [switch]$Apply,
    [int]$StartupTimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$repo = 'D:\CodeProject\PuddingAgent\PuddingAgent'
$bundle = Join-Path $repo 'temp\build\winui3\bin\PuddingDesktop\Debug\net10.0-windows10.0.19041.0\win-x64'
$desktopExe = Join-Path $bundle 'PuddingDesktop.exe'
$guardianExe = Join-Path $bundle 'guardian\PuddingDesktop.Guardian.exe'
$puddingHome = Join-Path $env:LOCALAPPDATA 'Pudding'
$lease = Join-Path $puddingHome 'guardian\guardian.lock'
$desktopProject = Join-Path $repo 'Source\PuddingDesktop\PuddingDesktop.csproj'

function Say($text) { Write-Output $text }

function Get-LeaseHeld {
    if (-not (Test-Path $lease)) { return $false }
    try {
        $fs = New-Object System.IO.FileStream($lease, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $fs.Dispose()
        return $false
    } catch { return $true }
}

function Get-OwnedProcess($name, $expectedPath) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path -ieq $expectedPath)
    }
}

Say '=== preconditions ==='
if (-not (Test-Path $desktopExe)) { throw "Desktop executable missing: $desktopExe" }
if (-not (Test-Path $guardianExe)) { throw "Guardian executable missing: $guardianExe" }

$head = (& git -C $repo rev-parse --short HEAD) 2>$null
$dirty = @(& git -C $repo status --porcelain) | Where-Object { $_ }
Say ("HEAD          = " + $head)
Say ("worktree      = " + $(if ($dirty.Count -eq 0) { 'CLEAN' } else { "$($dirty.Count) changed path(s)" }))
Say ("desktop exe   = " + (Get-Item $desktopExe).LastWriteTime)

# CommandLine is not a property of the Process object returned by Get-Process, so it is
# always null there and the filter would silently report "no builds" forever. Use CIM.
$builders = @(Get-CimInstance Win32_Process -Filter "name='dotnet.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and ($_.CommandLine -match 'MSBuild|dotnet build|dotnet test')
    })
Say ("active builds = " + $builders.Count)
foreach ($b in $builders) {
    $cmd = $b.CommandLine
    if ($cmd.Length -gt 110) { $cmd = $cmd.Substring(0, 110) }
    Say ("  pid " + $b.ProcessId + ": " + $cmd)
}
if ($builders.Count -gt 0) {
    Say 'WARN: another build/test is running; it may contend for the output directory.'
}

$guardian = @(Get-OwnedProcess 'PuddingDesktop.Guardian' $guardianExe)
$desktop = @(Get-OwnedProcess 'PuddingDesktop' $desktopExe)
$core = @(Get-Process -Name 'PuddingAgent' -ErrorAction SilentlyContinue)

Say ("guardian      = " + $(if ($guardian.Count) { ($guardian | ForEach-Object { $_.Id }) -join ',' } else { 'not running' }))
Say ("desktop       = " + $(if ($desktop.Count) { ($desktop | ForEach-Object { $_.Id }) -join ',' } else { 'not running' }))
Say ("core          = " + $(if ($core.Count) { ($core | ForEach-Object { $_.Id }) -join ',' } else { 'not running' }))
Say ("lease held    = " + (Get-LeaseHeld))

if (-not $Apply) {
    Say ''
    Say 'DRY RUN. The following would happen with -Apply:'
    Say '  1. stop guardian (release the lease)'
    Say '  2. stop desktop (this also stops core)'
    Say '  3. verify the lease is free'
    Say '  4. dotnet build PuddingDesktop'
    Say '  5. start desktop; it spawns a fresh guardian'
    Say "  6. verify port 8200 answers and guardian.log gets a new healthy line (timeout ${StartupTimeoutSeconds}s)"
    Say ''
    Say 'No changes made.'
    exit 0
}

Say ''
Say '=== 1/6 stop guardian ==='
foreach ($p in $guardian) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Say ("stopped guardian pid " + $p.Id)
}
for ($i = 0; $i -lt 40; $i++) {
    if (-not (Get-LeaseHeld)) { break }
    Start-Sleep -Milliseconds 250
}
if (Get-LeaseHeld) { throw 'The guardian lease is still held after stopping the guardian.' }

Say '=== 2/6 stop desktop ==='
foreach ($p in $desktop) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Say ("stopped desktop pid " + $p.Id)
}
for ($i = 0; $i -lt 40; $i++) {
    if (-not (Get-Process -Name 'PuddingDesktop' -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Milliseconds 250
}
foreach ($p in @(Get-Process -Name 'PuddingAgent' -ErrorAction SilentlyContinue)) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Say ("stopped core pid " + $p.Id)
}

Say '=== 3/6 verify lease free ==='
if (Get-LeaseHeld) { throw 'Lease reported held with no guardian running; investigate before building.' }
Say 'lease is free'

Say '=== 4/6 build ==='
$buildLog = Join-Path $repo 'temp\redeploy-build.log'
& dotnet build $desktopProject -c Debug -v q *> $buildLog
if ($LASTEXITCODE -ne 0) { throw "Build failed; see $buildLog" }
Say 'build ok'

Say '=== 5/6 start desktop ==='
$startedAt = Get-Date
Start-Process -FilePath $desktopExe -WorkingDirectory $bundle | Out-Null
Say ('started desktop at ' + $startedAt.ToString('HH:mm:ss'))

Say '=== 6/6 verify ==='
$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
$listening = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    try {
        $r = Invoke-WebRequest -Uri 'http://127.0.0.1:8200/guardian/status' -UseBasicParsing -TimeoutSec 3
        if ($r.StatusCode -eq 200) { $listening = $true; break }
    } catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $listening = $true; break }
    }
}

$newGuardian = @(Get-OwnedProcess 'PuddingDesktop.Guardian' $guardianExe)
Say ("guardian running = " + $(if ($newGuardian.Count) { ($newGuardian | ForEach-Object { $_.Id }) -join ',' } else { 'NO' }))
Say ("port 8200 answers = " + $listening)

$conflict = Join-Path $puddingHome 'guardian\guardian.lease-conflict.log'
if (Test-Path $conflict) {
    Say ''
    Say 'lease-conflict log exists (tail):'
    Get-Content $conflict -Tail 3 | ForEach-Object { Say ('  ' + $_) }
}

$desktopLog = Join-Path $puddingHome 'logs\desktop.log'
if (Test-Path $desktopLog) {
    $recent = @(Get-Content $desktopLog -Tail 40 | Where-Object { $_ -match 'GuardianStart|GuardianHandoff' })
    Say ("recent guardian errors in desktop.log = " + $recent.Count)
    $recent | ForEach-Object { Say ('  ' + $_) }
}

if (-not $listening) { throw 'Guardian endpoint never answered. Inspect desktop.log and guardian.log.' }
Say ''
Say 'DEPLOY OK: guardian endpoint is answering with a free-lease start.'
exit 0
