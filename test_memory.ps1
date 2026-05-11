try {
    $loginResp = Invoke-RestMethod -Uri "http://localhost:5000/api/login/account" -Method POST -Body '{"username":"admin","password":"Admin@123456"}' -ContentType "application/json"
    $token = $loginResp.token
    $headers = @{Authorization="Bearer $token"}
    Write-Host "Login OK"

    Write-Host "Sending fact..."
    $factBody = '{"messageText":"我喜欢的水果是苹果","agentId":"general-assistant-001","forceNewSession":true}'
    $null = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $factBody -ContentType "application/json" -Headers $headers

    Write-Host "Waiting 62s..."
    Start-Sleep -Seconds 62

    Write-Host "Checking DB..."
    $c = docker ps --filter "name=pudding-agent" --format "{{.ID}}" | Select-Object -First 1
    if ($c) {
        docker exec $c sqlite3 data/pudding_memory.db "SELECT Statement FROM MemoryFacts;"
    } else {
        Write-Host "Container not found"
    }

    Write-Host "Testing recall..."
    $recallBody = '{"messageText":"我喜欢的水果是什么？","agentId":"general-assistant-001","forceNewSession":true}'
    $recallResp = Invoke-WebRequest -Uri "http://localhost:5000/api/workspaces/default/chat/message/stream" -Method POST -Body $recallBody -ContentType "application/json" -Headers $headers
    $recallResp.Content
} catch {
    Write-Error $_
}
