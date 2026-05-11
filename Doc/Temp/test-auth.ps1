# Benchmark v2: Single session with multiple facts then cross-session recall
param($factCount=20, $waitSec=60)

$base = "http://localhost:5000"
$ErrorActionPreference = "Stop"

function api($method, $uri, $body) {
    $json = if ($body) { $body | ConvertTo-Json -Depth 5 } else { $null }
    $h = @{}
    if ($global:token) { $h["Authorization"] = "Bearer $global:token" }
    Write-Debug "Request: $method $uri"
    if ($body) {
        $resp = Invoke-WebRequest -Uri "$base$uri" -Method $method -Body $json -ContentType "application/json" -Headers $h -UseBasicParsing
    } else {
        $resp = Invoke-WebRequest -Uri "$base$uri" -Method $method -Headers $h -UseBasicParsing
    }
    return $resp.Content | ConvertFrom-Json
}

# Bootstrap + Login
Write-Host "=== Bootstrap ==="
try {
    $r = api POST "/api/login/account" @{username="admin";password="Admin@123456"}
    $global:token = $r.token
    Write-Host "Login OK (Token: $($global:token.Substring(0,10))...)"
} catch {
    Write-Host "Login failed, trying bootstrap..."
    api POST "/api/bootstrap" @{username="admin";password="Admin@123456";email="admin@localhost"}
    Start-Sleep 2
    $r = api POST "/api/login/account" @{username="admin";password="Admin@123456"}
    $global:token = $r.token
    Write-Host "Bootstrap+Login OK"
}

Write-Host "Fetching Workspaces..."
$ws = api GET "/api/workspaces"
$wsId = if ($ws -is [array]) { $ws[0].workspaceId } else { $ws.workspaceId }
Write-Host "WS: $wsId"

Write-Host "Fetching Agents..."
$ags = api GET "/api/workspaces/$wsId/agents"
$agentId = if ($ags -is [array]) { $ags[0].agentId } else { $ags.agentId }
Write-Host "Agent: $agentId"

# Get baseline health
Write-Host "Fetching Health..."
$h0 = api GET "/health/subconscious"
Write-Host "Baseline: $($h0.summary.totalFacts) facts, $($h0.summary.totalJobs) jobs"

# Phase A
Write-Host "Phase A..."
$firstMsg = "Hi"
api POST "/api/workspaces/$wsId/chat/message" @{messageText=$firstMsg;agentId=$agentId;forceNewSession=$true}
Write-Host "Done"
