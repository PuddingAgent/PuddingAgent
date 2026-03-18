# ============================================================
#  PuddingPlatform API 集成测试脚本
#  测试对象：http://localhost/api
#  前置条件：所有 Docker 容器处于运行状态
# ============================================================

param(
    [string]$BaseUrl  = "http://localhost/api",
    [string]$AdminUser = "admin",
    [string]$AdminPass = "Admin@123"
)

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

# ── 统计 ────────────────────────────────────────────────────
$script:pass  = 0
$script:fail  = 0
$script:token = ""

# ── 输出助手 ────────────────────────────────────────────────
function Write-Pass([string]$msg) {
    Write-Host "  [PASS] $msg" -ForegroundColor Green
    $script:pass++
}
function Write-Fail([string]$msg) {
    Write-Host "  [FAIL] $msg" -ForegroundColor Red
    $script:fail++
}
function Write-Info([string]$msg) {
    Write-Host "         $msg" -ForegroundColor DarkGray
}
function Write-Section([string]$title) {
    Write-Host ""
    Write-Host "══════════════════════════════════════════════" -ForegroundColor DarkCyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "══════════════════════════════════════════════" -ForegroundColor DarkCyan
}

function AuthHeaders {
    if ($script:token) {
        return @{ Authorization = "Bearer $($script:token)" }
    }
    return @{}
}

# 包装 Invoke-RestMethod，返回 $null 表示失败而不抛异常
function Api {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null,
        [switch]$Auth,
        [int[]]$OkCodes = @(200, 201)
    )
    $uri = "$BaseUrl$Path"
    $headers = if ($Auth) { AuthHeaders } else { @{} }
    try {
        $params = @{
            Method          = $Method
            Uri             = $uri
            Headers         = $headers
            ContentType     = "application/json"
            UseBasicParsing = $true
        }
        if ($Body) {
            $params.Body = ($Body | ConvertTo-Json -Depth 10 -Compress)
        }
        $resp = Invoke-WebRequest @params
        return ($resp.Content | ConvertFrom-Json)
    }
    catch [System.Net.WebException] {
        $statusCode = [int]$_.Exception.Response.StatusCode
        # 204 No Content 是成功的
        if ($statusCode -eq 204 -or $OkCodes -contains $statusCode) {
            return $null
        }
        $body = ""
        try {
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $body = $reader.ReadToEnd()
        } catch {}
        throw "HTTP $statusCode — $body"
    }
}

function ApiDelete {
    param([string]$Path)
    $uri = "$BaseUrl$Path"
    $headers = AuthHeaders
    try {
        Invoke-WebRequest -Method DELETE -Uri $uri -Headers $headers -UseBasicParsing | Out-Null
        return $true
    }
    catch [System.Net.WebException] {
        $code = [int]$_.Exception.Response.StatusCode
        if ($code -eq 204 -or $code -eq 200) { return $true }
        throw "HTTP $code"
    }
}

# ============================================================
#  1. 认证  Auth
# ============================================================
Write-Section "1 / 认证 Auth"

