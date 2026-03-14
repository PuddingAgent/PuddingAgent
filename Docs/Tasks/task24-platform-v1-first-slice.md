# Task 24 - Platform V1 首条垂直切片细化任务

状态：`design`
优先级：`P0`
最后更新：2026-03-14

## 1. 目标
把第一条真实垂直切片拆到可直接编码的类/API 级别。

目标链路：

`CLI -> Platform API -> Workspace 路由 -> ServiceSession -> Runtime Agent -> 真实 LLM 回复`

这条切片完成后，系统至少应具备：

- 渠道消息进入 Platform
- Platform 命中 Workspace 与 AgentTemplate
- Platform 自动创建或复用 `ServiceSession`
- Platform 将请求投递到 Runtime
- Runtime 返回真实 LLM 回复
- CLI 可以查询 Session、路由决策、审计事件、Runtime Agent 状态

补充目标：

- 内置支持 Email Channel
- 一个 Workspace 可以挂接多个渠道，并为每个渠道声明默认 Agent 或允许 Agent 集合

## 2. 非目标
第一条切片不包含以下复杂能力：

- 跨工作区共享
- Workflow 版本化
- 多 Runtime 分布式调度
- 第三方插件自动审批
- 复杂预算策略
- 自动热更新 Workspace 配置

## 3. 设计约束
- 路由权威在 `PuddingPlatform`
- 会话权威在 `PuddingRuntime`
- 第一条切片必须走真实 LLM 回复，不接受纯模拟闭环
- Agent 未命中时必须返回明确错误并记录审计事件
- CLI 必须通过 Platform API 进入，不允许继续直连本地运行态主链路
- 渠道接入机制必须插件化，避免后续新增渠道时改动平台核心路由主干

## 4. PuddingPlatform 细化任务

### 4.1 配置与目录加载

#### T24-P-01 `WorkspaceCatalog`
职责：加载并缓存 Workspace 级配置对象。

建议类：
- `WorkspaceCatalog`
- `WorkspaceDefinition`
- `AgentTemplateDefinition`
- `ChannelBindingDefinition`
- `EmailChannelDefinition`
- `PermissionPolicyDefinition`

输入：
- Workspace YAML
- Platform 持久化索引

输出：
- 可供路由层查询的 `WorkspaceDefinition`

验收标准：
- 能加载最小 Workspace 配置对象：`AgentTemplate`、`ChannelBinding`、`PermissionPolicy`
- 能加载内置 Email Channel 配置
- 能按 `WorkspaceId` 和 `ChannelId` 查询配置
- 能读取渠道绑定的 `DefaultAgentTemplateId` 与 `AllowedAgentTemplateIds`
- 配置 reload 后可重新生效

#### T24-P-01A `ChannelProviderCatalog`
职责：注册和发现渠道插件。

建议类：
- `IChannelProvider`
- `ChannelProviderCatalog`
- `ChannelProviderDescriptor`

验收标准：
- 平台可注册内置渠道 Provider，例如 `CliChannelProvider`、`EmailChannelProvider`
- 平台可按 `ChannelType` 查询对应 Provider
- 后续新增渠道不需要修改核心路由接口定义

#### T24-P-02 `WorkspaceReloadService`
职责：执行手动 reload。

建议 API：
- `POST /api/platform/workspaces/reload`

验收标准：
- 手动触发后能够重新加载 Workspace 配置
- reload 结果能返回成功/失败明细

### 4.2 消息接入与身份解析

#### T24-P-03 `MessageIngressController`
职责：提供第一版消息入口。

建议 API：
- `POST /api/messages`

请求模型：
- `ChannelId`
- `UserExternalId`
- `MessageText`
- `MessageType`
- `CorrelationId?`

返回模型：
- `SessionId`
- `MessageId`
- `RouteDecisionId`
- `Reply`

验收标准：
- CLI 能通过该 API 提交消息
- API 能返回可读错误
- 请求会生成审计事件

#### T24-P-03A `ChannelPluginHost`
职责：承载渠道插件生命周期。

建议类：
- `ChannelPluginHost`
- `ChannelPluginLoadResult`

验收标准：
- 平台启动时可装载内置渠道 Provider
- 可列出当前已注册的渠道 Provider 与其能力声明

#### T24-P-04 `ChannelIdentityResolver`
职责：把渠道携带的外部身份映射为平台可识别用户上下文。

建议类：
- `ChannelIdentityResolver`
- `ChannelUserContext`

补充：
- 对 Email Channel，需要支持 `MailboxAddress + SenderIdentity` 解析

验收标准：
- 能根据 `ChannelId + UserExternalId` 生成用户上下文
- Email Channel 能根据邮箱地址、发件人身份生成渠道用户上下文
- 未识别身份时能返回明确失败原因

说明：
- `ChannelIdentityResolver` 应优先通过对应 `IChannelProvider` 提供的身份解析能力完成标准化，再交由平台策略层处理

