# ============================================================
# Pudding Agent — 编译 + Docker 部署脚本
# ============================================================
# 用法：
#   .\build-and-up.ps1                  # 发布验证：前端 build → docker compose build → up
#   .\build-and-up.ps1 -Fast            # 快速集成：前端 build → dotnet publish → dev.yml up
#   .\build-and-up.ps1 -Fast -Restart   # 仅重启容器（不重新编译）
#   .\build-and-up.ps1 -BuildOnly       # 只编译，不启动 Docker
#   .\build-and-up.ps1 -Restart         # 仅重建镜像并重启（不重新编译）
#   .\build-and-up.ps1 -NoCache         # 禁用 Docker 构建缓存
#   .\build-and-up.ps1 -Validate        # 显式执行宿主机 dotnet build（默认由 Docker 内 publish）
#
# -Fast 跳过参数：
#   .\build-and-up.ps1 -Fast -SkipFrontend   # 跳过前端 build，使用现有 dist
#   .\build-and-up.ps1 -Fast -SkipBackend    # 跳过 dotnet publish，只重启容器
#   .\build-and-up.ps1 -Fast -NoInstall      # 跳过 pnpm install，依赖已安装
#
# 模式说明：
#   发布验证模式 — 前端 build → docker compose build → docker compose up -d
#   -Fast 模式   — 前端 build → dotnet publish → docker compose -f dev.yml up -d
#   dev 开发模式 — .\dev-up.ps1（源码挂载 + dotnet watch + 前端 HMR）
#
# 配置文件：
#   data/config/llm.providers.json — LLM 服务商、模型与 profile 配置
#   data/config/system.json        — 系统运行配置
#   data/config/security.json      — 本地安全配置

param(
    [switch]$Fast,
    [switch]$BuildOnly,
    [switch]$Restart,
    [switch]$NoCache,
    [switch]$SkipFrontend,
    [switch]$SkipBackend,
    [switch]$NoInstall,
    [switch]$Validate
)

$ErrorActionPreference = "Continue"
$Root = $PSScriptRoot

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    V $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "    X $msg" -ForegroundColor Red; exit 1 }

function Invoke-Native {
    param([scriptblock]$Cmd, [string]$ErrorMsg)
    & $Cmd
    if ($LASTEXITCODE -ne 0) { Fail $ErrorMsg }
}

# ── 0. 检查 JSON 配置源 ───────────────────────────────────
$configDir = "$Root\data\config"
$defaultConfigDir = "$Root\Source\PuddingAgent\default-data\config"
if (-not (Test-Path "$configDir")) {
    New-Item -ItemType Directory -Path "$configDir" -Force | Out-Null
}

foreach ($name in @("llm.providers.json", "system.json", "security.json", "connectors.json")) {
    $target = Join-Path $configDir $name
    $source = Join-Path $defaultConfigDir $name
    if ((-not (Test-Path $target)) -and (Test-Path $source)) {
        Copy-Item -Path $source -Destination $target
        Write-Host "[i] 已创建默认配置 data/config/$name" -ForegroundColor Yellow
    }
}

if (-not (Test-Path "$configDir\llm.providers.json")) {
    Fail "缺少 data/config/llm.providers.json，无法解析 LLM 服务商和模型配置"
}

# ── 1. 编译 Admin 前端（pnpm，产物 dist/）─────────────
if ((-not $Restart) -and (-not $SkipFrontend)) {
    Step "编译 Admin 前端 (pnpm)"
    $adminDir = "$Root\Source\PuddingPlatformAdmin"
    Invoke-Native {
        Push-Location $adminDir
        try {
            if (-not $NoInstall) {
                pnpm install --frozen-lockfile 2>$null
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "    pnpm install --frozen-lockfile 失败，尝试 pnpm install..." -ForegroundColor Yellow
                    pnpm install
                }
            } else {
                Write-Host "    [--NoInstall] 跳过 pnpm install" -ForegroundColor Yellow
            }
            pnpm run build
        } finally {
            Pop-Location
        }
    } "Admin 前端编译失败"
    Ok "Admin 前端编译通过"
} elseif (-not $Restart) {
    Write-Host "    [--SkipFrontend] 跳过前端构建，使用现有 dist" -ForegroundColor Yellow
}

