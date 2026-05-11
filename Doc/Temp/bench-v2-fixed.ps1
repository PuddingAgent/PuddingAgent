# Benchmark v2: Single session with multiple facts then cross-session recall
param($factCount=20, $waitSec=60)

$base = "http://localhost:5000"
$ErrorActionPreference = "Stop"

function api($method, $uri, $body) {
    $json = if ($body) { $body | ConvertTo-Json -Depth 5 } else { $null }
    $h = @{}
    if ($global:token) { $h["Authorization"] = "Bearer $global:token" }
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
    Write-Host "Login OK"
} catch {
    api POST "/api/bootstrap" @{username="admin";password="Admin@123456";email="admin@localhost"}
    Start-Sleep 2
    $r = api POST "/api/login/account" @{username="admin";password="Admin@123456"}
    $global:token = $r.token
    Write-Host "Bootstrap+Login OK"
}

$ws = api GET "/api/workspaces"
$wsId = if ($ws -is [array]) { $ws[0].workspaceId } else { $ws.workspaceId }
$ags = api GET "/api/workspaces/$wsId/agents"
$agentId = if ($ags -is [array]) { $ags[0].agentId } else { $ags.agentId }
Write-Host "WS=$wsId Agent=$agentId"

# Get baseline health
$h0 = api GET "/health/subconscious"
Write-Host "Baseline: $($h0.summary.totalFacts) facts, $($h0.summary.totalJobs) jobs"

# === Phase A: Fact ingestion (single session, multi-turn) ===
Write-Host "`n=== Phase A: Ingesting $factCount facts in single session ==="
$sw1 = [System.Diagnostics.Stopwatch]::StartNew()

# First message creates session
$firstMsg = "I am going to tell you $factCount facts about me. Please remember them all."
$r1 = api POST "/api/workspaces/$wsId/chat/message" @{messageText=$firstMsg;agentId=$agentId;forceNewSession=$true}
$sessionId = if ($r1.sessionId) { $r1.sessionId } else { "" }
Write-Host "Session: $sessionId"

# Subsequent messages in same session
$fail = 0
for ($i = 1; $i -le $factCount; $i++) {
    try {
        $msg = "Fact #$i`: My benchmark_key_$i is set to benchmark_value_$i."
        api POST "/api/workspaces/$wsId/chat/message" @{messageText=$msg;agentId=$agentId;sessionId=$sessionId} | Out-Null
    } catch { $fail++ }
    if ($i % 5 -eq 0) { Write-Host "  Sent $i/$factCount" }
}
$sw1.Stop()
Write-Host "Send done: $($sw1.ElapsedMilliseconds)ms, $fail failures"

# === Phase B: Wait for subconscious ===
Write-Host "`n=== Phase B: Waiting ${waitSec}s for subconscious ==="
Start-Sleep $waitSec

$h1 = api GET "/health/subconscious"
Write-Host "Post-wait: $($h1.summary.totalFacts) facts, $($h1.summary.totalJobs) jobs"
foreach ($j in $h1.recentJobs) {
    Write-Host "  Job $($j.jobId.Substring(0,8)): $($j.status) extracted=$($j.factsExtracted) merged=$($j.factsMerged) elapsed=$($j.elapsedMs)ms"
}

# === Phase C: Cross-session recall (NEW session) ===
Write-Host "`n=== Phase C: Cross-session recall ==="
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $recall = api POST "/api/workspaces/$wsId/chat/message" @{
        messageText="Based on what you remember about me, list all benchmark_key and benchmark_value pairs you know."
        agentId=$agentId
        forceNewSession=$true
    }
    $reply = if ($recall.reply) { $recall.reply } elseif ($recall.replyText) { $recall.replyText } else { $recall.content }
} catch { $reply = "ERROR: $($_.Exception.Message)" }
$sw2.Stop()

$replyLen = if ($reply) { $reply.Length } else { 0 }
Write-Host "Recall latency: $($sw2.ElapsedMilliseconds)ms, replyLen=$replyLen"
Write-Host "Reply (first 600 chars):"
Write-Host ($reply.Substring(0, [Math]::Min(600, $replyLen)))

# Count matched facts
$matched = 0
for ($i = 1; $i -le $factCount; $i++) {
    if ($reply -match "benchmark_value_$i") { $matched++ }
}
Write-Host "Matched facts: $matched / $factCount"

Write-Host "`n=== DONE ==="

