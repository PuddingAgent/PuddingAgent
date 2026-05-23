# ============================================================
# Pudding Agent — 编译 + Docker 部署脚本
# ============================================================
# 用法：
#   .\build-and-up.ps1                  # 完整构建并启动
#   .\build-and-up.ps1 -Fast            # 快速开发模式：编译后挂载本地产物并重启
#   .\build-and-up.ps1 -Fast -Restart   # 仅重启容器（热加载，不重新编译）
#   .\build-and-up.ps1 -BuildOnly       # 只编译 dotnet，不启动 Docker
#   .\build-and-up.ps1 -Restart         # 仅重建镜像并重启（不重新编译）
#   .\build-and-up.ps1 -NoCache         # 禁用 Docker 构建缓存
#
# 模式说明：
#   普通模式  — dotnet build → docker compose build → docker compose up -d
#   -Fast 模式 — dotnet publish → docker compose -f dev.yml up -d（秒级重启）
#
# 配置文件：
#   data/config/llm.providers.json — LLM 服务商、模型与 profile 配置
#   data/config/system.json        — 系统运行配置
#   data/config/security.json      — 本地安全配置

param(
    [switch]$Fast,
    [switch]$BuildOnly,
    [switch]$Restart,
    [switch]$NoCache
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

# ── 1. 编译 Admin 前端（pnpm，产物 dist/，Docker 直接 COPY）──
if (-not $Restart) {
    Step "编译 Admin 前端 (pnpm)"
    $adminDir = "$Root\Source\PuddingPlatformAdmin"
    Invoke-Native {
        Push-Location $adminDir
        try {
            pnpm install --frozen-lockfile 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-Host "    pnpm install --frozen-lockfile 失败，尝试 pnpm install..." -ForegroundColor Yellow
                pnpm install
            }
            pnpm run build
        } finally {
            Pop-Location
        }
    } "Admin 前端编译失败"
    Ok "Admin 前端编译通过"

    # 清理旧的前端构建产物，避免哈希冲突（Docker COPY dist/ 会提供正确的文件）
    Step "清理旧 wwwroot 产物"
    $wwwrootDir = "$Root\Source\PuddingAgent\wwwroot"
    if (Test-Path $wwwrootDir) {
        Remove-Item -Path "$wwwrootDir\*" -Recurse -Force -ErrorAction SilentlyContinue
    }
    # 生成根 index.html — 重定向到 /admin/（Umi base=/admin/）
    $indexContent = @'
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <meta http-equiv="refresh" content="0;url=/admin/">
    <title>Pudding</title>
</head>
<body>
    <p>Redirecting to <a href="/admin/">Pudding Admin</a>...</p>
</body>
</html>
'@
    Set-Content -Path "$wwwrootDir\index.html" -Value $indexContent -Encoding UTF8

    # 复制 Admin SPA 构建产物到 wwwroot/admin/（用于 dotnet run 本地开发，Docker 通过 Dockerfile COPY）
    $adminDistDir = "$Root\Source\PuddingPlatformAdmin\dist"
    $adminWwwrootDir = "$wwwrootDir\admin"
    if (Test-Path $adminDistDir) {
        if (-not (Test-Path $adminWwwrootDir)) {
            New-Item -ItemType Directory -Path $adminWwwrootDir -Force | Out-Null
        }
        Copy-Item -Path "$adminDistDir\*" -Destination $adminWwwrootDir -Recurse -Force
        Ok "Admin SPA 已复制到 wwwroot/admin/"
    } else {
        Write-Host "    [WARN] Admin SPA 构建产物不存在: $adminDistDir" -ForegroundColor Yellow
        Write-Host "    请先执行 pnpm run build" -ForegroundColor Yellow
    }

    Ok "wwwroot 已清理，index.html + Admin SPA 已就绪"

    if ($Fast) {
        Step "编译 PuddingAgent（发布模式，用于卷挂载）"
        $publishDir = "$Root\Source\PuddingAgent\bin\Release\net10.0\publish"
        if (Test-Path $publishDir) { Remove-Item "$publishDir\*" -Recurse -Force -ErrorAction SilentlyContinue }
        Invoke-Native {
            dotnet publish "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release -o $publishDir --nologo
        } "编译失败"

        # 将前端 dist 合并到 publish/wwwroot/admin（单挂载方式）
        Step "合并前端产物到 publish/wwwroot/admin"
        $publishAdminDir = Join-Path $publishDir "wwwroot\admin"
        if (-not (Test-Path $publishAdminDir)) { New-Item -ItemType Directory -Path $publishAdminDir -Force | Out-Null }
        $adminDistDir = "$Root\Source\PuddingPlatformAdmin\dist"
        if (Test-Path $adminDistDir) {
            Copy-Item -Path "$adminDistDir\*" -Destination $publishAdminDir -Recurse -Force
            Ok "前端产物已合并到 publish/wwwroot/admin"
        } else {
            Write-Host "    [WARN] 前端产物不存在: $adminDistDir" -ForegroundColor Yellow
        }
        Ok "编译通过（发布产物: $publishDir）"
    } else {
        Step "编译 PuddingAgent"
        Invoke-Native {
            dotnet build "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release --nologo
        } "编译失败"
        Ok "编译通过"
    }
}

# ── 2. Docker 构建与启动 ─────────────────────────────────
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
