<#
.SYNOPSIS
大规模内存系统端到端基准测试脚本。

.DESCRIPTION
分三阶段执行：
1) 预热与基线
2) 规模递增测试（L1/L2/L3/L4）
3) Markdown/CSV 报告输出

.NOTES
- 兼容 Windows PowerShell 5.1
- 通过 HTTP API 调用，不直接操作数据库
#>

[CmdletBinding()]
param(
    [ValidateSet('small', 'medium', 'large')]
    [string]$Scale = 'medium',

    [switch]$SkipWarmup,

    [string]$OutputDir = 'Doc/Temp',

    [string]$BaseUrl = 'http://localhost:5000'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "[benchmark-memory] $Message" -ForegroundColor Cyan
}

function Get-RepoRoot {
    $scriptPath = $PSCommandPath
    if (-not $scriptPath) {
        $scriptPath = $MyInvocation.MyCommand.Path
    }
    $scriptDir = Split-Path -Parent $scriptPath
    return (Resolve-Path (Join-Path $scriptDir '..\..')).Path
}

function Get-NowMs {
    return [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
}

function Resolve-OutputDir {
    param(
        [string]$RepoRoot,
        [string]$Dir
    )

    if ([System.IO.Path]::IsPathRooted($Dir)) {
        return $Dir
    }

    return (Join-Path $RepoRoot $Dir)
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body,
        [int]$TimeoutSec = 120,
        [int]$MaxRetry = 2
    )

    $attempt = 0
    while ($true) {
        $attempt = $attempt + 1
        try {
            if ($null -eq $Body) {
                return Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers -TimeoutSec $TimeoutSec
            }

            $json = $Body | ConvertTo-Json -Depth 8
            return Invoke-RestMethod -Uri $Uri -Method $Method -Headers $Headers -Body $json -ContentType 'application/json; charset=utf-8' -TimeoutSec $TimeoutSec
        }
        catch {
            if ($attempt -ge $MaxRetry) {
                throw "API 调用失败 [$Method $Uri]，重试 $attempt 次后仍失败：$($_.Exception.Message)"
            }
            Write-Host "[benchmark-memory] API 调用失败，准备重试($attempt/$MaxRetry): $Uri" -ForegroundColor Yellow
            Start-Sleep -Seconds 2
        }
    }
}

function Get-PropertyValue {
    param(
        [object]$Obj,
        [string[]]$Candidates,
        [object]$DefaultValue = $null
    )

    if ($null -eq $Obj) {
        return $DefaultValue
    }

    foreach ($name in $Candidates) {
        $prop = $Obj.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($null -ne $prop) {
            return $prop.Value
        }
    }

    return $DefaultValue
}

function Invoke-AdminAndLogin {
    param([string]$BaseUrl)

    Write-Step "检查 bootstrap 状态"
    $status = $null
    try {
        $status = Invoke-Api -Method 'GET' -Uri "$BaseUrl/api/bootstrap/status" -Headers @{} -Body $null -TimeoutSec 30 -MaxRetry 1
    }
    catch {
        Write-Host "[benchmark-memory] bootstrap/status 不可用，按已初始化系统继续：$($_.Exception.Message)" -ForegroundColor Yellow
    }

    if ($null -ne $status) {
        $needsSetup = Get-PropertyValue -Obj $status -Candidates @('needsSetup') -DefaultValue $false
        if ([bool]$needsSetup) {
            Write-Step '系统未初始化，创建默认管理员 admin'
            $bootstrapBody = @{
                userId = 'admin'
                email = 'admin@pudding.local'
                password = 'Admin@123456'
                displayName = 'Administrator'
            }

            try {
                [void](Invoke-Api -Method 'POST' -Uri "$BaseUrl/api/bootstrap/admin" -Headers @{} -Body $bootstrapBody -TimeoutSec 60 -MaxRetry 2)
            }
            catch {
                Write-Host "[benchmark-memory] bootstrap/admin 失败，可能是并发初始化或已完成：$($_.Exception.Message)" -ForegroundColor Yellow
            }
        }
    }

    Write-Step '登录管理员账号'
    $loginBody = @{
        username = 'admin'
        password = 'Admin@123456'
        type = 'account'
    }

    $loginResp = Invoke-Api -Method 'POST' -Uri "$BaseUrl/api/login/account" -Headers @{} -Body $loginBody -TimeoutSec 60 -MaxRetry 2
    $token = Get-PropertyValue -Obj $loginResp -Candidates @('token')
    if ([string]::IsNullOrWhiteSpace([string]$token)) {
        throw '登录失败：响应中缺少 token。请检查 admin 账号密码或系统初始化状态。'
    }

    return $token
}

