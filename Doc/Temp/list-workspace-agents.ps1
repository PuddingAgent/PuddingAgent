$ErrorActionPreference='Stop'
$base='http://localhost:5000'
$login=Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"Admin@123456","type":"account"}'
$h=@{Authorization="Bearer $($login.token)"}

Write-Host "=== Workspaces ==="
$ws = Invoke-RestMethod -Uri "$base/api/workspaces" -Headers $h
$ws | ForEach-Object { Write-Host "ws=$($_.workspaceId) name=$($_.name)" }

if (-not $ws -or $ws.Count -eq 0) {
    Write-Host "No workspaces; need to create one"
    exit 1
}
$wsid = $ws[0].workspaceId
Write-Host "Using workspace: $wsid"

Write-Host "`n=== Workspace Agents ==="
$agents = Invoke-RestMethod -Uri "$base/api/workspaces/$wsid/agents" -Headers $h
$agents | ForEach-Object { Write-Host "agentId=$($_.agentId) name=$($_.name) tmpl=$($_.sourceTemplateId) provider=$($_.preferredProviderId) model=$($_.preferredModelId) enabled=$($_.isEnabled)" }
