# 批量创建架构改进任务卡 - Part 3
$ErrorActionPreference = "Stop"
$python = "python"
$script = ".github/skills/todo-api/todo_api.py"

function Invoke-CreateTask {
    param($Title, $Priority, $Stage, $Tags, $Goal, $OutOfScope, $Acceptance, $Impact, $EntryPoints, $Risk, $ExecType, $HumanOwner, $Description)
    $cmdArgs = @("create","--title",$Title,"--project","Pudding","--task-owner","WangXianQiang","--priority",$Priority,"--stage",$Stage,"--goal",$Goal,"--out-of-scope",$OutOfScope,"--impact-scope",$Impact,"--risk-notes",$Risk,"--executor-type",$ExecType,"--human-owner",$HumanOwner,"--description",$Description)
    foreach ($tag in $Tags) { $cmdArgs += "--tag"; $cmdArgs += $tag }
    foreach ($ac in $Acceptance) { $cmdArgs += "--acceptance-criteria"; $cmdArgs += $ac }
    foreach ($ep in $EntryPoints) { $cmdArgs += "--entry-point"; $cmdArgs += $ep }
    & $python $script @cmdArgs
}

# ============================================
# 连接器生命周期管理
# ============================================
Invoke-CreateTask -Title "连接器(Connector)生命周期与故障恢复机制文档补全" -Priority "P1" -Stage "ready" `
    -Tags @("architecture","connector","resilience","ClaudeCode对比") `
    -Goal "补充Docs/07架构/04PuddingController与Gateway.md中IPuddingConnector接口的生命周期管理细节：启动失败重试、断线重连、健康检查、优雅关闭" `
    -OutOfScope "不实现新的连接器类型（WebChat/CLI/Email等），仅补充运维文档" `
    -Acceptance @("定义连接器状态机：Stopped→Starting→Running→Degraded→Stopped","定义重连策略：指数退避、最大重试次数、熔断阈值","定义健康检查端点GET /health/connectors/{name}","定义优雅关闭的超时和资源清理顺序") `
    -Impact "Docs/07架构/04PuddingController与Gateway.md" `
    -EntryPoints @("Docs/07架构/04PuddingController与Gateway.md","Source/PuddingCore/Abstractions/IPuddingConnector.cs","参考Claude Code bridge崩溃恢复：Docs/claude-reviews-claude/architecture/13-bridge-system.md","参考.NET resilience模式：Polly库（重试/断路器/超时）") `
    -Risk "连接器的具体重试策略取决于通道类型（WebSocket vs HTTP vs MQTT），需按类型分别定义" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的13-bridge-system.md展示了完整的远程连接生命周期管理——崩溃恢复（重启后幂等重连、环境过期检测、最多3次重建尝试）、JWT生命周期管理（到期前5分钟刷新）、epoch机制（服务器端冲突检测）。我们的Docs/07架构/04PuddingController与Gateway.md定义了IPuddingConnector接口（StartAsync/StopAsync/SendAsync/OperateAsync/GetDiagnosticsAsync），但缺少这些运维关键细节。

## 问题

1. 连接器启动失败后如何处理？（无限重试？告警？回退？）
2. 运行中断线如何检测和重连？（心跳间隔？重连退避策略？）
3. 如何判断连接器"不健康"？（延迟阈值？错误率？）
4. 进程关闭时如何保证连接器优雅退出？（超时？强制终止？）

## 参考来源

- Claude Code bridge崩溃恢复：Docs/claude-reviews-claude/architecture/13-bridge-system.md
- .NET resilience最佳实践：Polly库（https://github.com/App-vNext/Polly）
- Kubernetes健康检查模式：Liveness/Readiness/Startup探针
- 我们的接口定义：Source/PuddingCore/Abstractions/IPuddingConnector.cs

## 具体修改方案

1. 在04文档中新增「连接器生命周期」章节
2. 定义连接器状态机（参考Kubernetes Pod状态）：
   Stopped → Starting → Running → Degraded(降级) → Stopped
3. 定义重连策略表（按通道类型）：
   | 通道类型 | 心跳间隔 | 重试策略 | 最大重试 | 熔断阈值 |
   | WebSocket | 30s | 指数退避(1s-60s) | 10次 | 连续5次失败 |
   | HTTP轮询 | 60s | 固定间隔 | 3次 | 连续3次超时 |
4. 补充健康检查端点和诊断信息格式
"@

# ============================================
# 数据模型完整表结构文档
# ============================================
Invoke-CreateTask -Title "数据模型完整表结构文档 — 补充字段/约束/索引/迁移策略" -Priority "P1" -Stage "ready" `
    -Tags @("architecture","database","SQLite","doc-sync","ClaudeCode对比") `
    -Goal "将Docs/07架构/08数据模型与配置.md从仅有实体名列表扩展为完整的数据库Schema文档，包含字段定义、类型、约束、索引、外键关系和迁移策略" `
    -OutOfScope "不改变现有数据库Schema，仅补充文档" `
    -Acceptance @("每个实体表列出完整字段（名称/类型/约束/默认值/说明）","标注索引（含联合索引）和用途说明","标注外键关系和级联删除策略","定义SQLite迁移策略（EF Core Code First migrations）","标注废弃实体在文档中的[DEPRECATED]状态") `
    -Impact "Docs/07架构/08数据模型与配置.md" `
    -EntryPoints @("Docs/07架构/08数据模型与配置.md","Source/PuddingAgent/Data/（DbContext和Entity定义）","参考Claude Code session持久化：Docs/claude-reviews-claude/architecture/09-session-persistence.md","参考EF Core文档：https://learn.microsoft.com/en-us/ef/core/modeling/") `
    -Risk "文档需与EF Core migrations保持同步，建议在CI中增加Schema文档一致性检查" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的09-session-persistence.md展示了精确的数据持久化文档——每个JSONL条目类型都有明确的字段定义、Parent-UUID链的数据结构图和双重写入路径的异步队列细节。我们的Docs/07架构/08数据模型与配置.md当前仅列出6个实体名称（Agent配置/Session/Message/Memory/PeerNode/AuditLog），缺少字段级定义。