function Resolve-WorkspaceAndAgent {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers
    )

    Write-Step '获取工作区与 Agent'
    $wsResp = Invoke-Api -Method 'GET' -Uri "$BaseUrl/api/workspaces" -Headers $Headers -Body $null -TimeoutSec 60 -MaxRetry 2

    $workspaces = @()
    if ($wsResp -is [array]) {
        $workspaces = $wsResp
    }
    elseif ($null -ne $wsResp) {
        $workspaces = @($wsResp)
    }

    if ($workspaces.Count -eq 0) {
        throw '未找到任何工作区，请先在系统中创建至少一个 workspace。'
    }

    $workspaceId = Get-PropertyValue -Obj $workspaces[0] -Candidates @('workspaceId')
    if ([string]::IsNullOrWhiteSpace([string]$workspaceId)) {
        throw '工作区响应缺少 workspaceId。'
    }

    $agentResp = Invoke-Api -Method 'GET' -Uri "$BaseUrl/api/workspaces/$workspaceId/agents" -Headers $Headers -Body $null -TimeoutSec 60 -MaxRetry 2

    $agents = @()
    if ($agentResp -is [array]) {
        $agents = $agentResp
    }
    elseif ($null -ne $agentResp) {
        $agents = @($agentResp)
    }

    if ($agents.Count -eq 0) {
        throw "工作区 $workspaceId 下未找到 Agent，请先创建至少一个 workspace agent。"
    }

    $agentId = Get-PropertyValue -Obj $agents[0] -Candidates @('agentId')
    if ([string]::IsNullOrWhiteSpace([string]$agentId)) {
        throw 'Agent 响应缺少 agentId。'
    }

    return @{
        WorkspaceId = $workspaceId
        AgentId = $agentId
    }
}

function Send-ChatMessage {
    param(
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$WorkspaceId,
        [string]$AgentId,
        [string]$MessageText,
        [switch]$ForceNewSession
    )

    $body = @{
        messageText = $MessageText
        agentId = $AgentId
        forceNewSession = [bool]$ForceNewSession
    }

    return Invoke-Api -Method 'POST' -Uri "$BaseUrl/api/workspaces/$WorkspaceId/chat/message" -Headers $Headers -Body $body -TimeoutSec 180 -MaxRetry 2
}

function Get-SubconsciousHealth {
    param([string]$BaseUrl)

    return Invoke-Api -Method 'GET' -Uri "$BaseUrl/health/subconscious" -Headers @{} -Body $null -TimeoutSec 30 -MaxRetry 2
}

function Wait-ForProcessing {
    param(
        [int]$Seconds,
        [string]$Label
    )

    Write-Step "$Label：等待 $Seconds 秒，让潜意识处理完成"
    Start-Sleep -Seconds $Seconds
}

function New-BenchmarkFacts {
    param(
        [int]$Count,
        [int]$StartIndex
    )

    $facts = New-Object System.Collections.ArrayList
    for ($i = 0; $i -lt $Count; $i++) {
        $idx = $StartIndex + $i
        $alias = "benchmark-user-$idx"
        $city = "city-$idx"
        $food = "food-ramen-$idx"

        $message = "Fact[$idx]: My alias is $alias. I live in $city. I love $food."
        $keywords = @($alias, $city, $food)

        [void]$facts.Add([pscustomobject]@{
            Index = $idx
            Message = $message
            Keywords = $keywords
        })
    }

    return $facts
}

