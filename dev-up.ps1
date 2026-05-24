# ============================================================
# Pudding Agent - local development startup script
# ============================================================
# Starts backend dotnet watch (port 5000) and frontend dev server (port 8000).
# nginx/back-end proxy serves the app from http://localhost/.
#
# Usage:
#   .\dev-up.ps1              # start
#   .\dev-up.ps1 -Status      # show status
#   .\dev-up.ps1 -Logs        # follow logs
#   .\dev-up.ps1 -Down        # stop
#   .\dev-up.ps1 -Restart     # restart
#
# URLs:
#   Backend API   -> http://localhost:5000
#   Frontend Dev  -> http://localhost:8000
#   Proxy entry   -> http://localhost/admin/user/login

param(
    [switch]$Down,
    [switch]$Logs,
    [switch]$Status,
    [switch]$Restart,
    [switch]$NoInstall
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$RunDir = Join-Path $Root "tmp\dev"
$BackendPidFile = Join-Path $RunDir "backend.pid"
$FrontendPidFile = Join-Path $RunDir "frontend.pid"
$BackendOutLog = Join-Path $RunDir "backend.out.log"
$BackendErrLog = Join-Path $RunDir "backend.err.log"
$FrontendOutLog = Join-Path $RunDir "frontend.out.log"
$FrontendErrLog = Join-Path $RunDir "frontend.err.log"

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    V $msg" -ForegroundColor Green }
function Warn($msg) { Write-Host "    ! $msg" -ForegroundColor Yellow }
function Fail($msg) { Write-Host "    X $msg" -ForegroundColor Red; exit 1 }

function Require-Command($name) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) {
        Fail "Missing command: $name. Install it and make sure it is in PATH."
    }
}

function Get-PidFromFile($path) {
    if (-not (Test-Path $path)) { return $null }
    $raw = (Get-Content $path -ErrorAction SilentlyContinue | Select-Object -First 1)
    $pidValue = 0
    if ([int]::TryParse($raw, [ref]$pidValue)) { return $pidValue }
    return $null
}

function Test-ProcessAlive($pidValue) {
    if (-not $pidValue) { return $false }
    return [bool](Get-Process -Id $pidValue -ErrorAction SilentlyContinue)
}

function Stop-ProcessTree($pidValue) {
    # Stop child processes recursively so dev-server subprocesses do not remain.
    $children = Get-CimInstance Win32_Process -Filter "ParentProcessId = $pidValue" -ErrorAction SilentlyContinue
    foreach ($child in $children) {
        Stop-ProcessTree $child.ProcessId
    }
    $process = Get-Process -Id $pidValue -ErrorAction SilentlyContinue
    if ($process) {
        Stop-Process -Id $pidValue -Force
    }
}

function Stop-TrackedProcess($name, $pidFile) {
    $pidValue = Get-PidFromFile $pidFile
    if (-not $pidValue) {
        Warn "$name PID is not recorded"
        return
    }
    if (Test-ProcessAlive $pidValue) {
        Stop-ProcessTree $pidValue
        Ok "Stopped $name (PID $pidValue)"
    } else {
        Warn "$name PID $pidValue is no longer running"
    }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}

function Test-PortFree($port) {
    $listeners = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    return -not $listeners
}

function Show-Status {
    $backendPid = Get-PidFromFile $BackendPidFile
    $frontendPid = Get-PidFromFile $FrontendPidFile
    $backendState = if (Test-ProcessAlive $backendPid) { "running (PID $backendPid)" } else { "stopped" }
    $frontendState = if (Test-ProcessAlive $frontendPid) { "running (PID $frontendPid)" } else { "stopped" }
    Write-Host "Backend : $backendState" -ForegroundColor $(if ($backendPid -and (Test-ProcessAlive $backendPid)) { "Green" } else { "Red" })
    Write-Host "Frontend: $frontendState" -ForegroundColor $(if ($frontendPid -and (Test-ProcessAlive $frontendPid)) { "Green" } else { "Red" })
}

function Prepare-Config {
    $configDir = Join-Path $Root "data\config"
    $defaultConfigDir = Join-Path $Root "Source\PuddingAgent\default-data\config"
    if (-not (Test-Path $configDir)) {
        New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    }
    foreach ($name in @("llm.providers.json", "system.json", "security.json", "connectors.json")) {
        $target = Join-Path $configDir $name
        $source = Join-Path $defaultConfigDir $name
        if ((-not (Test-Path $target)) -and (Test-Path $source)) {
            Copy-Item -Path $source -Destination $target
            Write-Host "[i] Created default config data/config/$name" -ForegroundColor Yellow
        }
    }
}

