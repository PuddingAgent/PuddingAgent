# ============================================================
# Pudding Agent Network — 本地编译 + Docker 打包启动脚本
# ============================================================
# 用法：
#   .\build-and-up.ps1                  # 完整构建并启动
#   .\build-and-up.ps1 -SkipFrontend    # 跳过前端构建
#   .\build-and-up.ps1 -SkipBackend     # 跳过后端构建
#   .\build-and-up.ps1 -BuildOnly       # 只编译，不启动 Docker
#   .\build-and-up.ps1 -Restart         # 仅重建应用镜像并重启（不重新编译）
#
# 架构说明：
#   后端 Dockerfile 使用宿主机 dotnet publish 产物（publish/ 目录），
#   不在容器内重复编译，构建速度更快且不依赖容器内网络访问 NuGet。
#   前端 Dockerfile 同理，使用宿主机 npm run build 产物（dist/ 目录）。

param(
    [switch]$SkipFrontend,
    [switch]$SkipBackend,
    [switch]$BuildOnly,
    [switch]$Restart   # 仅重建镜像并重启，不重新编译
)

# 使用 Continue 避免 docker/dotnet 的 stderr 被误判为致命错误
$ErrorActionPreference = "Continue"
$Root = $PSScriptRoot

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    ✓ $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "    ✗ $msg" -ForegroundColor Red; exit 1 }

function Invoke-Native {
    param([scriptblock]$Cmd, [string]$ErrorMsg)
    & $Cmd
    if ($LASTEXITCODE -ne 0) { Fail $ErrorMsg }
}

# ── 0. 检查 .env 文件（生产环境必要）────────────────────────────
if (-not (Test-Path "$Root\.env")) {
    Write-Host "`n[!] 未找到 .env 文件，将自动从 .env.example 复制" -ForegroundColor Yellow
    Copy-Item "$Root\.env.example" "$Root\.env"
    Write-Host "    请编辑 .env 并至少填写：" -ForegroundColor Yellow
    Write-Host "      LLM_API_KEY  — 你的 LLM 密钥" -ForegroundColor Yellow
    Write-Host "      JWT_KEY      — 生产环境必须替换（openssl rand -base64 48）" -ForegroundColor Yellow
    Write-Host "    就绪内将使用默认开发密码。`n" -ForegroundColor DarkYellow
}

# ── 1. 前端：npm run build ────────────────────────────────────
if (-not $SkipFrontend -and -not $Restart) {
    Step "构建前端 PuddingPlatformAdmin"
    Push-Location "$Root\Source\PuddingPlatformAdmin"
    try {
        Invoke-Native { npm run build } "前端构建失败"
    } finally {
        Pop-Location
    }
    Ok "前端产物已生成 → Source/PuddingPlatformAdmin/dist/"
}

# ── 2. 后端：dotnet publish（产物将被 Dockerfile COPY 使用）───
if (-not $SkipBackend -and -not $Restart) {
    $projects = @(
        @{ Proj = "Source\PuddingPlatform\PuddingPlatform.csproj";    Out = "Source\PuddingPlatform\publish"   },
        @{ Proj = "Source\PuddingController\PuddingController.csproj"; Out = "Source\PuddingController\publish" },
        @{ Proj = "Source\PuddingRuntime\PuddingRuntime.csproj";       Out = "Source\PuddingRuntime\publish"   }
    )

    foreach ($p in $projects) {
        $name = Split-Path $p.Proj -Leaf
        Step "发布 $name"
        Invoke-Native {
            dotnet publish "$Root\$($p.Proj)" -c Release -o "$Root\$($p.Out)" `
                --nologo /p:UseAppHost=false
        } "$name 发布失败"
        Ok "产物已生成 → $($p.Out)"
    }
}

# ── 3. Docker Compose：构建应用镜像 + 启动全部服务 ────────────
if (-not $BuildOnly) {
    Push-Location $Root
    try {
        # 只重建应用镜像（后端/前端），基础设施服务 postgres/redis/rabbitmq 不受影响
        Step "构建应用镜像"
        $appServices = @('pudding-platform', 'pudding-controller', 'pudding-runtime', 'pudding-platform-admin')
        docker compose build --no-cache=false @appServices
        if ($LASTEXITCODE -ne 0) { Fail "应用镜像构建失败（查看上方 docker 输出了解详情）" }
        Ok "镜像构建完成"

        # 启动全部服务（已运行的基础设施服务会保持不变）
        Step "启动服务"
        docker compose up -d
        if ($LASTEXITCODE -ne 0) { Fail "服务启动失败（查看上方 docker 输出了解详情）" }

        # 重载 nginx 配置，使新容器 IP 立即生效（避免 DNS 缓存导致 502）
        Step "重载 Nginx 配置"
        docker compose exec -T nginx nginx -s reload
        Ok "Nginx 配置已重载"
    } finally {
        Pop-Location
    }

    Ok "所有服务已启动"

    Write-Host @"

访问地址：
  前端管理界面   → http://localhost
  RabbitMQ 管理  → http://localhost:15672  (pudding / pudding_dev)

查看日志：
  docker compose logs -f pudding-runtime
  docker compose logs -f pudding-platform

停止所有服务：
  docker compose down
"@ -ForegroundColor Yellow
}
