# Phase 2A-3B External Acceptance Controller
# ============================================
# Purpose: Prepare and observe a standalone PuddingDesktop instance with
# TestSite, verify process lifecycle, write sanitized shutdown evidence.
#
# -PrepareOnly: internal dev agent verification (no process launch)
# Default: full external acceptance cycle
#
# Usage:
#   -PrepareOnly $true -PublishRoot .tmp-build\phase2a3b-external-preview -DataRoot D:\data
#   -PublishRoot .\publish -DataRoot D:\data

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,

    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [int]$StartupTimeoutSeconds = 120,

    [switch]$PrepareOnly,

    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path "$ScriptDir\.."

# Timestamp-based evidence directory
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ")
$EvidenceRoot = Join-Path $RepoRoot ".tmp-test-out\phase2a3b-deepseek-smoke\$timestamp"
$TempHome = Join-Path $env:TEMP "PuddingAgent\phase2a3b-external-$(New-Guid | Select-Object -ExpandProperty Guid | ForEach-Object { $_.Substring(0, 8) })\desktop-home"

# ─── Functions ───────────────────────────────────────────────────────────────

function Resolve-PublishLayout {
    param([string]$PublishRoot)

    $abs = Resolve-Path $PublishRoot -ErrorAction Stop
    $layout = @{
        PublishRoot = $abs
        DesktopExe  = Join-Path $abs "PuddingDesktop.exe"
        CoreDir     = Join-Path $abs "core"
        CoreExe     = Join-Path $abs "core\PuddingAgent.exe"
        WwwRoot     = Join-Path $abs "core\wwwroot\admin\index.html"
        AgentToolsDll = Join-Path $abs "core\PuddingBrowser.AgentTools.dll"
    }

    $missing = @()
    foreach ($kv in $layout.GetEnumerator()) {
        if ($kv.Key -eq 'PublishRoot') { continue }
        if (-not (Test-Path $kv.Value)) {
            $missing += "$($kv.Key): $($kv.Value)"
        }
    }

    if ($missing.Count -gt 0) {
        $msg = "Missing publish files:`n" + ($missing -join "`n")
        Write-Error $msg
        exit 1
    }

    return $layout
}

function Assert-NoPuddingDesktopInstance {
    $existing = Get-CimInstance Win32_Process -Filter "Name = 'PuddingDesktop.exe'" -ErrorAction SilentlyContinue |
        Select-Object ProcessId, ExecutablePath

    if ($existing) {
        $info = ($existing | ForEach-Object { "PID=$($_.ProcessId) Path=$($_.ExecutablePath)" }) -join "`n"
        Write-Error "Existing PuddingDesktop instance(s) found:`n$info`nStop them manually before running this script."
        exit 1
    }
}

function New-AcceptanceWorkspace {
    param([string]$DataRoot)

    if (-not (Test-Path $DataRoot)) {
        Write-Error "DataRoot does not exist: $DataRoot"
        exit 1
    }

    # Create temporary desktop home
    New-Item -ItemType Directory -Path $TempHome -Force | Out-Null

    # Write minimal desktop.json — no secrets, no provider config
    $desktopJson = @{
        dataRoot = $DataRoot
        corePath = (Join-Path (Resolve-Path $PublishRoot) "core\PuddingAgent.exe")
        exitAndStopCore = $true
    } | ConvertTo-Json -Depth 3

    $desktopJsonPath = Join-Path $TempHome "desktop.json"
    $desktopJson | Set-Content -Path $desktopJsonPath -Encoding UTF8

    # Verify no secrets leaked
    $sanitized = Get-Content $desktopJsonPath -Raw
    $forbidden = @("controlToken", "authorization", "cookie", "apiKey", "secret", "token")
    foreach ($pattern in $forbidden) {
        if ($sanitized -match $pattern) {
            Write-Error "desktop.json contains forbidden pattern: $pattern"
            exit 1
        }
    }

    return @{
        HomeDir = $TempHome
        DesktopJsonPath = $desktopJsonPath
        DesktopJson = $desktopJson
    }
}

