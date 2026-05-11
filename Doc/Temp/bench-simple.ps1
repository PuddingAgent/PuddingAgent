# Simplified Memory Benchmark Script
param($maxFacts=30, $waitSec=90)

$base = "http://localhost:5000"
$ErrorActionPreference = "Stop"

function api($method, $uri, $body, $headers) {
    $json = if ($body) { $body | ConvertTo-Json -Depth 5 } else { $null }
    $h = @{ Authorization = "Bearer $($headers.Token)" }
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
    $r = api POST "/api/login/account" @{username="admin";password="Admin@123456"} @{Token="x"}
    $token = $r.token
    Write-Host "Login OK"
} catch {
    Write-Host "Bootstrapping..."
    api POST "/api/bootstrap" @{username="admin";password="Admin@123456";email="admin@localhost"} @{Token="x"}
    Start-Sleep 2
    $r = api POST "/api/login/account" @{username="admin";password="Admin@123456"} @{Token="x"}
    $token = $r.token
    Write-Host "Bootstrap+Login OK"
}
$hdr = @{ Token = $token }

# Get workspace + agent
$ws = api GET "/api/workspaces" $null $hdr
$wsId = if ($ws -is [array]) { $ws[0].workspaceId } else { $ws.workspaceId }
$ags = api GET "/api/workspaces/$wsId/agents" $null $hdr
$agentId = if ($ags -is [array]) { $ags[0].agentId } else { $ags.agentId }
Write-Host "WS=$wsId Agent=$agentId"

# Send facts
Write-Host "=== Sending $maxFacts facts ==="
$sw1 = [System.Diagnostics.Stopwatch]::StartNew()
$fail = 0
for ($i = 1; $i -le $maxFacts; $i++) {
    try {
        $msg = "Fact #$i`: My item_$i value is benchmark_data_$i."
        api POST "/api/workspaces/$wsId/chat/message" @{messageText=$msg;agentId=$agentId;forceNewSession=$true} $hdr | Out-Null
    } catch { $fail++ }
    if ($i % 10 -eq 0) { Write-Host "  Sent $i/$maxFacts (${fail}fail)" }
}
$sw1.Stop()
Write-Host "Send done: $($sw1.ElapsedMilliseconds)ms, $fail failures"

# Wait for subconscious
Write-Host "=== Waiting ${waitSec}s for subconscious ==="
Start-Sleep $waitSec

# Check health
Write-Host "=== Subconscious Health ==="
$health = api GET "/health/subconscious" $null $hdr
Write-Host "Jobs: $($health.summary.successCount) OK / $($health.summary.failCount) FAIL"
Write-Host "Facts: $($health.summary.totalFacts)"
Write-Host "Prefs: $($health.summary.totalPreferences)"
foreach ($j in $health.recentJobs) {
    Write-Host "  Job $($j.jobId.Substring(0,8)): $($j.status) extracted=$($j.factsExtracted) elapsed=$($j.elapsedMs)ms model=$($j.llmModelId)"
}

# Recall test
Write-Host "=== Recall Test ==="
$sw2 = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $recall = api POST "/api/workspaces/$wsId/chat/message" @{messageText="What do you remember about my benchmark data? List what you know.";agentId=$agentId;forceNewSession=$true} $hdr
    $reply = if ($recall.reply) { $recall.reply } elseif ($recall.replyText) { $recall.replyText } else { $recall.content }
} catch { $reply = "ERROR: $($_.Exception.Message)" }
$sw2.Stop()
Write-Host "Recall latency: $($sw2.ElapsedMilliseconds)ms"
Write-Host "Reply (first 500 chars):"
Write-Host ($reply.Substring(0, [Math]::Min(500, $reply.Length)))
Write-Host "---"
Write-Host "DONE"
