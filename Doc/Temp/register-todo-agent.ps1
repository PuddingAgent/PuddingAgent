# Temp script: register todo-api agent msi
Set-Location e:\github\AgentNetworkPlan\PuddingAgent
$py = "C:\Users\huany\AppData\Local\Programs\Python\Python312\python.exe"

Write-Host "Using Python: $py"
Write-Host "Running setup..."

$result = & $py .github/skills/todo-api/todo_api.py setup --agent --id msi --name msi --role developer --intro "PuddingAgent dev agent" --key "msi-pudding-agent-key-2026" --registration-secret "hMS-Q6lxu5SUXLCELr03apn-pk9UyFGLIl0nx4vG3Ng" 2>&1

Write-Host "=== RESULT ===" 
Write-Host $result
Write-Host "=== EXIT CODE: $LASTEXITCODE ==="

Write-Host "=== ENV VARS ==="
Write-Host "PARTICIPANT_ID: $env:TODO_API_PARTICIPANT_ID"
Write-Host "PARTICIPANT_NAME: $env:TODO_API_PARTICIPANT_NAME"  
Write-Host "PARTICIPANT_KEY: $env:TODO_API_PARTICIPANT_KEY"
