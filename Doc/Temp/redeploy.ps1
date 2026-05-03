$ErrorActionPreference = "Continue"
$Root = "D:\WangXianQiang\github\hyfree\PuddingCode"
$AdminDir = "$Root\Source\PuddingPlatformAdmin"

Write-Host "==> 构建前端" -ForegroundColor Cyan
Push-Location $AdminDir
pnpm run build
if ($LASTEXITCODE -ne 0) { Write-Host "构建失败" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "构建通过" -ForegroundColor Green

Write-Host "`n==> 重建 Docker 并重启" -ForegroundColor Cyan
Push-Location $Root
docker compose build pudding-agent
if ($LASTEXITCODE -ne 0) { Write-Host "Docker build 失败" -ForegroundColor Red; Pop-Location; exit 1 }
docker compose up -d pudding-agent
if ($LASTEXITCODE -ne 0) { Write-Host "Docker up 失败" -ForegroundColor Red; Pop-Location; exit 1 }
Pop-Location
Write-Host "`n部署完成！http://localhost:8080" -ForegroundColor Yellow
