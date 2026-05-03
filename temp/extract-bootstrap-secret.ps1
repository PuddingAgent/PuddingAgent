# 从 docker-compose.yml 提取 Bootstrap__Secret 的默认值
param(
    [string]$ComposeFile = "docker-compose.yml"
)

$content = Get-Content $ComposeFile -Raw

# 匹配 Bootstrap__Secret: "${BOOTSTRAP_SECRET:-<default>}"
if ($content -match 'Bootstrap__Secret:\s*"\$\{BOOTSTRAP_SECRET:-([^}]*)\}"') {
    $defaultValue = $Matches[1]
    Write-Host "Bootstrap__Secret 默认值: '$defaultValue'" -ForegroundColor Cyan
    if ($defaultValue -eq '') {
        Write-Host "(空字符串 — 即未设置 BOOTSTRAP_SECRET 时不会传入任何引导密钥)" -ForegroundColor Yellow
    }
    
    $envValue = $env:BOOTSTRAP_SECRET
    if ($envValue) {
        Write-Host "当前环境变量 BOOTSTRAP_SECRET: '$envValue'" -ForegroundColor Green
    } else {
        Write-Host "当前环境变量 BOOTSTRAP_SECRET: 未设置" -ForegroundColor DarkGray
    }
} else {
    Write-Host "未找到 Bootstrap__Secret 配置项" -ForegroundColor Red
}
