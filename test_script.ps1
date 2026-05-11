# Rebuild
docker compose down
docker compose up -d --build
Start-Sleep -Seconds 35

# Login and test
$loginBody = @{ username='admin'; password='Admin@123456' } | ConvertTo-Json
Invoke-RestMethod -Uri 'http://localhost:5000/api/login/account' -Method POST -Body $loginBody -ContentType 'application/json' -SessionVariable s
$ws = Invoke-RestMethod -Uri 'http://localhost:5000/api/workspaces' -WebSession $s
$wsId = if ($ws -is [array]) { $ws[0].workspaceId } else { $ws.workspaceId }
$ag = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/agents" -WebSession $s
$agentId = if ($ag -is [array]) { $ag[0].agentId } else { $ag.agentId }

# Store facts
$b1 = @{ messageText = "My name is Bob, I am 25, and I love pizza."; agentId = $agentId; forceNewSession = $true } | ConvertTo-Json
try { Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $b1 -ContentType 'application/json' -WebSession $s 2>&1 | Out-Null } catch {}
Write-Host "Message sent. Waiting for subconscious processing..."
Start-Sleep -Seconds 20

# Check DB
$c = docker ps --filter "name=pudding" --format "{{.ID}}" | Select-Object -First 1
docker exec $c apt-get update -qq
docker exec $c apt-get install -y -qq sqlite3 2>&1 | Out-Null

Write-Host "`n=== JobLog ==="
docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT Status, FactsExtracted, FactsMerged, ElapsedMs FROM SubconsciousJobLogs ORDER BY CreatedAt DESC LIMIT 1;"

Write-Host "`n=== MemoryFacts ==="
docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT substr(Statement,1,80) as Fact, Confidence FROM MemoryFacts ORDER BY CreatedAt DESC LIMIT 5;"

Write-Host "`n=== MemoryPreferences ==="
docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT Category, Key, Value FROM MemoryPreferences ORDER BY CreatedAt DESC LIMIT 5;"

# Test recall
$b2 = @{ messageText = "What is my name, how old am I, and what food do I love?"; agentId = $agentId; forceNewSession = $true } | ConvertTo-Json
Write-Host "`n=== Recall Test ==="
try {
    $r2 = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message/stream" -Method POST -Body $b2 -ContentType 'application/json' -WebSession $s
    Write-Host $r2.Substring(0, [Math]::Min(500, $r2.Length))
} catch { Write-Host "Error: $_" }