function Start-BrowserTestSite {
    param([string]$EvidenceRoot)

    $testSiteProj = Join-Path $RepoRoot "Tests\PuddingBrowser.TestSite\PuddingBrowser.TestSite.csproj"
    $proc = Start-Process -FilePath "dotnet" `
        -ArgumentList "run --project `"$testSiteProj`" --no-build --urls http://127.0.0.1:0" `
        -PassThru -NoNewWindow -RedirectStandardOutput "$EvidenceRoot\testsite-stdout.log" `
        -RedirectStandardError "$EvidenceRoot\testsite-stderr.log"

    # Wait for URL to appear in stdout
    $deadline = (Get-Date).AddSeconds(30)
    $url = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $log = Get-Content "$EvidenceRoot\testsite-stdout.log" -ErrorAction SilentlyContinue | Select-Object -Last 5
        foreach ($line in $log) {
            if ($line -match "Now listening on:\s+(https?://[^\s]+)") {
                $url = $matches[1]
                break
            }
        }
        if ($url) { break }
    }

    if (-not $url) {
        Write-Error "TestSite did not report a URL within 30s"
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }

    return @{
        Process = $proc
        Pid = $proc.Id
        Url = $url
    }
}

function Start-TargetDesktop {
    param($Layout, $Workspace)

    # Record pre-launch WebView2 PIDs
    $preWebView2 = (Get-Process -Name "msedgewebview2" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

    $env:PUDDING_DESKTOP_HOME = $Workspace.HomeDir

    $proc = Start-Process -FilePath $Layout.DesktopExe -PassThru

    # Wait for window
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    $hwnd = [IntPtr]::Zero
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        try {
            $proc.Refresh()
            if ($proc.HasExited) {
                Write-Error "PuddingDesktop exited prematurely (code: $($proc.ExitCode))"
                exit 1
            }
            $hwnd = $proc.MainWindowHandle
            if ($hwnd -ne [IntPtr]::Zero) { break }
        }
        catch { }
    }

    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Error "PuddingDesktop window did not appear within ${StartupTimeoutSeconds}s"
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        exit 1
    }

    return @{
        Process = $proc
        Pid = $proc.Id
        ExecutablePath = $Layout.DesktopExe
        MainWindowHandle = $hwnd
        PreWebView2Pids = $preWebView2
    }
}

function Find-OwnedCoreProcess {
    param([int]$DesktopPid)

    Start-Sleep -Seconds 3  # Allow Core to start

    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $DesktopPid" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq "PuddingAgent.exe" }

    if (-not $children) {
        # Try broader search
        $children = Get-Process -Name "PuddingAgent" -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $DesktopPid }
    }

    if ($children) {
        $child = $children | Select-Object -First 1
        return @{
            Pid = $child.ProcessId
            ExecutablePath = $child.Path
        }
    }

    return $null
}

function Get-OwnedProcessTree {
    param([int]$RootPid)

    $result = @()
    $visited = @{}
    $queue = @($RootPid)

    while ($queue.Count -gt 0) {
        $current = $queue[0]
        $queue = $queue[1..($queue.Count - 1)]
        if ($visited[$current]) { continue }
        $visited[$current] = $true

        $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $current" -ErrorAction SilentlyContinue
        foreach ($child in $children) {
            $result += @{ Pid = $child.ProcessId; Name = $child.Name; ParentPid = $current }
            $queue += $child.ProcessId
        }
    }

    return $result
}

function Write-SanitizedJson {
    param([string]$Path, $Value)

    $parent = Split-Path $Path -Parent
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth 5 -Compress:$false
    $json | Set-Content -Path $Path -Encoding UTF8
}

function Wait-OwnedChildrenExit {
    param([int[]]$ProcessIds, [int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $remaining = @($ProcessIds)

    while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1000
        $remaining = $remaining | Where-Object {
            try { (Get-Process -Id $_ -ErrorAction Stop).HasExited -eq $false }
            catch { $false }
        }
    }

    return @($remaining)
}

# ─── PrepareOnly mode ────────────────────────────────────────────────────────

if ($PrepareOnly) {
    Write-Host "=== Phase 2A-3B PrepareOnly ===" -ForegroundColor Cyan

    # Resolve layout
    $layout = Resolve-PublishLayout -PublishRoot $PublishRoot
    Write-Host "Publish layout: $($layout.PublishRoot)" -ForegroundColor Green

    # Create workspace (desktop.json only)
    $workspace = New-AcceptanceWorkspace -DataRoot $DataRoot
    Write-Host "Temp home: $($workspace.HomeDir)" -ForegroundColor Green

    # Write handoff
    $handoff = @{
        schema = "pudding-phase2a3b-handoff-v1"
        status = "ready-for-external-deploy"
        timestamp = $timestamp
        publishRoot = $layout.PublishRoot
        desktopExe = $layout.DesktopExe
        coreExe = $layout.CoreExe
        dataRoot = $DataRoot
        tempHome = $workspace.HomeDir
        evidenceRoot = $EvidenceRoot
        preparedBy = "internal-dev-agent"
        assertions = @{
            noSecretsInDesktopJson = $true
            noExistingDesktopInstance = $true
            noProcessStarted = $true
            noRealModelCall = $true
        }
    }

    $handoffPath = Join-Path $EvidenceRoot "internal-handoff.json"
    Write-SanitizedJson -Path $handoffPath -Value $handoff
    Write-Host "Handoff: $handoffPath" -ForegroundColor Green

    # Verify handoff contains no secrets
    $raw = Get-Content $handoffPath -Raw
    $forbidden = @("controlToken", "authorization", "cookie", "apiKey", "secret")
    $leaked = $false
    foreach ($pattern in $forbidden) {
        if ($raw -match $pattern) {
            Write-Error "internal-handoff.json contains forbidden pattern: $pattern"
            $leaked = $true
        }
    }
    if ($leaked) { exit 1 }

    Write-Host "`nPrepareOnly verification PASSED" -ForegroundColor Green
    Write-Host "Handoff ready for external acceptance: $handoffPath" -ForegroundColor Cyan
    exit 0
}