## 1.1 登录（正确凭证）
try {
    $r = Api POST "/login/account" @{ username = $AdminUser; password = $AdminPass }
    if ($r.status -eq "ok" -and $r.token) {
        $script:token = $r.token
        Write-Pass "POST /login/account — admin 登录成功，authority=$($r.currentAuthority)"
    } else {
        Write-Fail "POST /login/account — 响应 status 不是 ok: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "POST /login/account — 异常: $_" }

## 1.2 登录（错误密码）
try {
    $r = Api POST "/login/account" @{ username = $AdminUser; password = "WrongPassword!" }
    if ($r.status -eq "error") {
        Write-Pass "POST /login/account — 错误密码正确返回 status=error"
    } else {
        Write-Fail "POST /login/account — 错误密码期望 error，实际: $($r.status)"
    }
}
catch { Write-Fail "POST /login/account (wrong pwd) — 异常: $_" }

## 1.3 currentUser（已登录）
try {
    $r = Api GET "/currentUser" -Auth
    if ($r.userid -and $r.access) {
        Write-Pass "GET /currentUser — userid=$($r.userid) access=$($r.access)"
    } else {
        Write-Fail "GET /currentUser — 返回数据不完整: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "GET /currentUser — 异常: $_" }

## 1.4 currentUser（匿名）
try {
    $r = Api GET "/currentUser"
    if ($r.access -eq "guest") {
        Write-Pass "GET /currentUser (匿名) — 正确返回 guest"
    } else {
        Write-Fail "GET /currentUser (匿名) — 期望 guest，实际: $($r.access)"
    }
}
catch { Write-Fail "GET /currentUser (匿名) — 异常: $_" }

# ============================================================
#  2. 用户管理 AppUser
# ============================================================
Write-Section "2 / 用户管理 AppUser"

$testUserId = "test-user-$(Get-Date -Format 'mmss')"

## 2.1 列出用户
try {
    $r = Api GET "/users" -Auth
    Write-Pass "GET /users — 共 $($r.Count) 个用户"
}
catch { Write-Fail "GET /users — 异常: $_" }

## 2.2 创建用户
try {
    $r = Api POST "/users" @{
        userId      = $testUserId
        username    = $testUserId
        email       = "$testUserId@test.local"
        displayName = "测试用户"
        password    = "Test@123456"
        userType    = "SimpleUser"
    } -Auth
    if ($r.userId -eq $testUserId) {
        Write-Pass "POST /users — 创建用户 $testUserId 成功"
    } else {
        Write-Fail "POST /users — 响应 userId 不匹配: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "POST /users — 异常: $_" }

## 2.3 重复创建（应返回 409）
try {
    Api POST "/users" @{
        userId   = $testUserId
        username = $testUserId
        email    = "$testUserId@test.local"
        password = "Test@123456"
        userType = "SimpleUser"
    } -Auth | Out-Null
    Write-Fail "POST /users (重复) — 期望 409 Conflict，实际未报错"
}
catch {
    if ($_ -match "409") {
        Write-Pass "POST /users (重复) — 正确返回 409 Conflict"
    } else {
        Write-Fail "POST /users (重复) — 期望 409，实际: $_"
    }
}

## 2.4 获取单个用户
try {
    $r = Api GET "/users/$testUserId" -Auth
    if ($r.userId -eq $testUserId) {
        Write-Pass "GET /users/$testUserId — 获取成功"
    } else {
        Write-Fail "GET /users/$testUserId — userId 不匹配"
    }
}
catch { Write-Fail "GET /users/$testUserId — 异常: $_" }

## 2.5 更新用户
try {
    $r = Api PUT "/users/$testUserId" @{
        username    = $testUserId
        email       = "$testUserId@test.local"
        displayName = "Updated-Display-Name"
        userType    = "SimpleUser"
        isEnabled   = $true
    } -Auth
    if ($r.displayName -eq "Updated-Display-Name") {
        Write-Pass "PUT /users/$testUserId — 更新 displayName 成功"
    } else {
        Write-Fail "PUT /users/$testUserId — displayName 未更新: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "PUT /users/$testUserId — 异常: $_" }

## 2.6 修改密码
try {
    Api PUT "/users/$testUserId/password" @{ newPassword = "NewPass@789" } -Auth | Out-Null
    Write-Pass "PUT /users/$testUserId/password — 修改密码成功（204）"
}
catch { Write-Fail "PUT /users/$testUserId/password — 异常: $_" }

## 2.7 删除用户
try {
    ApiDelete "/users/$testUserId" | Out-Null
    Write-Pass "DELETE /users/$testUserId — 删除成功"
}
catch { Write-Fail "DELETE /users/$testUserId — 异常: $_" }

## 2.8 删除后 404
try {
    Api GET "/users/$testUserId" -Auth | Out-Null
    Write-Fail "GET /users/$testUserId (已删) — 期望 404，实际返回数据"
}
catch {
    if ($_ -match "404") {
        Write-Pass "GET /users/$testUserId (已删) — 正确返回 404"
    } else {
        Write-Fail "GET /users/$testUserId (已删) — 期望 404，实际: $_"
    }
}

## 2.9 不能删除最后一个 Admin
try {
    ApiDelete "/users/admin" | Out-Null
    Write-Fail "DELETE /users/admin — 期望阻止删除最后一个 Admin，实际删除成功（危险！）"
}
catch {
    if ($_ -match "400") {
        Write-Pass "DELETE /users/admin — 正确阻止删除最后 Admin（400）"
    } else {
        Write-Fail "DELETE /users/admin — 期望 400，实际: $_"
    }
}

# ============================================================
#  3. 角色管理 AppRole
# ============================================================
Write-Section "3 / 角色管理 AppRole"

$testRoleId = "test-role-$(Get-Date -Format 'mmss')"

## 3.1 列出角色
try {
    $r = Api GET "/roles" -Auth
    Write-Pass "GET /roles — 共 $($r.Count) 个角色"
}
catch { Write-Fail "GET /roles — 异常: $_" }

## 3.2 创建角色
try {
    $r = Api POST "/roles" @{
        roleId      = $testRoleId
        name        = "测试角色"
        description = "集成测试自动创建"
        permissions = @("read", "write")
    } -Auth
    if ($r.roleId -eq $testRoleId) {
        Write-Pass "POST /roles — 创建角色 $testRoleId 成功"
    } else {
        Write-Fail "POST /roles — roleId 不匹配: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "POST /roles — 异常: $_" }

## 3.3 获取角色
try {
    $r = Api GET "/roles/$testRoleId" -Auth
    Write-Pass "GET /roles/$testRoleId — 权限: $($r.permissions -join ', ')"
}
catch { Write-Fail "GET /roles/$testRoleId — 异常: $_" }

## 3.4 更新角色
try {
    $r = Api PUT "/roles/$testRoleId" @{
        roleId      = $testRoleId
        name        = "Updated-Role-Name"
        description = "updated"
        permissions = @("read", "write", "admin")
    } -Auth
    if ($r.name -eq "Updated-Role-Name") {
        Write-Pass "PUT /roles/$testRoleId — 更新成功"
    } else {
        Write-Fail "PUT /roles/$testRoleId — name 未更新"
    }
}
catch { Write-Fail "PUT /roles/$testRoleId — 异常: $_" }

## 3.5 删除角色
try {
    ApiDelete "/roles/$testRoleId" | Out-Null
    Write-Pass "DELETE /roles/$testRoleId — 删除成功"
}
catch { Write-Fail "DELETE /roles/$testRoleId — 异常: $_" }

# ============================================================
#  4. 团队与工作区 Team / Workspace
# ============================================================
Write-Section "4 / 团队与工作区 Team / Workspace"

$testTeamId = "test-team-$(Get-Date -Format 'mmss')"
$testWsId   = "test-ws-$(Get-Date -Format 'mmss')"

## 4.1 列出团队
try {
    $r = Api GET "/teams" -Auth
    Write-Pass "GET /teams — 共 $($r.Count) 个团队"
}
catch { Write-Fail "GET /teams — 异常: $_" }

## 4.2 创建团队
try {
    $r = Api POST "/teams" @{
        teamId      = $testTeamId
        name        = "测试团队"
        description = "集成测试自动创建"
        isEnabled   = $true
    } -Auth
    if ($r.teamId -eq $testTeamId) {
        Write-Pass "POST /teams — 创建团队 $testTeamId 成功"
    } else {
        Write-Fail "POST /teams — teamId 不匹配"
    }
}
catch { Write-Fail "POST /teams — 异常: $_" }

## 4.3 获取团队详情
try {
    $r = Api GET "/teams/$testTeamId" -Auth
    Write-Pass "GET /teams/$testTeamId — 成员: $($r.members.Count) 工作区: $($r.workspaces.Count)"
}
catch { Write-Fail "GET /teams/$testTeamId — 异常: $_" }

## 4.4 添加成员
try {
    $r = Api POST "/teams/$testTeamId/members" @{
        userId = "admin"
        role   = "Member"
    } -Auth
    if ($r.userId -eq "admin") {
        Write-Pass "POST /teams/$testTeamId/members — 添加 admin 为成员成功"
    } else {
        Write-Fail "POST /teams/$testTeamId/members — 响应异常"
    }
}
catch { Write-Fail "POST /teams/$testTeamId/members — 异常: $_" }

## 4.5 成员列表
try {
    $r = Api GET "/teams/$testTeamId/members" -Auth
    Write-Pass "GET /teams/$testTeamId/members — 共 $($r.Count) 个成员"
}
catch { Write-Fail "GET /teams/$testTeamId/members — 异常: $_" }

## 4.6 创建工作区
try {
    $r = Api POST "/teams/$testTeamId/workspaces" @{
        workspaceId        = $testWsId
        teamId             = $testTeamId
        name               = "Test-Workspace"
        description        = "integration test"
        teamAccessPolicy   = "Write"
        companyAccessPolicy = "None"
    } -Auth
    if ($r -and $r.workspaceId -eq $testWsId) {
        Write-Pass "POST /teams/$testTeamId/workspaces — 创建工作区 $testWsId 成功"
    } else {
        Write-Fail "POST /teams/$testTeamId/workspaces — workspaceId 不匹配: $($r | ConvertTo-Json -Compress)"
    }
}
catch { Write-Fail "POST /teams/$testTeamId/workspaces — 异常: $_" }

## 4.7 查询工作区
try {
    $r = Api GET "/teams/workspaces/$testWsId" -Auth
    Write-Pass "GET /teams/workspaces/$testWsId — teamId=$($r.teamId)"
}
catch { Write-Fail "GET /teams/workspaces/$testWsId — 异常: $_" }

## 4.8 更新工作区
try {
    $r = Api PUT "/teams/workspaces/$testWsId" @{
        name                = "Updated-Workspace"
        description         = "updated"
        teamAccessPolicy    = "ReadOnly"
        companyAccessPolicy = "None"
        isEnabled           = $true
    } -Auth
    if ($r.name -eq "Updated-Workspace") {
        Write-Pass "PUT /teams/workspaces/$testWsId — 更新成功"
    } else {
        Write-Fail "PUT /teams/workspaces/$testWsId — name 未更新"
    }
}
catch { Write-Fail "PUT /teams/workspaces/$testWsId — 异常: $_" }

## 4.9 删除工作区
try {
    ApiDelete "/teams/workspaces/$testWsId" | Out-Null
    Write-Pass "DELETE /teams/workspaces/$testWsId — 删除成功"
}
catch { Write-Fail "DELETE /teams/workspaces/$testWsId — 异常: $_" }

## 4.10 移除成员
try {
    ApiDelete "/teams/$testTeamId/members/admin" | Out-Null
    Write-Pass "DELETE /teams/$testTeamId/members/admin — 移除成功"
}
catch { Write-Fail "DELETE /teams/$testTeamId/members/admin — 异常: $_" }

## 4.11 删除团队
try {
    ApiDelete "/teams/$testTeamId" | Out-Null
    Write-Pass "DELETE /teams/$testTeamId — 删除成功"
}
catch { Write-Fail "DELETE /teams/$testTeamId — 异常: $_" }

## 4.12 有工作区时不能删除团队（重建验证）
try {
    $tmpTeamId = "tmp-team-$(Get-Date -Format 'mmss')"
    $tmpWsId   = "tmp-ws-$(Get-Date -Format 'mmss')"
    Api POST "/teams" @{ teamId=$tmpTeamId; name="临时"; isEnabled=$true } -Auth | Out-Null
    Api POST "/teams/$tmpTeamId/workspaces" @{
        workspaceId="$tmpWsId"; teamId=$tmpTeamId; name="Tmp-Workspace"
        teamAccessPolicy="Write"; companyAccessPolicy="None"
    } -Auth | Out-Null
    try {
        ApiDelete "/teams/$tmpTeamId" | Out-Null
        Write-Fail "DELETE 有工作区的团队 — 期望 400，实际删除成功"
    }
    catch {
        if ($_ -match "400") {
            Write-Pass "DELETE 有工作区的团队 — 正确阻止（400）"
        } else {
            Write-Fail "DELETE 有工作区的团队 — 期望 400，实际: $_"
        }
    }
    # 清理
    ApiDelete "/teams/workspaces/$tmpWsId" | Out-Null
    ApiDelete "/teams/$tmpTeamId" | Out-Null
}
catch { Write-Fail "有工作区删团队保护测试 — 异常: $_" }

# ============================================================
#  5. LLM 服务商与模型
# ============================================================
Write-Section "5 / LLM 服务商与模型"

$testProviderId = "test-provider-$(Get-Date -Format 'mmss')"
$testModelId    = "gpt-test-$(Get-Date -Format 'mmss')"

## 5.1 列出服务商
try {
    $r = Api GET "/llm/providers" -Auth
    Write-Pass "GET /llm/providers — 共 $($r.Count) 个服务商"
}
catch { Write-Fail "GET /llm/providers — 异常: $_" }

## 5.2 创建服务商
try {
    $r = Api POST "/llm/providers" @{
        providerId  = $testProviderId
        name        = "测试服务商"
        protocol    = "OpenAI"
        baseUrl     = "https://api.test.local/v1"
        apiKey      = "sk-test-key-12345"
        description = "集成测试自动创建"
        isEnabled   = $true
    } -Auth
    if ($r.providerId -eq $testProviderId) {
        Write-Pass "POST /llm/providers — 创建 $testProviderId 成功"
    } else {
        Write-Fail "POST /llm/providers — providerId 不匹配"
    }
}
catch { Write-Fail "POST /llm/providers — 异常: $_" }

## 5.3 获取详情
try {
    $r = Api GET "/llm/providers/$testProviderId" -Auth
    Write-Pass "GET /llm/providers/$testProviderId — hasApiKey=$($r.hasApiKey)"
}
catch { Write-Fail "GET /llm/providers/$testProviderId — 异常: $_" }

## 5.4 更新服务商
try {
    $r = Api PUT "/llm/providers/$testProviderId" @{
        providerId  = $testProviderId
        name        = "Updated-Provider"
        protocol    = "OpenAI"
        baseUrl     = "https://api.test.local/v1"
        description = "updated"
        isEnabled   = $true
    } -Auth
    if ($r.name -eq "Updated-Provider") {
        Write-Pass "PUT /llm/providers/$testProviderId — 更新成功"
    } else {
        Write-Fail "PUT /llm/providers/$testProviderId — name 未更新"
    }
}
catch { Write-Fail "PUT /llm/providers/$testProviderId — 异常: $_" }

## 5.5 配额管理
try {
    $r = Api PUT "/llm/providers/$testProviderId/quota" @{
        dailyTokenLimit   = 1000000
        monthlyTokenLimit = 10000000
    } -Auth
    if ($r.dailyTokenLimit -eq 1000000) {
        Write-Pass "PUT /llm/providers/$testProviderId/quota — 设置配额成功"
    } else {
        Write-Fail "PUT /llm/providers/$testProviderId/quota — 配额值不匹配"
    }
}
catch { Write-Fail "PUT /llm/providers/$testProviderId/quota — 异常: $_" }

## 5.6 查询配额
try {
    $r = Api GET "/llm/providers/$testProviderId/quota" -Auth
    Write-Pass "GET /llm/providers/$testProviderId/quota — daily=$($r.dailyTokenLimit) monthly=$($r.monthlyTokenLimit)"
}
catch { Write-Fail "GET /llm/providers/$testProviderId/quota — 异常: $_" }

## 5.7 重置每日配额
try {
    Api POST "/llm/providers/$testProviderId/quota/reset-daily" -Auth | Out-Null
    Write-Pass "POST /llm/providers/$testProviderId/quota/reset-daily — 重置成功"
}
catch { Write-Fail "POST /llm/providers/$testProviderId/quota/reset-daily — 异常: $_" }

## 5.8 创建模型
try {
    $r = Api POST "/llm/providers/$testProviderId/models" @{
        modelId                  = $testModelId
        name                     = "测试模型"
        description              = "集成测试模型"
        maxContextTokens         = 128000
        inputPricePer1MTokens    = 0.5
        outputPricePer1MTokens   = 1.5
        capabilityTags           = @("chat", "function_call")
        isDeprecated             = $false
        isDefault                = $false
        sortOrder                = 99
    } -Auth
    if ($r.modelId -eq $testModelId) {
        Write-Pass "POST /llm/providers/$testProviderId/models — 创建模型 $testModelId 成功"
    } else {
        Write-Fail "POST /llm/providers/$testProviderId/models — modelId 不匹配"
    }
}
catch { Write-Fail "POST /llm/providers/$testProviderId/models — 异常: $_" }

## 5.9 获取模型列表
try {
    $r = Api GET "/llm/providers/$testProviderId/models" -Auth
    Write-Pass "GET /llm/providers/$testProviderId/models — 共 $($r.Count) 个模型"
}
catch { Write-Fail "GET /llm/providers/$testProviderId/models — 异常: $_" }

## 5.10 更新模型
try {
    $r = Api PUT "/llm/providers/$testProviderId/models/$testModelId" @{
        modelId                  = $testModelId
        name                     = "Updated-Model"
        description              = "updated"
        maxContextTokens         = 200000
        inputPricePer1MTokens    = 0.6
        outputPricePer1MTokens   = 1.8
        capabilityTags           = @("chat")
        isDeprecated             = $false
        isDefault                = $true
        sortOrder                = 1
    } -Auth
    if ($r.name -eq "Updated-Model") {
        Write-Pass "PUT /llm/providers/$testProviderId/models/$testModelId — 更新成功"
    } else {
        Write-Fail "PUT /llm/providers/$testProviderId/models/$testModelId — name 未更新"
    }
}
catch { Write-Fail "PUT /llm/providers/$testProviderId/models/$testModelId — 异常: $_" }

## 5.11 删除模型
try {
    ApiDelete "/llm/providers/$testProviderId/models/$testModelId" | Out-Null
    Write-Pass "DELETE /llm/providers/$testProviderId/models/$testModelId — 删除成功"
}
catch { Write-Fail "DELETE /llm/providers/$testProviderId/models/$testModelId — 异常: $_" }

## 5.12 删除服务商
try {
    ApiDelete "/llm/providers/$testProviderId" | Out-Null
    Write-Pass "DELETE /llm/providers/$testProviderId — 删除成功"
}
catch { Write-Fail "DELETE /llm/providers/$testProviderId — 异常: $_" }

# ============================================================
#  6. 会话历史 Sessions（只读）
# ============================================================
Write-Section "6 / 会话历史 Sessions"

## 6.1 列出会话（可能为空，正常）
try {
    $r = Api GET "/sessions" -Auth
    Write-Pass "GET /sessions — 共 $(@($r).Count) 条记录"
}
catch {
    # Controller 未连通时返回 502/500，也标记为已知情况
    if ($_ -match "502" -or $_ -match "500") {
        Write-Info "GET /sessions — Controller 未连通（$_），跳过"
    } else {
        Write-Fail "GET /sessions — 异常: $_"
    }
}

# ============================================================
#  汇总
# ============================================================
Write-Host ""
Write-Host "══════════════════════════════════════════════" -ForegroundColor DarkCyan
$total = $script:pass + $script:fail
$color = if ($script:fail -eq 0) { "Green" } else { "Yellow" }
Write-Host ("  结果：{0} 通过 / {1} 失败 / {2} 合计" -f $script:pass, $script:fail, $total) -ForegroundColor $color
Write-Host "══════════════════════════════════════════════" -ForegroundColor DarkCyan
Write-Host ""

if ($script:fail -gt 0) {
    exit 1
}
