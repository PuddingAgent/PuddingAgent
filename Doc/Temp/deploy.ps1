$ErrorActionPreference = "Continue"
$Root = "D:\WangXianQiang\github\hyfree\PuddingCode"

Write-Host "==> 编译前端" -ForegroundColor Cyan
Push-Location "$Root\Source\PuddingPlatformAdmin"
pnpm run build
if ($LASTEXITCODE -ne 0) { Write-Host "前端编译失败" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "前端编译通过" -ForegroundColor Green

Write-Host "`n==> 编译 dotnet" -ForegroundColor Cyan
dotnet build "$Root\Source\PuddingAgent\PuddingAgent.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { Write-Host "dotnet 编译失败" -ForegroundColor Red; exit 1 }
Write-Host "dotnet 编译通过" -ForegroundColor Green

Write-Host "`n==> Docker 构建并启动" -ForegroundColor Cyan
Push-Location $Root
docker compose build pudding-agent
if ($LASTEXITCODE -ne 0) { Write-Host "Docker build 失败" -ForegroundColor Red; Pop-Location; exit 1 }
docker compose up -d
if ($LASTEXITCODE -ne 0) { Write-Host "Docker up 失败" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "`nPudding Agent 已启动！http://localhost:8080" -ForegroundColor Yellow
