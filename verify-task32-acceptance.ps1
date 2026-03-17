param(
    [string]$ControllerBaseUrl = "http://localhost",
    [string]$WorkspaceId = "default",
    [string]$ChannelId = "cli",
    [string]$UserExternalId = "task32-verifier",
    [switch]$FailOnWarning
)

$ErrorActionPreference = "Stop"

function New-CheckResult {
    param(
        [string]$Name,
        [bool]$Success,
        [string]$Detail,
        [string]$Level = "info"
    )
    return [ordered]@{
        name = $Name
        success = $Success
        detail = $Detail
        level = $Level
        at = (Get-Date).ToString("o")
    }
}

function Invoke-Api {
    param(
        [string]$Method,
        [string]$Path,
        $Body = $null
    )

    $url = "$ControllerBaseUrl$Path"
    try {
        if ($null -eq $Body) {
            $resp = Invoke-RestMethod -Method $Method -Uri $url -TimeoutSec 20
        }
        else {
            $json = $Body | ConvertTo-Json -Depth 20
            $resp = Invoke-RestMethod -Method $Method -Uri $url -Body $json -ContentType "application/json" -TimeoutSec 20
        }

        return [ordered]@{ ok = $true; data = $resp; error = $null; url = $url }
    }
    catch {
        return [ordered]@{ ok = $false; data = $null; error = $_.Exception.Message; url = $url }
    }
}

$checks = New-Object System.Collections.Generic.List[object]
$artifacts = [ordered]@{}
$runId = Get-Date -Format "yyyyMMdd-HHmmss"

# 0) 预检查：Controller API 可达性
$preflight = Invoke-Api -Method "GET" -Path "/api/workspace"
if (-not $preflight.ok) {
    $checks.Add((New-CheckResult -Name "preflight_controller_api" -Success $false -Detail "Controller API 不可达：$($preflight.error)；请先启动 docker compose 或本地服务。" -Level "critical"))

    $report = [ordered]@{
        runId = $runId
        createdAt = (Get-Date).ToString("o")
        controllerBaseUrl = $ControllerBaseUrl
        workspaceId = $WorkspaceId
        channelId = $ChannelId
        userExternalId = $UserExternalId
        artifacts = $artifacts
        summary = [ordered]@{
            total = $checks.Count
            passed = 0
            failed = 1
            criticalFailed = 1
            warningFailed = 0
            status = "blocked"
        }
        checks = $checks
    }

    New-Item -ItemType Directory -Path "$PSScriptRoot\TestResults" -Force | Out-Null
    $reportPath = "$PSScriptRoot\TestResults\task32-acceptance-$runId.json"
    $report | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath -Encoding UTF8

    Write-Host "`n=== Task32 Acceptance Summary ===" -ForegroundColor Cyan
    Write-Host "Status: blocked" -ForegroundColor Yellow
    Write-Host "Detail: Controller API 不可达，请启动服务后重试。"
    Write-Host "Report: $reportPath" -ForegroundColor Yellow
    exit 2
}

# 1) Workspace 基础可用性
$wsResp = Invoke-Api -Method "GET" -Path "/api/workspace/$([uri]::EscapeDataString($WorkspaceId))"
if ($wsResp.ok -and $wsResp.data.workspaceId) {
    $checks.Add((New-CheckResult -Name "workspace_lookup" -Success $true -Detail "workspace=$($wsResp.data.workspaceId)"))
}
else {
    $checks.Add((New-CheckResult -Name "workspace_lookup" -Success $false -Detail "无法读取 workspace：$($wsResp.error)" -Level "critical"))
}

# 2) 冻结/解冻 + 审计链路
$freezeResp = Invoke-Api -Method "POST" -Path "/api/workspace/$([uri]::EscapeDataString($WorkspaceId))/freeze"
$unfreezeResp = Invoke-Api -Method "POST" -Path "/api/workspace/$([uri]::EscapeDataString($WorkspaceId))/unfreeze"
$auditResp = Invoke-Api -Method "GET" -Path "/api/audit?workspaceId=$([uri]::EscapeDataString($WorkspaceId))&limit=200"

$hasFreezeAudit = $false
$hasResumeAudit = $false
if ($auditResp.ok -and $auditResp.data) {
    foreach ($e in $auditResp.data) {
        if ("$($e.eventType)" -eq "WorkspaceFrozen") { $hasFreezeAudit = $true }
        if ("$($e.eventType)" -eq "WorkspaceResumed") { $hasResumeAudit = $true }
    }
}