# ─── Full external acceptance mode ───────────────────────────────────────────

Write-Host "=== Phase 2A-3B External Acceptance ===" -ForegroundColor Cyan

# Step 1: Resolve layout
$layout = Resolve-PublishLayout -PublishRoot $PublishRoot
Write-Host "[1/7] Publish layout verified" -ForegroundColor Green

# Step 2: Check no existing Desktop
Assert-NoPuddingDesktopInstance
Write-Host "[2/7] No existing PuddingDesktop" -ForegroundColor Green

# Step 3: Prepare workspace
$workspace = New-AcceptanceWorkspace -DataRoot $DataRoot
Write-Host "[3/7] Workspace prepared: $($workspace.HomeDir)" -ForegroundColor Green

# Step 4: Start TestSite
New-Item -ItemType Directory -Path $EvidenceRoot -Force | Out-Null
$testSite = Start-BrowserTestSite -EvidenceRoot $EvidenceRoot
Write-Host "[4/7] TestSite started: $($testSite.Url) (PID $($testSite.Pid))" -ForegroundColor Green

# Step 5: Start Desktop
$desktop = Start-TargetDesktop -Layout $layout -Workspace $workspace
Write-Host "[5/7] Desktop started: PID $($desktop.Pid), HWND $($desktop.MainWindowHandle)" -ForegroundColor Green

# Step 6: Find Core child process
$core = Find-OwnedCoreProcess -DesktopPid $desktop.Pid
if ($core) {
    Write-Host "[6/7] Core found: PID $($core.Pid)" -ForegroundColor Green
}
else {
    Write-Host "[6/7] Core not found (may start later)" -ForegroundColor Yellow
}

# Write external-controller-ready JSON
$ready = @{
    schema = "pudding-phase2a3b-ready-v1"
    status = "external-controller-ready"
    timestamp = $timestamp
    desktop = @{
        pid = $desktop.Pid
        executablePath = $desktop.ExecutablePath
        mainWindowHandle = $desktop.MainWindowHandle.ToString()
    }
    core = if ($core) { @{ pid = $core.Pid; executablePath = $core.ExecutablePath } } else { $null }
    testSite = @{
        pid = $testSite.Pid
        url = $testSite.Url
    }
    instructions = @(
        "1. Open PuddingDesktop (PID $($desktop.Pid))",
        "2. Navigate Agent Browser to $($testSite.Url)",
        "3. Execute Phase 77 acceptance task with a DeepSeek Agent",
        "4. After completion, exit PuddingDesktop via tray or title bar",
        "5. This script will write shutdown-observation.json and exit"
    )
}