## 问题

1. 新开发者无法从文档了解数据库Schema，必须阅读EF Core代码
2. 未定义索引策略（如Session按时间范围查询的索引）
3. 未定义级联删除策略（删除Workspace时关联的Session/Message如何处理）
4. 未定义数据过期清理策略（PeerNode心跳过期数据如何处理）

## 参考来源

- Claude Code session持久化：Docs/claude-reviews-claude/architecture/09-session-persistence.md（JSONL条目类型、Parent-UUID链、64KB轻量窗口）
- EF Core建模文档：https://learn.microsoft.com/en-us/ef/core/modeling/
- SQLite最佳实践：https://www.sqlite.org/pragma.html
- 我们的架构文档：Docs/07架构/08数据模型与配置.md

## 具体修改方案

1. 为每个实体编写Schema卡片（参考Claude Code JSONL条目文档风格）：
```
## AgentConfig
| 字段 | 类型 | 约束 | 默认值 | 说明 |
|------|------|------|--------|------|
| Id | TEXT | PK | GUID | 配置唯一标识 |
| Name | TEXT | NOT NULL | - | Agent名称 |
...
```
2. 增加「索引策略」章节
3. 增加「外键与级联删除」章节
4. 增加「数据清理策略」章节（PeerNode TTL、AuditLog归档等）
5. 增加「迁移策略」章节
"@

# ============================================
# 权限流水线设计
# ============================================
Invoke-CreateTask -Title "权限流水线设计 — 参考Claude Code 7步纵深防御" -Priority "P1" -Stage "ready" `
    -Tags @("architecture","security","permission","ClaudeCode对比") `
    -Goal "设计并文档化工具调用的权限检查流水线，参考Claude Code的7步纵深防御模型（工具级拒绝→询问→工具特定检查→绕过免疫→模式检查→允许规则→兜底询问）" `
    -OutOfScope "V1不实现完整权限流水线代码，仅完成架构设计和核心接口定义" `
    -Acceptance @("产出权限流水线设计文档（含流程图）","定义IPermissionCheck接口（参考Claude Code的Tool.checkPermissions）","定义至少3种权限模式：Interactive/Plan/Auto","定义权限缓存策略（Session级别的权限记忆）") `
    -Impact "Docs/07架构/（新增13权限与安全.md）" `
    -EntryPoints @("Docs/claude-reviews-claude/architecture/07-permission-pipeline.md","Docs/07架构/12多轮会话与工具调用执行.md（Tool接口已有checkPermissions概念）","Source/PuddingRuntime/Services/AgentExecutionService.cs（当前权限检查位置）") `
    -Risk "当前V1已有基本的工具权限检查（IsReadOnly/IsDestructive），需评估与现有实现的兼容性。权限流水线可能引入性能开销" `
    -ExecType "hybrid" -HumanOwner "WangXianQiang" `
    -Description @"
## 背景

Claude Code的07-permission-pipeline.md描述了一个极其完善的7步权限流水线——从编译时工具过滤到运行时用户询问，每步有明确的决策语义。特别值得借鉴的设计：
- **fail-closed默认值**：忘记声明isConcurrencySafe默认为false
- **绕过免疫安全检查**：即使绕过权限模式，某些安全检查（.git/.claude/目录）仍然强制生效
- **YOLO分类器**：自动模式下用轻量LLM判断是否需要用户确认
- **拒绝断路器**：连续3次或总计20次拒绝触发模式回退

## 问题

当前V1中权限检查较简单——主要在AgentExecutionService中判断IsReadOnly/IsDestructive，缺少：
1. 层级化的权限规则（全局→项目→用户→临时）
2. 权限缓存（同一Session内重复操作不重复询问）
3. 自动模式（对安全操作静默允许）
4. 绕过模式的保护（某些安全检查不可绕过）

## 参考来源

- Claude Code权限流水线：Docs/claude-reviews-claude/architecture/07-permission-pipeline.md
- Claude Code工具系统：Docs/claude-reviews-claude/architecture/02-tool-system.md（checkPermissions接口设计）
- OWASP访问控制最佳实践：https://cheatsheetseries.owasp.org/cheatsheets/Access_Control_Cheat_Sheet.html
- 我们的Tool接口：Source/PuddingRuntime/（ITool.checkPermissions相关实现）

## 具体修改方案

1. 新建Docs/07架构/13权限与安全.md
2. 定义6步权限流水线（适配Pudding的单进程场景）：
   (1)工具级硬拒绝 → (2)工具特定检查 → (3)安全目录检查 → (4)权限模式判断 → (5)允许规则匹配 → (6)用户询问
3. 定义3种权限模式：Interactive（默认）/ Plan（预演）/ Auto（自动允许安全操作）
4. 设计权限记忆缓存结构
"@

Write-Host "Part 3 done!"
