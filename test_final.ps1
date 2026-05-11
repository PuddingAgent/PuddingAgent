$ErrorActionPreference = "Stop"
$login = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
$token = $login.token
$h = @{ Authorization = "Bearer $token" }

$facts = "我叫王五", "今年40岁", "喜欢旅游和摄影"
foreach ($f in $facts) {
    Write-Host "Sending fact: $f"
    $body = @{messageText=$f; agentId="general-assistant-001"; forceNewSession=$true} | ConvertTo-Json
    Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $body -ContentType "application/json; charset=utf-8" -Headers $h | Out-Null
}

Write-Host "Waiting 90s for consolidation..."
Start-Sleep -Seconds 90

Write-Host "Testing recall..."
$query = @{messageText="我叫什么"; agentId="general-assistant-001"; forceNewSession=$true} | ConvertTo-Json
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
    Write-Host "Done event not found."
}
