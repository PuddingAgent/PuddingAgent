$ErrorActionPreference='Stop'
$base='http://localhost:5000'
$login=Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"Admin@123456","type":"account"}'
$h=@{Authorization="Bearer $($login.token)"}
Invoke-RestMethod -Uri "$base/api/workspaces/default/agents" -Headers $h | ConvertTo-Json -Depth 6
Write-Host "`n=== workspace-service-agent template (workspace-level) ==="
try {
  Invoke-RestMethod -Uri "$base/api/workspaces/default/agent-templates/workspace-service-agent" -Headers $h | ConvertTo-Json -Depth 6
} catch {
  Write-Host "Try alternate route..."
  Invoke-RestMethod -Uri "$base/api/workspaces/default/agent-templates" -Headers $h | ConvertTo-Json -Depth 6
}
