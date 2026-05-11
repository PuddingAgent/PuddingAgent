$ErrorActionPreference = "Stop"

# Login
$loginJson = '{"username":"admin","password":"Admin@123456"}'
$loginResp = Invoke-WebRequest -Uri "http://localhost:5000/api/login/account" -Method POST -Body $loginJson -ContentType "application/json"
$token = ($loginResp.Content | ConvertFrom-Json).token
$headers = @{ Authorization = "Bearer $token" }
Write-Host "LOGIN OK"

# Workspaces
$wsResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces" -Headers $headers
$wsId = $wsResp.workspaceId
if ($wsResp -is [array]) { $wsId = $wsResp[0].workspaceId }
Write-Host "WS: $wsId"

# Agents
$agResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/agents" -Headers $headers
$agentId = $agResp.agentId
if ($agResp -is [array]) { $agentId = $agResp[0].agentId }
Write-Host "AGENT: $agentId"

# Send chat message (short timeout because SSE streams don't close)
$chatJson = "{""messageText"":""My name is Tom, I am 28, and I love ramen."",""agentId"":""$agentId"",""forceNewSession"":true}"
try {
    Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $chatJson -ContentType "application/json" -Headers $headers -TimeoutSec 5
} catch { 
    Write-Host "Chat sent (streaming, timeout expected)"
}

Write-Host "Waiting 30 seconds for subconscious processing..."
Start-Sleep -Seconds 30

# Check DB
$container = docker ps --filter "name=pudding" --format "{{.ID}}" | Select-Object -First 1
if (-not $container) { Write-Host "CONTAINER NOT FOUND"; exit 1 }

$null = docker exec $container apt-get update -qq 2>&1
$null = docker exec $container apt-get install -y -qq sqlite3 2>&1

Write-Host ""
Write-Host "=== SubconsciousJobLogs ==="
docker exec $container sqlite3 -column -header data/pudding_memory.db "SELECT Status, FactsExtracted, FactsMerged, ElapsedMs, substr(ErrorMessage,1,60) as Error FROM SubconsciousJobLogs ORDER BY CreatedAt DESC LIMIT 2;"

Write-Host ""
Write-Host "=== MemoryFacts ==="
docker exec $container sqlite3 -column -header data/pudding_memory.db "SELECT substr(Statement,1,100) as Fact, Confidence FROM MemoryFacts ORDER BY CreatedAt DESC LIMIT 5;"

Write-Host ""
Write-Host "=== MemoryPreferences ==="
docker exec $container sqlite3 -column -header data/pudding_memory.db "SELECT Category, Key, Value FROM MemoryPreferences ORDER BY CreatedAt DESC LIMIT 5;"

# Test recall
$recallJson = "{""messageText"":""What is my name, how old am I, and what food do I love?"",""agentId"":""$agentId"",""forceNewSession"":true}"
Write-Host ""
Write-Host "=== Recall Response ==="
try {
    $recallResp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $recallJson -ContentType "application/json" -Headers $headers -TimeoutSec 60
    $text = $recallResp.Content.Substring(0, [Math]::Min(600, $recallResp.Content.Length))
    Write-Host $text
} catch { 
    Write-Host "Recall: $($_.Exception.Message)" 
}

Write-Host ""
Write-Host "===== COMPLETE ====="