### 4.3 Workspace 与 AgentTemplate 路由

#### T24-P-05 `WorkspaceRouteResolver`
职责：根据渠道绑定先命中 Workspace。

建议类：
- `WorkspaceRouteResolver`
- `WorkspaceRouteDecision`

规则输入：
- 渠道来源
- 用户身份/角色
- 消息类型
- 关键词或意图分类

验收标准：
- 能命中唯一 Workspace
- 支持按邮箱地址把邮件入口命中到指定 Workspace
- 未命中时返回明确错误并记录审计
- 路由决策可查询

#### T24-P-06 `AgentTemplateRouteResolver`
职责：在命中的 Workspace 内选择目标 AgentTemplate。

建议类：
- `AgentTemplateRouteResolver`
- `AgentTemplateRouteDecision`

验收标准：
- 能根据消息类型和基础意图分类命中 AgentTemplate
- 能优先尊重 `ChannelBinding` 中声明的默认 Agent 和允许 Agent 集合
- 第一版不要求外部直接传 `AgentId`
- 未命中时必须明确报错，不回退默认 Agent

#### T24-P-07 `RouteDecisionStore`
职责：保存消息路由决策，供查询和调试。

建议类：
- `RouteDecisionRecord`
- `RouteDecisionStore`

建议 API：
- `GET /api/routes/{routeDecisionId}`
- `GET /api/messages/{messageId}/route`

验收标准：
- 能查询某条消息命中了哪个 Workspace 和 AgentTemplate
- 能看到命中的是哪个渠道绑定，以及是否通过默认 Agent / 允许 Agent 集合命中
- 能看到未命中的失败原因

### 4.4 ServiceSession 管理

#### T24-P-08 `ServiceSessionService`
职责：自动创建或复用 ServiceSession。

建议类：
- `ServiceSessionService`
- `SessionRecord`
- `SessionRepository`

复用键建议：
- `ChannelId`
- `OwnerUserId`
- `WorkspaceId`
- `AgentTemplateId`
- `SessionType = ServiceSession`

建议 API：
- `GET /api/sessions/{sessionId}`
- `GET /api/sessions?channelId=&userId=&workspaceId=`

验收标准：
- 能按规则自动新建或复用 ServiceSession
- Session 状态可查询
- Session 能显示所属 Workspace、Runtime、Owner、SessionType

### 4.5 权限与审批

#### T24-P-09 `AuthorizationService`
职责：完成第一版最小权限交集校验。

建议类：
- `AuthorizationService`
- `PermissionSnapshot`
- `AuthorizationDecision`

校验输入：
- 用户身份
- WorkspaceRole
- AgentTemplate 权限画像

验收标准：
- 能生成会话级权限快照
- 冲突时默认拒绝
- 拒绝原因可查询

#### T24-P-10 `ApprovalService`
职责：处理第一版高风险动作审批。

建议类：
- `ApprovalService`
- `ApprovalRecord`
- `ConfirmationCodeGenerator`

建议 API：
- `POST /api/approvals`
- `POST /api/approvals/{approvalId}/confirm`
- `GET /api/approvals/{approvalId}`
- `GET /api/approvals?status=pending`

验收标准：
- 高风险动作可生成审批记录
- 支持确认码和过期时间
- 支持 CLI 与 HTTP API 批准

### 4.6 审计与查询

#### T24-P-11 `AuditEventStore`
职责：落盘第一版关键审计事件。

建议类：
- `AuditEventRecord`
- `AuditEventStore`

首批事件：
- 渠道消息进入
- Session 创建/复用
- 路由决策
- 审批请求与结果
- Runtime 投递
- Runtime 回复返回

建议 API：
- `GET /api/audit-events`
- `GET /api/audit-events/{eventId}`

验收标准：
- 审计事件可按 `SessionId`、`MessageId`、`ApprovalId` 查询
- 路由失败和权限拒绝都能看到审计记录

## 5. PuddingRuntime 细化任务

### 5.1 Runtime 会话承载

#### T24-R-01 `SessionRuntimeHost`
职责：承载 Platform 分配过来的 `ServiceSession`。

建议类：
- `SessionRuntimeHost`
- `RuntimeSessionRecord`

建议 API：
- `POST /runtime/sessions/attach`
- `GET /runtime/sessions/{sessionId}`

验收标准：
- Runtime 能接收 Platform 投递的 ServiceSession
- Runtime 能返回 Session 状态与基础元数据

### 5.2 Agent 执行链路

#### T24-R-02 `AgentInstanceFactory`
职责：根据 AgentTemplate 创建 AgentInstance。

建议类：
- `AgentInstanceFactory`
- `AgentInstanceRecord`

验收标准：
- 同一模板可派生普通 Agent 实例
- Agent 实例状态可查询

#### T24-R-03 `AgentExecutionService`
职责：执行单轮真实 LLM 回复。

