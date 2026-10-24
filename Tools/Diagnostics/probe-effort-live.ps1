# Live reasoning-effort probe against the configured provider.
#
# WARNING: this makes a REAL provider call and therefore costs a small amount of money.
# Run it deliberately (never from an automatic gate). It reads endpoint + api key from
# the live resource-pool config, prints the baseUrl and whether a key exists, and
# NEVER prints the key itself.
#
# Purpose: answer questions that no unit test can answer, e.g.
#   - does the route accept reasoning.effort = max, or does it 400?
#   - is the parameter actually honoured (positive control: effort=none must yield 0
#     reasoning tokens while effort=high yields > 0)?
#
# Example (ASCII-only output, safe for Windows PowerShell):
#   & .\probe-effort-live.ps1 -Effort none -Prompt 'A bat and a ball cost 1.10 ...' -MaxOutputTokens 512
#   & .\probe-effort-live.ps1 -Effort max  -Prompt 'A bat and a ball cost 1.10 ...' -MaxOutputTokens 512
param(
    [string]$ConfigPath = 'D:\Data\config\llm.providers.json',
    [string]$ProviderId = 'deepseek',
    [string]$ModelId = 'deepseek-flash',
    [string]$Effort = 'max',
    [string]$Prompt = 'Reply with the single word: ok',
    [int]$MaxOutputTokens = 16
)
$ErrorActionPreference = 'Stop'

$json = [IO.File]::ReadAllText($ConfigPath) | ConvertFrom-Json
$provider = $json.providers | Where-Object { $_.providerId -eq $ProviderId } | Select-Object -First 1
if (-not $provider) { Write-Output 'PROVIDER_NOT_FOUND'; exit 2 }
$model = $provider.models | Where-Object { $_.modelId -eq $ModelId } | Select-Object -First 1
if (-not $model) { Write-Output 'MODEL_NOT_FOUND'; exit 2 }

$base = $provider.baseUrl.TrimEnd('/')
Write-Output ("baseUrl=" + $base)
Write-Output ("apiKeyPresent=" + (-not [string]::IsNullOrWhiteSpace($provider.apiKey)))

$body = @{
    model = $model.modelId
    input = @(@{ role = 'user'; content = @(@{ type = 'input_text'; text = $Prompt }) })
    reasoning = @{ effort = $Effort }
    max_output_tokens = $MaxOutputTokens
    stream = $false
} | ConvertTo-Json -Depth 8 -Compress

$headers = @{ Authorization = "Bearer $($provider.apiKey)"; 'Content-Type' = 'application/json' }
$sw = [Diagnostics.Stopwatch]::StartNew()
try {
    $resp = Invoke-WebRequest -Uri "$base/responses" -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 90 -SkipHttpErrorCheck
} catch {
    Write-Output ("effort=" + $Effort + " TRANSPORT_ERROR=" + $_.Exception.Message)
    exit 3
}
$sw.Stop()
Write-Output ("effort=" + $Effort + " status=" + [int]$resp.StatusCode + " elapsedMs=" + $sw.ElapsedMilliseconds)

$text = $resp.Content
if ([int]$resp.StatusCode -ne 200) {
    $snippet = if ($text.Length -gt 300) { $text.Substring(0, 300) } else { $text }
    Write-Output ("errorBody=" + ($snippet -replace '\s+', ' '))
    exit 0
}

try {
    $doc = $text | ConvertFrom-Json
    $outTokens = $null; $reasonTokens = $null
    if ($doc.usage) {
        if ($doc.usage.PSObject.Properties.Name -contains 'output_tokens') { $outTokens = $doc.usage.output_tokens }
        if ($doc.usage.PSObject.Properties.Name -contains 'output_tokens_details') {
            $reasonTokens = $doc.usage.output_tokens_details.reasoning_tokens
        }
        if ($null -eq $reasonTokens -and ($doc.usage.PSObject.Properties.Name -contains 'reasoning_tokens')) {
            $reasonTokens = $doc.usage.reasoning_tokens
        }
    }
    Write-Output ("outputTokens=" + $outTokens + " reasoningTokens=" + $reasonTokens)
} catch {
    Write-Output 'BODY_PARSE_FAILED'
}