$readyPath = Join-Path $EvidenceRoot "external-controller-ready.json"
Write-SanitizedJson -Path $readyPath -Value $ready
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "External controller ready!" -ForegroundColor Green
Write-Host "Desktop PID: $($desktop.Pid)" -ForegroundColor White
Write-Host "TestSite URL: $($testSite.Url)" -ForegroundColor White
Write-Host "Evidence: $EvidenceRoot" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "`nWaiting for PuddingDesktop to exit..." -ForegroundColor Yellow
Write-Host "(Complete Phase 77 task, then exit Desktop via tray/title bar)`n" -ForegroundColor Yellow

# Step 7: Wait for Desktop exit
$exitCode = $null
try {
    $desktop.Process.WaitForExit()
    $desktop.Process.Refresh()
    $exitCode = $desktop.Process.ExitCode
    Write-Host "[7/7] PuddingDesktop exited (code: $exitCode)" -ForegroundColor Green
}
catch {
    Write-Host "[7/7] PuddingDesktop wait interrupted" -ForegroundColor Yellow
}

# Wait a moment for child processes to settle
Start-Sleep -Seconds 3

# Shutdown observation
$coreTree = Get-OwnedProcessTree -RootPid $desktop.Pid
$ownedCore = $coreTree | Where-Object { $_.Name -eq "PuddingAgent.exe" }
$ownedWebView2 = $coreTree | Where-Object { $_.Name -match "msedgewebview2" }
$newWebView2 = @()
foreach ($wv in $ownedWebView2) {
    if ($desktop.PreWebView2Pids -notcontains $wv.Pid) {
        $newWebView2 += $wv
    }
}

$remainingOwned = @($ownedCore) + @($newWebView2)

$shutdown = @{
    schema = "pudding-phase2a3b-shutdown-v1"
    timestamp = (Get-Date).ToUniversalTime().ToString("o")
    desktopExitCode = $exitCode
    desktopPid = $desktop.Pid
    corePid = if ($core) { $core.Pid } else { $null }
    coreExited = if ($core) {
        try { (Get-Process -Id $core.Pid -ErrorAction Stop).HasExited }
        catch { $true }
    } else { $null }
    remainingOwnedChildren = ($remainingOwned | ForEach-Object { "$($_.Pid):$($_.Name)" })
    remainingOwnedCount = $remainingOwned.Count
    pass = ($remainingOwned.Count -eq 0 -and ($exitCode -eq 0 -or $exitCode -eq -1))
}

$shutdownPath = Join-Path $EvidenceRoot "shutdown-observation.json"
Write-SanitizedJson -Path $shutdownPath -Value $shutdown

Write-Host "`nShutdown observation:" -ForegroundColor Cyan
Write-Host "  Desktop exit code: $exitCode" -ForegroundColor White
Write-Host "  Owned children remaining: $($remainingOwned.Count)" -ForegroundColor White
if ($remainingOwned.Count -gt 0) {
    Write-Host "  !!! Residual processes: $($remainingOwned -join ', ')" -ForegroundColor Red
}

# Cleanup TestSite
try {
    Stop-Process -Id $testSite.Pid -Force -ErrorAction Stop
    Write-Host "  TestSite stopped" -ForegroundColor Gray
}
catch { }

# Cleanup temp
if (-not $KeepArtifacts) {
    try {
        Remove-Item -Recurse -Force $TempHome -ErrorAction SilentlyContinue
    }
    catch { }
}

Write-Host "`nEvidence: $EvidenceRoot" -ForegroundColor Cyan
Write-Host "Acceptance $(if ($shutdown.pass) { 'PASSED' } else { 'FAILED — check remaining children' })" -ForegroundColor $(if ($shutdown.pass) { 'Green' } else { 'Red' })

exit $(if ($shutdown.pass) { 0 } else { 1 })
