$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5000'

# 1. login
$login = Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' `
    -Body (@{ username = 'admin'; password = 'Admin@123456'; type = 'account' } | ConvertTo-Json)
if ($login.status -ne 'ok') { throw "login failed: $($login | ConvertTo-Json -Depth 5)" }
$token = $login.token
$h = @{ Authorization = "Bearer $token" }
Write-Host "[OK] login -> token=$($token.Substring(0,16))..." -ForegroundColor Green

# 2. create provider Mimo (idempotent)
$providerBody = @{
    providerId  = 'mimo'
    name        = 'Mimo'
    protocol    = 'OpenAI'
    baseUrl     = 'https://token-plan-cn.xiaomimimo.com/v1'
    apiKey      = 'tp-cm2d6b219te46815h95bwm4zyogj0qfvw8r755fk2b111qjy'
    description = 'Mimo OpenAI-compatible (mimo-v2.5 family)'
    isEnabled   = $true
} | ConvertTo-Json

try {
    $prov = Invoke-RestMethod -Uri "$base/api/llm/providers" -Method Post -Headers $h `
        -ContentType 'application/json' -Body $providerBody
    Write-Host "[OK] provider created" -ForegroundColor Green
} catch {
    if ($_.Exception.Response.StatusCode.value__ -eq 409) {
        Write-Host "[SKIP] provider already exists, updating..." -ForegroundColor Yellow
        $prov = Invoke-RestMethod -Uri "$base/api/llm/providers/mimo" -Method Put -Headers $h `
            -ContentType 'application/json' -Body $providerBody
    } else { throw }
}

# 3. add models
$models = @(
    @{ modelId='mimo-v2.5-pro'; name='Mimo v2.5 Pro'; description='1M ctx / 128K out / thinking'; maxContextTokens=1000000; maxOutputTokens=131072; inputPricePer1MTokens=0; outputPricePer1MTokens=0; capabilityTags=@('chat','tools','thinking','stream'); isDeprecated=$false; isDefault=$true; sortOrder=10 },
    @{ modelId='mimo-v2.5';     name='Mimo v2.5';     description='1M ctx / 128K out';            maxContextTokens=1000000; maxOutputTokens=131072; inputPricePer1MTokens=0; outputPricePer1MTokens=0; capabilityTags=@('chat','tools','stream');            isDeprecated=$false; isDefault=$false; sortOrder=20 }
)
foreach ($m in $models) {
    $body = $m | ConvertTo-Json
    try {
        Invoke-RestMethod -Uri "$base/api/llm/providers/mimo/models" -Method Post -Headers $h `
            -ContentType 'application/json' -Body $body | Out-Null
        Write-Host "[OK] model $($m.modelId) created" -ForegroundColor Green
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -eq 409) {
            Invoke-RestMethod -Uri "$base/api/llm/providers/mimo/models/$($m.modelId)" -Method Put -Headers $h `
                -ContentType 'application/json' -Body $body | Out-Null
            Write-Host "[SKIP] model $($m.modelId) updated" -ForegroundColor Yellow
        } else { throw }
    }
}

# 4. dump final state
$detail = Invoke-RestMethod -Uri "$base/api/llm/providers/mimo" -Headers $h
$detail | ConvertTo-Json -Depth 6
