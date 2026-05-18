# ============================================================
# Pudding Agent — 编译 + Docker 部署脚本
# ============================================================
# 用法：
#   .\build-and-up.ps1                  # 完整构建并启动
#   .\build-and-up.ps1 -BuildOnly       # 只编译 dotnet，不启动 Docker
#   .\build-and-up.ps1 -Restart         # 仅重建镜像并重启（不重新编译）
#   .\build-and-up.ps1 -NoCache         # 禁用 Docker 构建缓存
#
# 环境变量（可选，也可通过 .env 或 docker-compose.yml 传入）：
#   LLM_API_KEY  — LLM API 密钥（必须）
#   LLM_ENDPOINT — LLM API 端点，默认 https://api.openai.com/v1
#   LLM_MODEL    — 模型名，默认 gpt-4o-mini

param(
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

# ── 0. 检查 .env（可选）───────────────────────────────────
if (-not (Test-Path "$Root\.env")) {
    Write-Host "[i] 未找到 .env 文件，将使用默认值" -ForegroundColor Yellow
    Write-Host "    至少需要设置 LLM_API_KEY 环境变量" -ForegroundColor Yellow
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
    Ok "wwwroot 已清理，index.html 已生成"

    Step "编译 PuddingAgent"
    Invoke-Native {
        dotnet build "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release --nologo
    } "编译失败"
    Ok "编译通过"
}

# ── 2. Docker 构建与启动 ─────────────────────────────────
if (-not $BuildOnly) {
    Push-Location $Root

    try {
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
    } finally {
        Pop-Location
    }

    Write-Host @"

Pudding Agent 已启动！
  浏览器打开 → http://localhost:8080

查看日志：
  docker compose logs -f pudding-agent

停止：
  docker compose down
"@ -ForegroundColor Yellow
} else {
    Write-Host "`n仅编译模式，跳过 Docker 部署。" -ForegroundColor Yellow
    Write-Host "手动运行：dotnet run --project Source/PuddingAgent`n"
}