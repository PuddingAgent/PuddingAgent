$ErrorActionPreference = "Stop"

# Login (bootstrap needed for clean data)
$loginBody = @{ username = "admin"; password = "Admin@123456" } | ConvertTo-Json
try {
    $loginResp = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body $loginBody -ContentType "application/json"
    $token = $loginResp.token
    Write-Host "LOGIN OK"
} catch {
    # Bootstrap
    Write-Host "Need bootstrap..."
    $bsBody = @{ username = "admin"; password = "Admin@123456"; email = "admin@localhost" } | ConvertTo-Json
    Invoke-RestMethod -Uri "http://localhost:5000/api/bootstrap" -Method POST -Body $bsBody -ContentType "application/json" | Out-Null
    Start-Sleep -Seconds 2
    $loginResp = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body $loginBody -ContentType "application/json"
    $token = $loginResp.token
    Write-Host "BOOTSTRAP+LOGIN OK"
}

$headers = @{ Authorization = "Bearer $token" }

# Get workspace
$wsResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces" -Headers $headers
$wsId = if ($wsResp -is [array]) { $wsResp[0].workspaceId } else { $wsResp.workspaceId }
Write-Host "WS: $wsId"

# Get agents
$agResp = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/$wsId/agents" -Headers $headers
$agentId = if ($agResp -is [array]) { $agResp[0].agentId } else { $agResp.agentId }
Write-Host "AGENT: $agentId"

# Send fact message (non-streaming for simplicity)
$chatBody = @{ messageText = "My name is Tom, I am 28, and I love ramen."; agentId = $agentId; forceNewSession = $true } | ConvertTo-Json
try {
    $chatResp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message" -Method POST -Body $chatBody -ContentType "application/json" -Headers $headers
    $chatData = $chatResp.Content | ConvertFrom-Json
    Write-Host ("CHAT: " + ($chatData.replyText ?? $chatData.content ?? "ok").Substring(0, [Math]::Min(80, ($chatData.replyText ?? $chatData.content ?? "ok").Length)))
} catch {
    Write-Host "Chat error: $($_.Exception.Message)"
    Write-Host "Body: $($_.ErrorDetails.Message)"
}

Write-Host "Waiting 30s for subconscious..."
Start-Sleep -Seconds 30

# Check DB
$c = docker ps --filter "name=pudding" --format "{{.ID}}" | Select-Object -First 1
if ($c) {
    Write-Host "`n=== SubconsciousJobLogs ==="
    docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT Status, FactsExtracted, FactsMerged, ElapsedMs, substr(ErrorMessage,1,60) as Error FROM SubconsciousJobLogs ORDER BY CreatedAt DESC LIMIT 3;" 2>&1

    Write-Host "`n=== MemoryFacts ==="
    docker exec $c sqlite3 -column -header data/pudding_memory.db "SELECT substr(Statement,1,80) as Fact, Confidence FROM MemoryFacts ORDER BY CreatedAt DESC LIMIT 5;" 2>&1
}

# Test recall
Write-Host "`n=== Recall Test ==="
$recallBody = @{ messageText = "What is my name, how old am I, and what food do I love?"; agentId = $agentId; forceNewSession = $true } | ConvertTo-Json
try {
    $recallResp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/$wsId/chat/message" -Method POST -Body $recallBody -ContentType "application/json" -Headers $headers
    $recallData = $recallResp.Content | ConvertFrom-Json
    $text = ($recallData.replyText ?? $recallData.content ?? $recallResp.Content)
    Write-Host ("REPLY: " + $text.Substring(0, [Math]::Min(300, $text.Length)))
} catch {
    Write-Host "Recall error: $($_.Exception.Message)"
}

Write-Host "`nDONE"