# ── 1.5 复制前端产物到输出目录 wwwroot/ ─────────────
# ADR-037: 脚本显式复制到输出目录（Program.cs 中 UseWebRoot 指向 AppContext.BaseDirectory/wwwroot）
$adminDist = "$Root\Source\PuddingPlatformAdmin\dist"

function Copy-DistToWwwroot($targetWwwroot) {
    if (Test-Path $adminDist) {
        Step "复制前端产物到 $targetWwwroot"
        if (Test-Path $targetWwwroot) { Remove-Item "$targetWwwroot\*" -Recurse -Force -ErrorAction SilentlyContinue }
        New-Item -ItemType Directory -Path $targetWwwroot -Force | Out-Null
        Copy-Item "$adminDist\*" $targetWwwroot -Recurse -Force
        Ok "前端产物已复制到 $targetWwwroot"
    } else {
        Write-Host "    [i] 未找到前端产物 dist/，跳过复制" -ForegroundColor Yellow
    }
}

# ── 2. 编译后端 ────────────────────────────────────────
if (-not $Restart) {
    if ($Fast -and (-not $SkipBackend)) {
        Step "编译 PuddingAgent（Fast：发布模式，用于卷挂载）"
        $publishDir = "$Root\Source\PuddingAgent\bin\Release\net10.0\publish"
        if (Test-Path $publishDir) { Remove-Item "$publishDir\*" -Recurse -Force -ErrorAction SilentlyContinue }
        Invoke-Native {
            dotnet publish "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release -o $publishDir --nologo
        } "编译失败"
        # publish 后复制前端到输出目录 wwwroot
        Copy-DistToWwwroot "$publishDir\wwwroot"
        Ok "编译通过（发布产物: $publishDir）"
    } elseif ($Fast -and $SkipBackend) {
        Write-Host "    [--SkipBackend] 跳过 dotnet publish" -ForegroundColor Yellow
    } elseif ($Validate) {
        # 显式传入 -Validate 时才在宿主机执行 dotnet build
        # 默认普通模式依靠 Docker 内 dotnet publish，不重复编译
        Step "编译 PuddingAgent（-Validate 显式验证）"
        Invoke-Native {
            dotnet build "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release --nologo
        } "编译失败"
        Ok "编译通过"
    } else {
        Write-Host "    [i] 宿主机跳过 dotnet build，交由 Docker 内 dotnet publish" -ForegroundColor Yellow
    }
}

# ── 3. Docker 构建与启动 ─────────────────────────────────
if (-not $BuildOnly) {
    Push-Location $Root

    try {
        if ($Fast) {
            # ── 快速开发模式：跳过镜像构建，直接挂载本地产物 ──
            $composeFile = "docker-compose.dev.yml"
            Step "快速启动（$composeFile，跳过镜像构建）"
            docker compose -f $composeFile up -d
            if ($LASTEXITCODE -ne 0) { Fail "服务启动失败" }
            Ok "服务已启动（挂载本地产物）"
        } else {
            $buildArgs = @('build')
            if ($NoCache) { $buildArgs += '--no-cache' }
            $buildArgs += @('pudding-agent')

            Step "构建 Docker 镜像"
            docker compose @buildArgs
            if ($LASTEXITCODE -ne 0) { Fail "镜像构建失败" }
            Ok "镜像构建完成"

            Step "启动服务"
            docker compose up -d
            if ($LASTEXITCODE -ne 0) { Fail "服务启动失败" }
            Ok "服务已启动"
        }
    } finally {
        Pop-Location
    }

    $composeArgs = if ($Fast) { "-f docker-compose.dev.yml" } else { "" }
    Write-Host @"

Pudding Agent 已启动！
  浏览器打开 → http://localhost:5000

查看日志：
  docker compose $composeArgs logs -f pudding-agent

停止：
  docker compose $composeArgs down
"@ -ForegroundColor Yellow

    # Run healthcheck if requested
    if ($RunHealthcheck -or $AutoE2E) {
        Write-Host "Running healthcheck..." -ForegroundColor Cyan
        & "$Root\Tests\e2e\healthcheck.ps1" -Port 8080
    }
} else {
    $modeMsg = if ($Fast) { "发布模式" } else { "编译模式" }
    Write-Host "`n仅$modeMsg，跳过 Docker 部署。" -ForegroundColor Yellow
    Write-Host "手动运行：dotnet run --project Source/PuddingAgent`n"
}