建议类：
- `AgentExecutionService`
- `AgentExecutionRequest`
- `AgentExecutionResult`

依赖：
- `ILlmGateway`

验收标准：
- 能基于真实模型返回回复
- 执行错误能返回 Platform
- 结果能关联 `SessionId` 与 `AgentId`

### 5.3 技能、记忆与受限执行

#### T24-R-04 `SkillRuntimeBootstrap`
职责：注入第一版最小低风险能力。

验收标准：
- Agent 可获得模板声明的最小内置能力
- 第一版先不要求完整 Skill 热加载

#### T24-R-05 `MemoryBoundaryService`
职责：执行第一版最小记忆边界。

验收标准：
- 至少区分 `SessionMemory` 与 `WorkspaceMemory`
- 公开群/低信任来源默认不能直接写入长期记忆

#### T24-R-06 `SandboxExecutor`
职责：承接高风险动作的受限执行。

建议类：
- `SandboxExecutor`
- `SandboxExecutionRequest`
- `SandboxExecutionResult`

验收标准：
- Shell/进程、文件写入、网络访问可进入受限执行路径
- 未批准的高风险动作不能直接执行

## 6. PuddingCLI 细化任务

#### T24-C-01 `PlatformApiClient`
职责：统一调用 Platform HTTP API。

验收标准：
- CLI 能发送消息、查询 Session、查询审批、查询审计

#### T24-C-02 `SendMessageCommand`
职责：从 CLI 发起首条切片消息。

建议命令：
- `pudding send`

验收标准：
- CLI 发消息后能显示真实 Agent 回复
- 失败时显示可读错误

#### T24-C-03 `SessionStatusCommand`
职责：查询 Session 状态。

建议命令：
- `pudding session show <sessionId>`

验收标准：
- 能看到 SessionType、Workspace、Runtime、当前状态

#### T24-C-04 `ApprovalCommand`
职责：查询和批准高风险动作。

建议命令：
- `pudding approval list`
- `pudding approval approve <approvalId> --code <confirmationCode>`

验收标准：
- CLI 能查询待批准记录
- CLI 能用确认码完成批准

#### T24-C-05 `DebugRouteCommand`
职责：查看消息路由决策。

建议命令：
- `pudding debug route <messageId>`

验收标准：
- 能看到该消息命中了哪个 Workspace 和 AgentTemplate
- 路由失败原因可见

## 7. PuddingAgent 细化任务

#### T24-A-01 `ServiceAgentTemplates`
职责：提供第一版最小内置模板。

建议模板：
- `workspace-service-agent`
- `workspace-task-agent`

验收标准：
- Platform 能命中这些模板
- Runtime 能基于这些模板创建实例

#### T24-A-02 `AgentTemplateProfiles`
职责：补齐模板的权限、运行、记忆画像。

建议字段：
- `CapabilityPolicy`
- `RuntimeProfile`
- `DefaultMemoryPolicy`
- `HeartbeatPolicy`

验收标准：
- Platform 与 Runtime 都能读取这些画像
- 权限与沙箱选择能按模板生效

## 8. 首条切片 API 草案

### Platform API

```text
POST   /api/messages
GET    /api/messages/{messageId}/route
GET    /api/routes/{routeDecisionId}
GET    /api/sessions/{sessionId}
GET    /api/sessions
POST   /api/approvals
POST   /api/approvals/{approvalId}/confirm
GET    /api/approvals/{approvalId}
GET    /api/approvals
GET    /api/audit-events
POST   /api/platform/workspaces/reload
```

### Runtime API

```text
POST   /runtime/sessions/attach
POST   /runtime/agents/execute
GET    /runtime/sessions/{sessionId}
GET    /runtime/agents/{agentId}
```

## 9. 实现顺序（建议）
1. `WorkspaceCatalog` + `MessageIngressController`
2. `ChannelIdentityResolver` + `WorkspaceRouteResolver`
3. `AgentTemplateRouteResolver` + `ServiceSessionService`
4. `SessionRuntimeHost` + `AgentInstanceFactory`
5. `AgentExecutionService`（真实 LLM）
6. `AuditEventStore` + 查询接口
7. `PlatformApiClient` + `pudding send`
8. `AuthorizationService` + `ApprovalService`
9. `SandboxExecutor`

## 10. DoD
以下条件同时满足，Task 24 才算完成：

1. CLI 能通过 Platform API 发消息。
2. Platform 能命中 Workspace 与 AgentTemplate。
3. Platform 能自动创建或复用 `ServiceSession`。
4. Runtime 能创建 AgentInstance 并返回真实 LLM 回复。
5. 路由决策、Session 状态、审计事件、Runtime Agent 状态都可查询。
6. Agent 未命中时返回明确错误并记录审计。
7. 高风险动作可进入 ApprovalRecord 链路，不会直接执行。