function Start-Backend {
    Step "Start backend dotnet watch"
    # Use environment variables for port binding.
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:ASPNETCORE_URLS = "http://localhost:5000"
    $env:DOTNET_USE_POLLING_FILE_WATCHER = "1"
    $env:PUDDING_DATA_ROOT = Join-Path $Root "data"

    $env:Jwt__Key         = "Pudding-Platform-JWT-DevKey-MUST-CHANGE-IN-PRODUCTION-32PLUS!"
    $env:Jwt__Issuer      = "pudding-platform"
    $env:Jwt__Audience    = "pudding-admin"
    $env:Jwt__ExpiryHours = "8"
    $env:ConnectionStrings__Default = "Data Source=$(Join-Path $Root 'data\pudding_platform.db')"
    $env:PUDDING_LOG_LEVEL = "Debug"
    # PlatformApiClient self-calls use the dev port.
    $env:Pudding__ControllerEndpoint = "http://localhost:5000"
    # RuntimeDispatcher falls back to the same dev process.
    $env:Pudding__RuntimeFallbackEndpoint = "http://localhost:5000"

    # Control the port through ASPNETCORE_URLS instead of passing --urls.
    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList "watch --project Source\PuddingAgent\PuddingAgent.csproj run" `
        -WorkingDirectory $Root `
        -RedirectStandardOutput $BackendOutLog `
        -RedirectStandardError $BackendErrLog `
        -WindowStyle Hidden `
        -PassThru

    Set-Content -Path $BackendPidFile -Value $process.Id -Encoding ASCII
    Ok "Backend started (PID $($process.Id))"
}

# Main flow

New-Item -ItemType Directory -Path $RunDir -Force | Out-Null

# Stop or restart
if ($Down -or $Restart) {
    Step "Stop development processes"
    Stop-TrackedProcess "Frontend" $FrontendPidFile
    Stop-TrackedProcess "Backend" $BackendPidFile
    if ($Down -and (-not $Restart)) { exit 0 }
}

if ($Status) {
    Show-Status
    exit 0
}

if ($Logs) {
    Step "Follow backend and frontend logs (Ctrl+C to exit)"
    Get-Content -Path $BackendOutLog, $BackendErrLog, $FrontendOutLog, $FrontendErrLog -Wait -ErrorAction SilentlyContinue
    exit 0
}

Require-Command "dotnet"

if (-not (Test-PortFree 5000)) { Fail "Port 5000 is already in use. Stop the occupying process or change the dev port." }

Prepare-Config

# Backend
Start-Backend

# Frontend dev server
# ADR-034: Ensure avatar assets exist in the output directory.
$avatarSrc = Join-Path $Root "Source\PuddingPlatform\wwwroot\assets\agent-avatars"
$avatarDst = Join-Path $Root "Source\PuddingAgent\bin\Debug\net10.0\wwwroot\assets\agent-avatars"
if (Test-Path $avatarSrc) {
    New-Item -ItemType Directory -Path $avatarDst -Force | Out-Null
    Copy-Item "$avatarSrc\*" $avatarDst -Recurse -Force
}

$adminDir = Join-Path $Root "Source\PuddingPlatformAdmin"
$launchCmd = "cd '$adminDir'; `$env:REACT_APP_ENV='dev'; `$env:MOCK='none'; `$env:UMI_ENV='dev'; pnpm run start:dev -- --host 127.0.0.1 --port 8000"
$process = Start-Process `
    -FilePath "powershell" `
    -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $launchCmd) `
    -WorkingDirectory $adminDir `
    -RedirectStandardOutput $FrontendOutLog `
    -RedirectStandardError $FrontendErrLog `
    -WindowStyle Hidden `
    -PassThru
Set-Content -Path $FrontendPidFile -Value $process.Id -Encoding ASCII
Ok "Frontend dev server started (PID $($process.Id))"

$summary = @"

Development environment started:
  Backend API  -> http://localhost:5000
  Frontend Dev -> http://localhost:8000
  Proxy entry  -> http://localhost/admin/user/login

Status:
  .\dev-up.ps1 -Status

Logs:
  .\dev-up.ps1 -Logs

Stop:
  .\dev-up.ps1 -Down
"@
Write-Host $summary -ForegroundColor Yellow
