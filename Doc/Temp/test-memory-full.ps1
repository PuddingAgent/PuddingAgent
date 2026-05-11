$ErrorActionPreference = "Stop"

# Login
$loginResp = Invoke-WebRequest -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
$token = ($loginResp.Content | ConvertFrom-Json).token
$headers = @{ Authorization = "Bearer $token" }
Write-Host "LOGIN OK"

# Workspaces
$wsResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces" -Headers $headers
$wsData = $wsResp
$wsId = $wsData.workspaceId
if ($wsData -is [array]) { $wsId = $wsData[0].workspaceId }
Write-Host "WS: $wsId"

# Agents
$agResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/agents" -Headers $headers
$agData = $agResp
$agentId = $agData.agentId
if ($agData -is [array]) { $agentId = $agData[0].agentId }
Write-Host "AGENT: $agentId"

# Send chat message
$chatJson = "{""messageText"":""My name is Tom, I am 28, and I love ramen."",""agentId"":""$agentId"",""forceNewSession"":true}"
try { 
    $resp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $chatJson -ContentType "application/json" -Headers $headers
    Write-Host "Chat response received"
} catch { 
    Write-Host "Chat sent/stream error: $($_.Exception.Message)" 
}

Write-Host "Waiting 30 seconds for subconscious processing..."
Start-Sleep -Seconds 30

# Check DB
$c = docker ps --filter "name=pudding" --format "{{.ID}}" | Select-Object -First 1
if ($c) {
    docker exec $c apt-get install -y -qq sqlite3 2>&1 | Out-Null

    Write-Host ""
    Write-Host "=== SubconsciousJobLogs ==="
    docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT Status, FactsExtracted, FactsMerged, ElapsedMs, substr(ErrorMessage,1,50) as Error FROM SubconsciousJobLogs ORDER BY CreatedAt DESC LIMIT 2;"

    Write-Host ""
    Write-Host "=== MemoryFacts ==="
    docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT substr(Statement,1,100) as Fact, Confidence FROM MemoryFacts ORDER BY CreatedAt DESC LIMIT 5;"

    Write-Host ""
    Write-Host "=== MemoryPreferences ==="
    docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT Category, Key, Value FROM MemoryPreferences ORDER BY CreatedAt DESC LIMIT 5;"
}

# Test recall
$recallJson = "{""messageText"":""What is my name, how old am I, and what food do I love?"",""agentId"":""$agentId"",""forceNewSession"":true}"
Write-Host ""
Write-Host "=== Recall Response ==="
try {
    $recallResp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $recallJson -ContentType "application/json" -Headers $headers
    $text = $recallResp.Content.Substring(0, [Math]::Min(500, $recallResp.Content.Length))
    Write-Host $text
} catch { 
    Write-Host "Recall error: $($_.Exception.Message)" 
}

Write-Host ""
Write-Host "DONE"
