$ErrorActionPreference='Stop'
$base='http://localhost:5000'
$login=Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"Admin@123456","type":"account"}'
$h=@{Authorization="Bearer $($login.token)"}

$tmpls = Invoke-RestMethod -Uri "$base/api/global-agent-templates" -Headers $h
Write-Host "=== Global Agent Templates ==="
$tmpls | ForEach-Object { Write-Host "id=$($_.templateId) name=$($_.name) provider=$($_.preferredProviderId) model=$($_.preferredModelId) builtin=$($_.isBuiltIn)" }

# pick the template named "布丁" or first builtin
$pudding = $tmpls | Where-Object { $_.name -eq '布丁' -or $_.templateId -like '*pudding*' -or $_.name -like '*布丁*' } | Select-Object -First 1
if (-not $pudding) { $pudding = $tmpls | Where-Object { $_.isBuiltIn } | Select-Object -First 1 }
if (-not $pudding) { $pudding = $tmpls | Select-Object -First 1 }
Write-Host "`n=== Picked: $($pudding.templateId) / $($pudding.name) ==="

# fetch full
$full = Invoke-RestMethod -Uri "$base/api/global-agent-templates/$($pudding.templateId)" -Headers $h
$full | ConvertTo-Json -Depth 6 | Out-File -Encoding utf8 Doc\Temp\agent-template-before.json
Write-Host "Before snapshot saved."

# build update body preserving everything, only set provider/model/memory mode
$body = [ordered]@{
    templateId               = $full.templateId
    name                     = $full.name
    description              = $full.description
    role                     = $full.role
    systemPrompt             = $full.systemPrompt
    userPromptTemplate       = $full.userPromptTemplate
    personaPrompt            = $full.personaPrompt
    toolsDescription         = $full.toolsDescription
    bootstrapTemplate        = $full.bootstrapTemplate
    avatarEmoji              = $full.avatarEmoji
    preferredProviderId      = 'mimo'
    preferredModelId         = 'mimo-v2.5-pro'
    memoryLlmEndpoint        = $full.memoryLlmEndpoint
    memoryLlmApiKey          = $full.memoryLlmApiKey
    memoryLlmModelId         = $full.memoryLlmModelId
    memorySearchMode         = if ($full.memorySearchMode) { $full.memorySearchMode } else { 'deep' }
    reasoningEffort          = $full.reasoningEffort
    maxContextTokens         = $full.maxContextTokens
    maxReplyTokens           = $full.maxReplyTokens
    containerImage           = $full.containerImage
    selectedCapabilityIds    = @($full.selectedCapabilityIds)
    selectedSkillPackageIds  = @($full.selectedSkillPackageIds)
    isEnabled                = $true
    sortOrder                = $full.sortOrder
} | ConvertTo-Json -Depth 6

$updated = Invoke-RestMethod -Uri "$base/api/global-agent-templates/$($pudding.templateId)" -Method Put -Headers $h -ContentType 'application/json' -Body $body
Write-Host "`n=== After update ==="
Write-Host "provider=$($updated.preferredProviderId) model=$($updated.preferredModelId) memorySearchMode=$($updated.memorySearchMode)"
