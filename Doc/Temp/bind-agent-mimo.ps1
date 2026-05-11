$ErrorActionPreference='Stop'
$base='http://localhost:5000'
$login=Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"Admin@123456","type":"account"}'
$h=@{Authorization="Bearer $($login.token)"}

$wsid='default'
$agentId='ad4a6a5f-660a-47a4-8794-0721507093e0'

# fetch current
$cur = Invoke-RestMethod -Uri "$base/api/workspaces/$wsid/agents/$agentId" -Headers $h
$body = [ordered]@{
    name                 = $cur.name
    description          = $cur.description
    displayName          = $cur.displayName
    avatarUrl            = $cur.avatarUrl
    sourceTemplateId     = $cur.sourceTemplateId
    systemPromptOverride = $cur.systemPromptOverride
    preferredProviderId  = 'mimo'
    preferredModelId     = 'mimo-v2.5-pro'
    isEnabled            = $true
} | ConvertTo-Json
$updated = Invoke-RestMethod -Uri "$base/api/workspaces/$wsid/agents/$agentId" -Method Put -Headers $h -ContentType 'application/json' -Body $body
Write-Host "Agent updated: provider=$($updated.preferredProviderId) model=$($updated.preferredModelId)"
