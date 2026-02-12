param([int]$Port = 8080, [int]$TimeoutSec = 30)

$BaseUrl = "http://localhost:$Port"
$ErrorActionPreference = "Stop"

Write-Host "==> Pudding Health Check" -ForegroundColor Cyan

# 1. Check app is up
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    try {
        $null = Invoke-WebRequest "$BaseUrl/health" -TimeoutSec 2 -UseBasicParsing
        break
    } catch {
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSec) { throw "Health check timeout" }
        Start-Sleep -Seconds 2
    }
}
Write-Host "  PASS app health" -ForegroundColor Green

# 2. Check Fake LLM
$fakeResponse = Invoke-RestMethod -Uri "$BaseUrl/__fake_llm/v1/chat/completions" -Method Post -Body '{"model":"fake-gpt","messages":[{"role":"user","content":"hi"}]}' -ContentType "application/json" -TimeoutSec 10
if ($fakeResponse.choices[0].message.content) {
    Write-Host "  PASS fake llm" -ForegroundColor Green
} else { throw "Fake LLM no response" }

# 3. Check event diagnostics
$null = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics/events/stats" -TimeoutSec 10
Write-Host "  PASS diagnostics API" -ForegroundColor Green

Write-Host "All checks PASSED" -ForegroundColor Green
