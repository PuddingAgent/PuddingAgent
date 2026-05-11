$ErrorActionPreference = "Stop"
$login = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
$token = $login.token
$h = @{ Authorization = "Bearer $token" }

$msg = @{messageText="我叫王五"; agentId="general-assistant-001"; forceNewSession=$true} | ConvertTo-Json
$resp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $msg -ContentType "application/json; charset=utf-8" -Headers $h
Write-Host "Stream Response Length: $($resp.Content.Length)"
Write-Host "Stream Response Head: $($resp.Content.Substring(0, [Math]::Min(500, $resp.Content.Length)))"
