# ============================================================
# Pudding Agent - 本地开发启动脚本
# ============================================================
# 启动后端 dotnet watch（端口 5000）和前端 dev server（端口 8000），
# 通过 nginx 在 http://localhost/ 统一提供。
#
# 用法：
#   .\dev-up.ps1              # 启动
#   .\dev-up.ps1 -Status      # 查看状态
#   .\dev-up.ps1 -Logs        # 跟随日志
#   .\dev-up.ps1 -Down        # 停止
#   .\dev-up.ps1 -Restart     # 重启
#
# 访问：
#   后端 API  → http://localhost:5000
#   前端 Dev  → http://localhost:8000
#   nginx 代理 → http://localhost/admin/user/login (浏览器应该打开这个地址，nginx 会代理到前端和dev server)

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
        Fail "缺少命令：$name。请先安装并确保它在 PATH 中。"
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
    # 递归终止子进程，避免前端 dev server 子进程残留
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
        Warn "$name 未记录 PID"
        return
    }
    if (Test-ProcessAlive $pidValue) {
        Stop-ProcessTree $pidValue
        Ok "已停止 $name (PID $pidValue)"
    } else {
        Warn "$name PID $pidValue 已不存在"
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
            Write-Host "[i] 已创建默认配置 data/config/$name" -ForegroundColor Yellow
        }
    }
}

function Start-Backend {
    Step "启动后端 dotnet watch"
    # 通过环境变量控制端口绑定（ASPNETCORE_HTTP_PORTS 与 ASPNETCORE_URLS 互斥，只用后者）
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
    # PlatformApiClient 自环调用时使用正确的 dev 端口
    $env:Pudding__ControllerEndpoint = "http://localhost:5000"
    # RuntimeDispatcher 回退到自身（dev 模式下 Controller 和 Runtime 同进程）
    $env:Pudding__RuntimeFallbackEndpoint = "http://localhost:5000"

    # 通过环境变量 ASPNETCORE_URLS 控制端口，命令行不传 --urls
    # 避免 Start-Process 传参方式导致参数未透传到 dotnet watch 子进程
    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList "watch --project Source\PuddingAgent\PuddingAgent.csproj run" `
        -WorkingDirectory $Root `
        -RedirectStandardOutput $BackendOutLog `
        -RedirectStandardError $BackendErrLog `
        -WindowStyle Hidden `
        -PassThru

    Set-Content -Path $BackendPidFile -Value $process.Id -Encoding ASCII
    Ok "后端已启动 (PID $($process.Id))"
}

# ── 主流程 ──────────────────────────────────────────────

New-Item -ItemType Directory -Path $RunDir -Force | Out-Null

# 停止或重启
if ($Down -or $Restart) {
    Step "停止开发进程"
    Stop-TrackedProcess "Frontend" $FrontendPidFile
    Stop-TrackedProcess "Backend" $BackendPidFile
    if ($Down -and (-not $Restart)) { exit 0 }
}

if ($Status) {
    Show-Status
    exit 0
}

if ($Logs) {
    Step "跟随后端和前端日志（Ctrl+C 退出）"
    Get-Content -Path $BackendOutLog, $BackendErrLog, $FrontendOutLog, $FrontendErrLog -Wait -ErrorAction SilentlyContinue
    exit 0
}

Require-Command "dotnet"

if (-not (Test-PortFree 5000)) { Fail "端口 5000 已被占用，请先停止占用进程或修改开发端口。" }

Prepare-Config

# ── 后端启动 ──────────────────────────────────────────
Start-Backend

# ── 前端启动（独立 dev server，通过 nginx 汇总）──────
# ADR-034: 确保头像资源在输出目录中存在
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
Ok "前端 dev server 已启动 (PID $($process.Id))"

Write-Host @"

开发环境已启动：
  后端 API   → http://localhost:5000
  前端 Dev   → http://localhost:8000
  nginx 代理 → http://localhost/admin/user/login

查看状态：
  .\dev-up.ps1 -Status

查看日志：
  .\dev-up.ps1 -Logs

停止：
  .\dev-up.ps1 -Down
"@ -ForegroundColor Yellow