function Get-RecallMatchedFactCount {
    param(
        [string]$Reply,
        [System.Collections.ArrayList]$Facts
    )

    if ([string]::IsNullOrWhiteSpace($Reply)) {
        return 0
    }

    $count = 0
    foreach ($fact in $Facts) {
        $keywords = @($fact.Keywords)
        $matched = $true
        foreach ($kw in $keywords) {
            if ($Reply.IndexOf([string]$kw, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
                $matched = $false
                break
            }
        }

        if ($matched) {
            $count = $count + 1
        }
    }

    return $count
}

function Get-RecentJobMetrics {
    param(
        [object]$Health,
        [long]$SinceMs
    )

    $recentJobsObj = Get-PropertyValue -Obj $Health -Candidates @('recentJobs') -DefaultValue @()
    $recentJobs = @()
    if ($recentJobsObj -is [array]) {
        $recentJobs = $recentJobsObj
    }
    elseif ($null -ne $recentJobsObj) {
        $recentJobs = @($recentJobsObj)
    }

    $scopedJobs = @()
    foreach ($job in $recentJobs) {
        $createdAt = Get-PropertyValue -Obj $job -Candidates @('createdAt', 'CreatedAt') -DefaultValue 0
        if ([int64]$createdAt -ge $SinceMs) {
            $scopedJobs += $job
        }
    }

    $success = 0
    $failed = 0
    $elapsedList = New-Object System.Collections.ArrayList
    $factsExtractedList = New-Object System.Collections.ArrayList

    foreach ($job in $scopedJobs) {
        $status = [string](Get-PropertyValue -Obj $job -Candidates @('status', 'Status') -DefaultValue '')
        if ($status -ieq 'completed') { $success = $success + 1 }
        if ($status -ieq 'failed') { $failed = $failed + 1 }

        $elapsed = [double](Get-PropertyValue -Obj $job -Candidates @('elapsedMs', 'ElapsedMs') -DefaultValue 0)
        $factsExtracted = [double](Get-PropertyValue -Obj $job -Candidates @('factsExtracted', 'FactsExtracted') -DefaultValue 0)

        [void]$elapsedList.Add($elapsed)
        [void]$factsExtractedList.Add($factsExtracted)
    }

    $avgElapsed = 0
    $avgFactsExtracted = 0

    if ($elapsedList.Count -gt 0) {
        $avgElapsed = ($elapsedList | Measure-Object -Average).Average
    }

    if ($factsExtractedList.Count -gt 0) {
        $avgFactsExtracted = ($factsExtractedList | Measure-Object -Average).Average
    }

    return [pscustomobject]@{
        JobCount = $scopedJobs.Count
        SuccessCount = $success
        FailCount = $failed
        AvgElapsedMs = [math]::Round([double]$avgElapsed, 2)
        AvgFactsExtracted = [math]::Round([double]$avgFactsExtracted, 2)
    }
}

function Get-ScalePlan {
    param([string]$Scale)

    $all = @(
        [pscustomobject]@{ Name = 'L1'; FactCount = 10;  WaitSec = 45 },
        [pscustomobject]@{ Name = 'L2'; FactCount = 50;  WaitSec = 60 },
        [pscustomobject]@{ Name = 'L3'; FactCount = 100; WaitSec = 90 },
        [pscustomobject]@{ Name = 'L4'; FactCount = 500; WaitSec = 180 }
    )

    if ($Scale -eq 'small') {
        return @($all[0], $all[1])
    }
    if ($Scale -eq 'medium') {
        return @($all[0], $all[1], $all[2])
    }

    return $all
}

function New-BottleneckNotes {
    param([System.Collections.ArrayList]$Rows)

    $notes = New-Object System.Collections.ArrayList

    $prevRecall = $null
    foreach ($row in $Rows) {
        if ($row.JobsFailCount -gt 0) {
            [void]$notes.Add("- $($row.Scale): 存在失败任务 ($($row.JobsFailCount))，需检查 SubconsciousJobLogs 错误详情。")
        }

        if ($null -ne $prevRecall -and $row.RecallLatencyMs -gt ($prevRecall * 1.8)) {
            [void]$notes.Add("- $($row.Scale): 回忆延迟较上一规模增长明显（$prevRecall -> $($row.RecallLatencyMs) ms），疑似召回路径或上下文构建成为瓶颈。")
        }

        $prevRecall = $row.RecallLatencyMs

        if ($row.MatchedFacts -lt [math]::Max(1, [math]::Floor($row.FactsSent * 0.05))) {
            [void]$notes.Add("- $($row.Scale): 命中事实偏低（$($row.MatchedFacts)/$($row.FactsSent)），可能需要优化抽取准确率或召回融合权重。")
        }
    }

    if ($notes.Count -eq 0) {
        [void]$notes.Add('- 当前各规模无明显异常瓶颈，建议继续扩大样本并结合日志做长期观测。')
    }

    return $notes
}

$globalSw = [System.Diagnostics.Stopwatch]::StartNew()
$repoRoot = Get-RepoRoot
$outputPath = Resolve-OutputDir -RepoRoot $repoRoot -Dir $OutputDir
if (-not (Test-Path $outputPath)) {
    [void](New-Item -ItemType Directory -Path $outputPath -Force)
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reportPath = Join-Path $outputPath ("benchmark-report-$timestamp.md")
$csvPath = Join-Path $outputPath ("benchmark-report-$timestamp.csv")

Write-Step "输出目录: $outputPath"
Write-Step "测试规模: $Scale"

$token = Invoke-AdminAndLogin -BaseUrl $BaseUrl
$headers = @{ Authorization = "Bearer $token" }

$ctx = Resolve-WorkspaceAndAgent -BaseUrl $BaseUrl -Headers $headers
$workspaceId = $ctx.WorkspaceId
$agentId = $ctx.AgentId
Write-Step "Workspace=$workspaceId, Agent=$agentId"

$results = New-Object System.Collections.ArrayList
$csvRows = New-Object System.Collections.ArrayList
$nextFactIndex = 1
$baselineHealth = $null

if (-not $SkipWarmup) {
    Write-Step 'Phase 1: 预热与基线'

    $warmupFacts = @(
        'Warmup fact: My name is warmup-tom.',
        'Warmup fact: I am 28 years old.',
        'Warmup fact: I love ramen.'
    )

    foreach ($message in $warmupFacts) {
        [void](Send-ChatMessage -BaseUrl $BaseUrl -Headers $headers -WorkspaceId $workspaceId -AgentId $agentId -MessageText $message -ForceNewSession)
    }

    Wait-ForProcessing -Seconds 45 -Label 'Warmup'
    $baselineHealth = Get-SubconsciousHealth -BaseUrl $BaseUrl
}
else {
    Write-Step 'Phase 1: 已跳过预热（-SkipWarmup）'
    $baselineHealth = Get-SubconsciousHealth -BaseUrl $BaseUrl
}

$scalePlan = Get-ScalePlan -Scale $Scale
Write-Step "Phase 2: 规模测试，共 $($scalePlan.Count) 档"

foreach ($plan in $scalePlan) {
    $scaleName = $plan.Name
    $factCount = [int]$plan.FactCount
    $waitSec = [int]$plan.WaitSec

    Write-Step "开始 $scaleName：发送 $factCount 条事实"

    $scaleStart = Get-NowMs
    $facts = New-BenchmarkFacts -Count $factCount -StartIndex $nextFactIndex
    $nextFactIndex = $nextFactIndex + $factCount

    $sendFail = 0
    $sendSw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($fact in $facts) {
        try {
            [void](Send-ChatMessage -BaseUrl $BaseUrl -Headers $headers -WorkspaceId $workspaceId -AgentId $agentId -MessageText $fact.Message -ForceNewSession)
        }
        catch {
            $sendFail = $sendFail + 1
            Write-Host "[benchmark-memory] 发送失败 fact[$($fact.Index)]：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
    $sendSw.Stop()

    Wait-ForProcessing -Seconds $waitSec -Label $scaleName

    $health = Get-SubconsciousHealth -BaseUrl $BaseUrl
    $metrics = Get-RecentJobMetrics -Health $health -SinceMs $scaleStart

    $summary = Get-PropertyValue -Obj $health -Candidates @('summary') -DefaultValue $null
    $totalFacts = [int](Get-PropertyValue -Obj $summary -Candidates @('totalFacts') -DefaultValue 0)

    $recallPrompt = 'What do you know about me?'
    $recallSw = [System.Diagnostics.Stopwatch]::StartNew()
    $recallResp = $null
    $recallError = $null
    try {
        $recallResp = Send-ChatMessage -BaseUrl $BaseUrl -Headers $headers -WorkspaceId $workspaceId -AgentId $agentId -MessageText $recallPrompt -ForceNewSession
    }
    catch {
        $recallError = $_.Exception.Message
    }
    $recallSw.Stop()

    $reply = ''
    $tokenUsage = 0
    if ($null -ne $recallResp) {
        $reply = [string](Get-PropertyValue -Obj $recallResp -Candidates @('reply') -DefaultValue '')
        $usage = Get-PropertyValue -Obj $recallResp -Candidates @('usage') -DefaultValue $null
        if ($null -ne $usage) {
            $tokenUsage = [int](Get-PropertyValue -Obj $usage -Candidates @('totalTokens', 'TotalTokens') -DefaultValue 0)
        }
    }

    $matchedFacts = Get-RecallMatchedFactCount -Reply $reply -Facts $facts

    $row = [pscustomobject]@{
        Scale = $scaleName
        FactsSent = $factCount
        FactsSendFailed = $sendFail
        SendElapsedMs = $sendSw.ElapsedMilliseconds
        JobsObserved = $metrics.JobCount
        JobsSuccessCount = $metrics.SuccessCount
        JobsFailCount = $metrics.FailCount
        AvgElapsedMs = $metrics.AvgElapsedMs
        AvgFactsExtracted = $metrics.AvgFactsExtracted
        MemoryFactsTotal = $totalFacts
        RecallLatencyMs = $recallSw.ElapsedMilliseconds
        MatchedFacts = $matchedFacts
        RecallTotalTokens = $tokenUsage
        RecallError = $recallError
    }

    [void]$results.Add($row)

    [void]$csvRows.Add([pscustomobject]@{
        scale = $scaleName
        factsSent = $factCount
        jobsSuccess = $metrics.SuccessCount
        jobsFail = $metrics.FailCount
        avgJobElapsedMs = $metrics.AvgElapsedMs
        memoryFactsTotal = $totalFacts
        recallLatencyMs = $recallSw.ElapsedMilliseconds
        matchedFacts = $matchedFacts
        recallTotalTokens = $tokenUsage
    })

    Write-Step "$scaleName 完成：Recall=${($recallSw.ElapsedMilliseconds)}ms, Matched=$matchedFacts, Tokens=$tokenUsage"
}

Write-Step 'Phase 3: 生成报告'

$baselineSummary = Get-PropertyValue -Obj $baselineHealth -Candidates @('summary') -DefaultValue $null
$baseSuccess = [int](Get-PropertyValue -Obj $baselineSummary -Candidates @('successCount') -DefaultValue 0)
$baseFail = [int](Get-PropertyValue -Obj $baselineSummary -Candidates @('failCount') -DefaultValue 0)
$baseFacts = [int](Get-PropertyValue -Obj $baselineSummary -Candidates @('totalFacts') -DefaultValue 0)

$notes = New-BottleneckNotes -Rows $results

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add('# Memory 系统大规模基准测试报告')
[void]$lines.Add('')
[void]$lines.Add("- 时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$lines.Add("- Scale 参数: $Scale")
[void]$lines.Add("- SkipWarmup: $SkipWarmup")
[void]$lines.Add("- BaseUrl: $BaseUrl")
[void]$lines.Add("- WorkspaceId: $workspaceId")
[void]$lines.Add("- AgentId: $agentId")
[void]$lines.Add('')
[void]$lines.Add('## Phase 1 基线')
[void]$lines.Add('')
[void]$lines.Add("- 基线 successCount: $baseSuccess")
[void]$lines.Add("- 基线 failCount: $baseFail")
[void]$lines.Add("- 基线 totalFacts: $baseFacts")
[void]$lines.Add('')
[void]$lines.Add('## 各规模关键指标')
[void]$lines.Add('')
[void]$lines.Add('| Scale | FactsSent | SendFail | JobsSuccess | JobsFail | AvgElapsedMs | AvgFactsExtracted | MemoryFactsTotal | RecallLatencyMs | MatchedFacts | RecallTotalTokens |')
[void]$lines.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($r in $results) {
    [void]$lines.Add("| $($r.Scale) | $($r.FactsSent) | $($r.FactsSendFailed) | $($r.JobsSuccessCount) | $($r.JobsFailCount) | $($r.AvgElapsedMs) | $($r.AvgFactsExtracted) | $($r.MemoryFactsTotal) | $($r.RecallLatencyMs) | $($r.MatchedFacts) | $($r.RecallTotalTokens) |")
}
[void]$lines.Add('')
[void]$lines.Add('## 延迟增长图表数据（CSV）')
[void]$lines.Add('')
[void]$lines.Add('```csv')
[void]$lines.Add('scale,factsSent,jobsSuccess,jobsFail,avgJobElapsedMs,memoryFactsTotal,recallLatencyMs,matchedFacts,recallTotalTokens')
foreach ($c in $csvRows) {
    [void]$lines.Add("$($c.scale),$($c.factsSent),$($c.jobsSuccess),$($c.jobsFail),$($c.avgJobElapsedMs),$($c.memoryFactsTotal),$($c.recallLatencyMs),$($c.matchedFacts),$($c.recallTotalTokens)")
}
[void]$lines.Add('```')
[void]$lines.Add('')
[void]$lines.Add('## 系统瓶颈识别')
[void]$lines.Add('')
foreach ($n in $notes) {
    [void]$lines.Add($n)
}
[void]$lines.Add('')
[void]$lines.Add('## 异常记录')
[void]$lines.Add('')
$hasError = $false
foreach ($r in $results) {
    if (-not [string]::IsNullOrWhiteSpace([string]$r.RecallError)) {
        $hasError = $true
        [void]$lines.Add("- $($r.Scale): RecallError = $($r.RecallError)")
    }
}
if (-not $hasError) {
    [void]$lines.Add('- 无')
}

$lines | Set-Content -Path $reportPath -Encoding UTF8
$csvRows | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

$globalSw.Stop()
Write-Host ''
Write-Host '===================== DONE =====================' -ForegroundColor Green
Write-Host "Report: $reportPath" -ForegroundColor Green
Write-Host "CSV   : $csvPath" -ForegroundColor Green
Write-Host "Elapsed: $($globalSw.Elapsed)" -ForegroundColor Green
