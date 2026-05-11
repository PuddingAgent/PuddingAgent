$ErrorActionPreference='Stop'
$base='http://localhost:5000'
$login=Invoke-RestMethod -Uri "$base/api/login/account" -Method Post -ContentType 'application/json' -Body '{"username":"admin","password":"Admin@123456","type":"account"}'
$token=$login.token

$wsid='default'
$agentId='ad4a6a5f-660a-47a4-8794-0721507093e0'

function Send-StreamMessage {
    param([string]$Text, [string]$SessionId, [bool]$ForceNew)

    Add-Type -AssemblyName System.Net.Http
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(5)
    $client.DefaultRequestHeaders.Add('Authorization', "Bearer $token")
    $client.DefaultRequestHeaders.Add('Accept', 'text/event-stream')

    $bodyObj = @{ messageText = $Text; agentId = $agentId; forceNewSession = $ForceNew }
    if ($SessionId) { $bodyObj.sessionId = $SessionId }
    $body = $bodyObj | ConvertTo-Json
    $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, 'application/json')

    $request = [System.Net.Http.HttpRequestMessage]::new('POST', "$base/api/workspaces/$wsid/chat/message/stream")
    $request.Content = $content

    $resp = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
    Write-Host "HTTP $($resp.StatusCode)" -ForegroundColor Cyan
    if (-not $resp.IsSuccessStatusCode) {
        Write-Host $resp.Content.ReadAsStringAsync().Result
        return $null
    }
    $stream = $resp.Content.ReadAsStreamAsync().Result
    $reader = [System.IO.StreamReader]::new($stream)
    $events = New-Object System.Collections.Generic.List[string]
    $eventName = $null
    $dataBuffer = New-Object System.Text.StringBuilder
    $sessionIdOut = $null
    $deltaTotal = New-Object System.Text.StringBuilder
    $thinkingTotal = New-Object System.Text.StringBuilder
    $sawDone = $false
    $sawUsage = $false

    while (-not $reader.EndOfStream) {
        $line = $reader.ReadLine()
        if ($null -eq $line) { break }
        if ($line -eq '') {
            if ($eventName) {
                $data = $dataBuffer.ToString()
                $events.Add("[$eventName] $($data.Substring(0, [Math]::Min(200, $data.Length)))")
                try {
                    $json = $data | ConvertFrom-Json
                    switch ($eventName) {
                        'metadata' { if ($json.sessionId) { $sessionIdOut = $json.sessionId } }
                        'delta'    { if ($json.delta) { [void]$deltaTotal.Append($json.delta) } }
                        'thinking' { if ($json.delta) { [void]$thinkingTotal.Append($json.delta) } }
                        'done'     { $sawDone = $true; if ($json.reply -and $deltaTotal.Length -eq 0) { [void]$deltaTotal.Append($json.reply) } }
                        'usage'    { $sawUsage = $true; Write-Host "  USAGE: $($json | ConvertTo-Json -Compress)" -ForegroundColor DarkGray }
                        'error'    { Write-Host "  ERROR EVENT: $data" -ForegroundColor Red }
                    }
                } catch { }
            }
            $eventName = $null
            [void]$dataBuffer.Clear()
            continue
        }
        if ($line.StartsWith('event: ')) { $eventName = $line.Substring(7).Trim() }
        elseif ($line.StartsWith('data: ')) { [void]$dataBuffer.Append($line.Substring(6)) }
    }

    Write-Host ""
    Write-Host "EVENTS SUMMARY (first 15):" -ForegroundColor Yellow
    $events | Select-Object -First 15 | ForEach-Object { Write-Host "  $_" }
    Write-Host "Total events: $($events.Count)" -ForegroundColor Yellow
    Write-Host "sawDone=$sawDone sawUsage=$sawUsage thinkingLen=$($thinkingTotal.Length) deltaLen=$($deltaTotal.Length)" -ForegroundColor Yellow
    Write-Host "REPLY:" -ForegroundColor Green
    Write-Host $deltaTotal.ToString()
    return [pscustomobject]@{ SessionId = $sessionIdOut; Reply = $deltaTotal.ToString(); SawDone = $sawDone }
}

Write-Host "`n========== ROUND 1: 我最喜欢吃香蕉，请记住 ==========" -ForegroundColor Magenta
$r1 = Send-StreamMessage -Text '我最喜欢吃香蕉，请记住。' -ForceNew $true
$sid = $r1.SessionId
Write-Host "Session: $sid"

Start-Sleep -Seconds 3

Write-Host "`n========== ROUND 2 (NEW SESSION): 你还记得我喜欢吃什么水果吗？ ==========" -ForegroundColor Magenta
$r2 = Send-StreamMessage -Text '你还记得我喜欢吃什么水果吗？' -ForceNew $true
Write-Host "`n=== Recall check ==="
if ($r2.Reply -match '香蕉') { Write-Host "✅ 成功召回 '香蕉'" -ForegroundColor Green }
else { Write-Host "❌ 未召回 '香蕉'" -ForegroundColor Red }