$checks.Add((New-CheckResult -Name "workspace_freeze_unfreeze" -Success ($freezeResp.ok -and $unfreezeResp.ok) -Detail "freeze=$($freezeResp.ok), unfreeze=$($unfreezeResp.ok)" -Level "critical"))
$checks.Add((New-CheckResult -Name "workspace_freeze_audit" -Success $hasFreezeAudit -Detail "WorkspaceFrozen 审计事件=$hasFreezeAudit" -Level "warning"))
$checks.Add((New-CheckResult -Name "workspace_resume_audit" -Success $hasResumeAudit -Detail "WorkspaceResumed 审计事件=$hasResumeAudit" -Level "warning"))

# 3) 消息主链路 + 调试链路
$msgText = "task32 acceptance ping @ $runId"
$msgResp = Invoke-Api -Method "POST" -Path "/api/messageingress" -Body @{
    channelId = $ChannelId
    userExternalId = $UserExternalId
    workspaceId = $WorkspaceId
    messageText = $msgText
}

if ($msgResp.ok -and $msgResp.data.messageId) {
    $messageId = "$($msgResp.data.messageId)"
    $sessionId = "$($msgResp.data.sessionId)"
    $artifacts.messageId = $messageId
    $artifacts.sessionId = $sessionId

    $routeResp = Invoke-Api -Method "GET" -Path "/api/debug/route/$([uri]::EscapeDataString($messageId))"
    $msgDebugResp = Invoke-Api -Method "GET" -Path "/api/debug/message/$([uri]::EscapeDataString($messageId))"
    $sessionDebugResp = $null
    if ($sessionId) {
        $sessionDebugResp = Invoke-Api -Method "GET" -Path "/api/debug/session/$([uri]::EscapeDataString($sessionId))"
    }

    $checks.Add((New-CheckResult -Name "message_ingress" -Success $true -Detail "messageId=$messageId, sessionId=$sessionId, replySuccess=$($msgResp.data.isSuccess)" -Level "critical"))
    $checks.Add((New-CheckResult -Name "route_debug" -Success $routeResp.ok -Detail "route record query ok=$($routeResp.ok)" -Level "critical"))
    $checks.Add((New-CheckResult -Name "message_debug" -Success $msgDebugResp.ok -Detail "message debug query ok=$($msgDebugResp.ok)" -Level "critical"))
    $checks.Add((New-CheckResult -Name "session_debug" -Success ($null -ne $sessionDebugResp -and $sessionDebugResp.ok) -Detail "session debug query ok=$($sessionDebugResp.ok)" -Level "warning"))
}
else {
    $checks.Add((New-CheckResult -Name "message_ingress" -Success $false -Detail "消息主链路失败：$($msgResp.error)" -Level "critical"))
}

# 4) Knowledge / Storage / Graph 最小链路
$docId = "acceptance-$runId"
$docResp = Invoke-Api -Method "POST" -Path "/api/knowledge/$([uri]::EscapeDataString($WorkspaceId))/documents" -Body @{
    documentId = $docId
    workspaceId = $WorkspaceId
    title = "Acceptance Document"
    content = "This is a task32 acceptance knowledge document."
}
$searchResp = Invoke-Api -Method "POST" -Path "/api/knowledge/$([uri]::EscapeDataString($WorkspaceId))/search" -Body @{
    query = "acceptance"
    topK = 5
}
$deleteDocResp = Invoke-Api -Method "DELETE" -Path "/api/knowledge/$([uri]::EscapeDataString($WorkspaceId))/documents/$([uri]::EscapeDataString($docId))"
$checks.Add((New-CheckResult -Name "knowledge_chain" -Success ($docResp.ok -and $searchResp.ok -and $deleteDocResp.ok) -Detail "index=$($docResp.ok), search=$($searchResp.ok), delete=$($deleteDocResp.ok)" -Level "warning"))

