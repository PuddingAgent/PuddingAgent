# ============================================================
# Pudding Agent Network — 本地编译 + Docker 打包启动脚本
# ============================================================
# 用法：
#   .\build-and-up.ps1           # 完整构建并启动
#   .\build-and-up.ps1 -SkipFrontend  # 跳过前端构建
#   .\build-and-up.ps1 -SkipBackend   # 跳过后端构建
#   .\build-and-up.ps1 -BuildOnly     # 只编译，不启动 Docker

param(
    [switch]$SkipFrontend,
    [switch]$SkipBackend,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Ok($msg)   { Write-Host "    ✓ $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "    ✗ $msg" -ForegroundColor Red; exit 1 }

# ── 0. 检查 .env 文件（生产环境必要）────────────────────────────
if (-not (Test-Path "$Root\.env")) {
    Write-Host "`n[!] 未找到 .env 文件，将自动从 .env.example 复制" -ForegroundColor Yellow
    Copy-Item "$Root\.env.example" "$Root\.env"
    Write-Host "    请编辑 .env 并至少填写：" -ForegroundColor Yellow
    Write-Host "      LLM_API_KEY  — 你的 LLM 密钥" -ForegroundColor Yellow
    Write-Host "      JWT_KEY      — 生产环境必须替换（openssl rand -base64 48）" -ForegroundColor Yellow
    Write-Host "    就绪内将使用默认开发密码。`n" -ForegroundColor DarkYellow
}

# ── 1. 前端：pnpm build ───────────────────────────────────────
if (-not $SkipFrontend) {
    Step "构建前端 PuddingPlatformAdmin"
    Push-Location "$Root\Source\PuddingPlatformAdmin"
    pnpm run build
    if ($LASTEXITCODE -ne 0) { Fail "前端构建失败" }
    Pop-Location
    Ok "前端产物已生成 → Source/PuddingPlatformAdmin/dist/"
}

# ── 2. 后端：dotnet publish ───────────────────────────────────
if (-not $SkipBackend) {
    $projects = @(
        @{ Proj = "Source\PuddingPlatform\PuddingPlatform.csproj";   Out = "Source\PuddingPlatform\publish";   Dll = "PuddingPlatform.dll"   },
        @{ Proj = "Source\PuddingController\PuddingController.csproj"; Out = "Source\PuddingController\publish"; Dll = "PuddingController.dll" },
        @{ Proj = "Source\PuddingRuntime\PuddingRuntime.csproj";     Out = "Source\PuddingRuntime\publish";   Dll = "PuddingRuntime.dll"   }
    )

    foreach ($p in $projects) {
        $name = Split-Path $p.Proj -Leaf
        Step "发布 $name"
        dotnet publish "$Root\$($p.Proj)" -c Release -o "$Root\$($p.Out)" /p:UseAppHost=false
        if ($LASTEXITCODE -ne 0) { Fail "$name 发布失败" }
        Ok "产物已生成 → $($p.Out)"
    }
}

# ── 3. Docker Compose 构建 + 启动 ────────────────────────────
if (-not $BuildOnly) {
    Step "docker compose up -d --build"
    Push-Location $Root
    docker compose up -d --build
    if ($LASTEXITCODE -ne 0) { Fail "docker compose 启动失败" }
    Pop-Location
    Ok "所有服务已启动"

    Write-Host @"

访问地址：
  前端管理界面   → http://localhost
  RabbitMQ 管理  → http://localhost:15672  (pudding / pudding_dev)

查看日志：
  docker compose logs -f pudding-runtime

停止所有服务：
  docker compose down
"@ -ForegroundColor Yellow
}
