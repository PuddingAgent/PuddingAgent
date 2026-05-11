#!/usr/bin/env pwsh
<#
.SYNOPSIS
    PuddingAgent 快速配置脚本 — 一键创建 LLM Provider、模型、Agent 模板配置
.DESCRIPTION
    从 LLM_CONFIG 环境变量或默认值读取配置，自动完成：
    1. 管理员登录
    2. 创建/更新 LLM Provider (Mimo)
    3. 注册模型 (mimo-v2.5-pro, mimo-v2.5)
    4. 配置全局 Agent 模板的显意识 LLM + 潜意识 LLM
.PARAMETER BaseUrl
    LLM Provider 地址，默认读取 LLM_CONFIG_BASE_URL 或环境变量
.PARAMETER ApiKey
    LLM Provider API Key，默认读取 LLM_CONFIG_API_KEY 或环境变量
.EXAMPLE
    .\setup-llm.ps1
    .\setup-llm.ps1 -BaseUrl "https://token-plan-cn.xiaomimimo.com/v1" -ApiKey "tp-..."
.NOTES
    配置持久化到 platform.db，Docker 重启后保留。
#>

param(
    [string]$BaseUrl,
    [string]$ApiKey,
    [string]$AdminUser = "admin",
    [string]$AdminPass = "Admin@123456",
    [string]$Server    = "http://localhost:5000"
)

# 默认值：优先参数，其次环境变量
if (-not $BaseUrl) { $BaseUrl = if ($env:LLM_CONFIG_BASE_URL) { $env:LLM_CONFIG_BASE_URL } else { "https://token-plan-cn.xiaomimimo.com/v1" } }
if (-not $ApiKey)  { $ApiKey  = if ($env:LLM_CONFIG_API_KEY)  { $env:LLM_CONFIG_API_KEY  } else { "tp-cm2d6b219te46815h95bwm4zyogj0qfvw8r755fk2b111qjy" } }

$ErrorActionPreference = "Stop"
Write-Host "╔══════════════════════════════════════════════════╗"
Write-Host "║  PuddingAgent 快速 LLM 配置                     ║"
Write-Host "╚══════════════════════════════════════════════════╝"

# ── 1. 管理员登录 ──
Write-Host "[1/5] 登录 $Server ..."
$loginJson = "{""username"":""$AdminUser"",""password"":""$AdminPass""}"
$loginResp = Invoke-WebRequest -Uri "$Server/api/login/account" -Method POST -Body $loginJson -ContentType "application/json"
$token = ($loginResp.Content | ConvertFrom-Json).token
$headers = @{ Authorization = "Bearer $token" }
Write-Host "  ✓ 登录成功"

# ── 2. 创建/更新 LLM Provider ──
Write-Host "[2/5] 配置 LLM Provider (mimo) ..."
$providerBody = @{
    providerId   = "mimo"
    name         = "Mimo"
    protocol     = "openai"
    baseUrl      = $BaseUrl
    isEnabled    = $true
    apiKey       = $ApiKey
} | ConvertTo-Json

try {
    $providerResp = Invoke-RestMethod -Uri "$Server/api/llm/providers/mimo" -Headers $headers
    Write-Host "  ✓ Provider 已存在，更新中..."
    $putResult = Invoke-RestMethod -Uri "$Server/api/llm/providers/mimo" -Method PUT -Body $providerBody -ContentType "application/json" -Headers $headers
} catch {
    Write-Host "  → 新建 Provider..."
    $putResult = Invoke-RestMethod -Uri "$Server/api/llm/providers" -Method POST -Body $providerBody -ContentType "application/json" -Headers $headers
}
Write-Host "  ✓ Provider 就绪"

# ── 3. 注册模型 ──
Write-Host "[3/5] 注册模型..."
$models = @(
    @{ modelId = "mimo-v2.5-pro"; name = "Mimo V2.5 Pro"; isDefault = $true;  maxContextTokens = 1000000; maxOutputTokens = 128000 }
    @{ modelId = "mimo-v2.5";     name = "Mimo V2.5";     isDefault = $false; maxContextTokens = 1000000; maxOutputTokens = 128000 }
)

foreach ($m in $models) {
    try {
        $modelResp = Invoke-RestMethod -Uri "$Server/api/llm/providers/mimo/models/$($m.modelId)" -Headers $headers
        Write-Host "  ✓ 模型 $($m.modelId) 已存在"
    } catch {
        $modelJson = $m | ConvertTo-Json
        Invoke-RestMethod -Uri "$Server/api/llm/providers/mimo/models" -Method POST -Body $modelJson -ContentType "application/json" -Headers $headers | Out-Null
        Write-Host "  ✓ 注册模型 $($m.modelId)"
    }
}

# ── 4. 配置全局 Agent 模板 ──
Write-Host "[4/5] 配置全局 Agent 模板..."
try {
    $tpl = Invoke-RestMethod -Uri "$Server/api/global-agent-templates/general-assistant" -Headers $headers
} catch {
    Write-Host "  ⚠ 模板 general-assistant 不存在，跳过"
    $tpl = $null
}

if ($tpl) {
    $tplUpdate = @{
        templateId          = "general-assistant"
        name                = $(if ($tpl.name) { $tpl.name } else { "布丁" })
        role                = $(if ($tpl.role) { $tpl.role } else { "Service" })
        preferredProviderId = "mimo"
        preferredModelId    = "mimo-v2.5-pro"
        memoryLlmEndpoint   = $BaseUrl
        memoryLlmApiKey     = $ApiKey
        memoryLlmModelId    = "mimo-v2.5"
        memorySearchMode    = "deep"
        reasoningEffort     = "medium"
    } | ConvertTo-Json

    Invoke-RestMethod -Uri "$Server/api/global-agent-templates/general-assistant" -Method PUT -Body $tplUpdate -ContentType "application/json" -Headers $headers | Out-Null
    Write-Host "  ✓ 模板已更新"
    Write-Host "    显意识 LLM : mimo / mimo-v2.5-pro"
    Write-Host "    潜意识 LLM : mimo / mimo-v2.5 (deep)"
}

# ── 5. 验证 ──
Write-Host "[5/5] 验证配置..."
$verifyTpl = Invoke-RestMethod -Uri "$Server/api/global-agent-templates/general-assistant" -Headers $headers
Write-Host "  Provider: $($verifyTpl.preferredProviderId)"
Write-Host "  Model   : $($verifyTpl.preferredModelId)"
Write-Host "  MemModel: $($verifyTpl.memoryLlmModelId)"
Write-Host "  MemMode : $($verifyTpl.memorySearchMode)"

Write-Host ""
Write-Host "══════════════════════════════════════════════════"
Write-Host "  配置完成！打开 $Server/admin/chat 开始对话"
Write-Host "══════════════════════════════════════════════════"