$raw = [System.Text.Encoding]::UTF8.GetBytes("task32 storage payload")
$base64 = [Convert]::ToBase64String($raw)
$putObjResp = Invoke-Api -Method "PUT" -Path "/api/storage/$([uri]::EscapeDataString($WorkspaceId))/objects" -Body @{
    path = "acceptance/$runId.txt"
    contentBase64 = $base64
    contentType = "text/plain"
}
$objectId = if ($putObjResp.ok) { "$($putObjResp.data.objectId)" } else { "" }
$getObjResp = if ($objectId) { Invoke-Api -Method "GET" -Path "/api/storage/$([uri]::EscapeDataString($WorkspaceId))/objects/$([uri]::EscapeDataString($objectId))" } else { [ordered]@{ ok = $false; data = $null; error = "objectId empty" } }
$deleteObjResp = if ($objectId) { Invoke-Api -Method "DELETE" -Path "/api/storage/$([uri]::EscapeDataString($WorkspaceId))/objects/$([uri]::EscapeDataString($objectId))" } else { [ordered]@{ ok = $false; data = $null; error = "objectId empty" } }
$checks.Add((New-CheckResult -Name "storage_chain" -Success ($putObjResp.ok -and $getObjResp.ok -and $deleteObjResp.ok) -Detail "put=$($putObjResp.ok), get=$($getObjResp.ok), delete=$($deleteObjResp.ok)" -Level "warning"))

$entityId = "e-$runId"
$upsertEntityResp = Invoke-Api -Method "PUT" -Path "/api/graph/$([uri]::EscapeDataString($WorkspaceId))/entities" -Body @{
    entityId = $entityId
    workspaceId = $WorkspaceId
    type = "Acceptance"
    label = "Task32 Probe"
    properties = @{ runId = $runId }
}
$queryEntityResp = Invoke-Api -Method "POST" -Path "/api/graph/$([uri]::EscapeDataString($WorkspaceId))/entities/query" -Body @{
    keyword = "Task32"
    limit = 10
}
$deleteEntityResp = Invoke-Api -Method "DELETE" -Path "/api/graph/$([uri]::EscapeDataString($WorkspaceId))/entities/$([uri]::EscapeDataString($entityId))"
$checks.Add((New-CheckResult -Name "graph_chain" -Success ($upsertEntityResp.ok -and $queryEntityResp.ok -and $deleteEntityResp.ok) -Detail "upsert=$($upsertEntityResp.ok), query=$($queryEntityResp.ok), delete=$($deleteEntityResp.ok)" -Level "warning"))

# 5) metrics / workspace debug
$metricsResp = Invoke-Api -Method "GET" -Path "/api/debug/metrics"
$workspaceDebugResp = Invoke-Api -Method "GET" -Path "/api/debug/workspace/$([uri]::EscapeDataString($WorkspaceId))"
$checks.Add((New-CheckResult -Name "metrics_endpoint" -Success $metricsResp.ok -Detail "metrics query ok=$($metricsResp.ok)" -Level "critical"))
$checks.Add((New-CheckResult -Name "workspace_debug_endpoint" -Success $workspaceDebugResp.ok -Detail "workspace debug query ok=$($workspaceDebugResp.ok)" -Level "critical"))

$criticalFailed = @($checks | Where-Object { -not $_.success -and $_.level -eq "critical" }).Count
$warningFailed = @($checks | Where-Object { -not $_.success -and $_.level -eq "warning" }).Count

$report = [ordered]@{
    runId = $runId
    createdAt = (Get-Date).ToString("o")
    controllerBaseUrl = $ControllerBaseUrl
    workspaceId = $WorkspaceId
    channelId = $ChannelId
    userExternalId = $UserExternalId
    artifacts = $artifacts
    summary = [ordered]@{
        total = $checks.Count
        passed = @($checks | Where-Object { $_.success }).Count
        failed = @($checks | Where-Object { -not $_.success }).Count
        criticalFailed = $criticalFailed
        warningFailed = $warningFailed
        status = if ($criticalFailed -eq 0) { "pass" } else { "fail" }
    }
    checks = $checks
}

New-Item -ItemType Directory -Path "$PSScriptRoot\TestResults" -Force | Out-Null
$reportPath = "$PSScriptRoot\TestResults\task32-acceptance-$runId.json"
$report | ConvertTo-Json -Depth 20 | Set-Content -Path $reportPath -Encoding UTF8

Write-Host "`n=== Task32 Acceptance Summary ===" -ForegroundColor Cyan
Write-Host "Status: $($report.summary.status)" -ForegroundColor $(if ($report.summary.status -eq "pass") { "Green" } else { "Red" })
Write-Host "Passed: $($report.summary.passed) / $($report.summary.total)"
Write-Host "CriticalFailed: $criticalFailed, WarningFailed: $warningFailed"
Write-Host "Report: $reportPath" -ForegroundColor Yellow

if ($criticalFailed -gt 0 -or ($FailOnWarning -and $warningFailed -gt 0)) {
    exit 1
}

exit 0
