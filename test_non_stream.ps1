$ErrorActionPreference = "Stop"
$login = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
$token = $login.token
$h = @{ Authorization = "Bearer $token" }

$msg = @{messageText="我叫王五，今年40岁"; agentId="general-assistant-001"; forceNewSession=$true} | ConvertTo-Json
Write-Host "POSTing to /api/workspaces/default/chat/message..."
$resp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message" -Method POST -Body $msg -ContentType "application/json; charset=utf-8" -Headers $h
Write-Host "Response: $($resp.Content)"

Write-Host "Wait 60s..."
Start-Sleep -Seconds 60

$query = @{messageText="我叫什么"; agentId="general-assistant-001"; forceNewSession=$true} | ConvertTo-Json
$resp2 = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message" -Method POST -Body $query -ContentType "application/json; charset=utf-8" -Headers $h
Write-Host "Recall Response: $($resp2.Content)"
