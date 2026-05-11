$ErrorActionPreference = "Stop"
Write-Host "1. Bootstrapping admin..."
try {
    Invoke-RestMethod -Uri "http://localhost:5000/api/bootstrap/admin" -Method POST -Body '{"userId":"admin","email":"admin@localhost","password":"Admin@123456"}' -ContentType "application/json"
} catch {
    Write-Host "Bootstrap might have already been done or failed: $($_.Exception.Message)"
}

Write-Host "2. Logging in..."
$login = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
$token = $login.token
$h = @{ Authorization = "Bearer $token" }

# Check agent ID
Write-Host "Checking agents..."
$agents = Invoke-RestMethod -Uri "http://localhost:5000/api/workspaces/default/agents" -Headers $h
Write-Host "Available Agent IDs: $($agents.agentId -join ', ')"
$agentId = $agents[0].agentId
if (-not $agentId) { $agentId = "general-assistant-001" } # Fallback
Write-Host "Using Agent ID: $agentId"

$facts = "我叫王五", "今年40岁", "喜欢旅游和摄影"
foreach ($f in $facts) {
    Write-Host "Sending fact: $f"
    $body = @{messageText=$f; agentId=$agentId; forceNewSession=$true} | ConvertTo-Json
    try {
        Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $body -ContentType "application/json; charset=utf-8" -Headers $h | Out-Null
    } catch {
        Write-Host "Error sending fact: $($_.Exception.Message)"
    }
}

Write-Host "3. Waiting 90s for consolidation..."
Start-Sleep -Seconds 90

Write-Host "4. Testing recall..."
$query = @{messageText="我叫什么"; agentId=$agentId; forceNewSession=$true} | ConvertTo-Json
$resp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $query -ContentType "application/json; charset=utf-8" -Headers $h

$content = $resp.Content
$doneLine = $content -split "`n" | Select-String "event: done" -Context 0,1
if ($doneLine) {
    $dataLine = $doneLine.Context.PostContext[0] -replace "^data: ",""
    $obj = $dataLine | ConvertFrom-Json
    $reply = [System.Text.RegularExpressions.Regex]::Unescape($obj.reply)
    Write-Host "`n--- RECALL REPLY ---"
    Write-Host $reply
    Write-Host "--------------------"
    
    if ($reply -match "\[来自: .*\]" -or $reply -match "\[Source: .*\]" -or $reply -match "【来自: .*】") {
        Write-Host "Source reference FOUND."
    } else {
        Write-Host "Source reference NOT found in reply."
    }
} else {
    Write-Host "Done event not found in stream output."
    Write-Host "Raw content: $content"
}
