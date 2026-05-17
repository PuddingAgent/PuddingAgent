# Pudding WebSocket 端到端测试
# 测试：连接 → 发送消息 → 验证回复

$ErrorActionPreference = "Continue"
$testClient = "TestScripts/PuddingWsTest/bin/Release/net10.0/PuddingWsTest.dll"
$wsUrl = "ws://localhost:5000/ws/connect"

Write-Host "=== WebSocket 连接器端到端测试 ===" -ForegroundColor Cyan
Write-Host ""

# 1. 尝试连接 + 发送消息 + 等待回复（超时15秒）
Write-Host "[TEST] 连接 WebSocket + 发送消息 + 等待回复..." -ForegroundColor Yellow

$output = & dotnet $testClient 2>&1 |
    ForEach-Object { $_ } 

# 2. 检查核心日志
Write-Host ""
Write-Host "[CHECK] 容器内 WebSocket 日志:" -ForegroundColor Cyan
docker compose logs --tail 30 pudding-agent 2>&1 | Select-String "\[WebSocket\]|\[ConnectorHost\].*Event" | Select-Object -Last 10

Write-Host ""
Write-Host "[CHECK] 容器状态:" -ForegroundColor Cyan
docker compose ps

Write-Host ""
Write-Host "=== 测试完成 ===" -ForegroundColor Green
