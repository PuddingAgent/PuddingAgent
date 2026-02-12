param([int]$Port = 8080, [int]$TimeoutSec = 60)

$BaseUrl = "http://localhost:$Port"
$ErrorActionPreference = "Stop"

Write-Host "==> Pudding Docker Smoke Test" -ForegroundColor Cyan

# 1. Health check
Write-Host "[1/4] Health check..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    try {
        $null = Invoke-WebRequest "$BaseUrl/health" -TimeoutSec 3 -UseBasicParsing
        break
    } catch {
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { throw "Health check timeout after ${TimeoutSec}s" }
        Start-Sleep -Seconds 3
    }
}
Write-Host "  PASS: app healthy" -ForegroundColor Green

# 2. Fake LLM check
Write-Host "[2/4] Fake LLM..."
$body = '{"model":"fake-gpt","messages":[{"role":"user","content":"hello"}]}'
$response = Invoke-RestMethod -Uri "$BaseUrl/__fake_llm/v1/chat/completions" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 15
if (-not $response.choices[0].message.content) { throw "No content" }
Write-Host "  PASS: fake llm" -ForegroundColor Green

# 3. Diagnostics API
Write-Host "[3/4] Diagnostics API..."
$null = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/runtime/components" -TimeoutSec 10
Write-Host "  PASS: diagnostics online" -ForegroundColor Green

# 4. Static assets
Write-Host "[4/4] Admin UI..."
$html = Invoke-WebRequest -Uri "$BaseUrl/admin/" -TimeoutSec 10 -UseBasicParsing
if ($html.Content -notmatch "Pudding") { throw "Admin UI not found" }
Write-Host "  PASS: admin ui" -ForegroundColor Green

Write-Host "`nAll Docker smoke checks PASSED" -ForegroundColor Green
