# PuddingController CodeMAP

> 代理控制层 | REST API · 会话路由 · 审批 · 审计 · 工作区

## Controllers（14 个）

| 文件 | 用途 |
|------|------|
| `AgentTemplateController.cs` | Agent 模板管理 |
| `ApprovalController.cs` | 工具审批；**类级 `[Authorize]`**（2026-09-20 发现 X 补）；读接口返回脱敏投影 `ApprovalView`，**不下发 `ConfirmationCode`**；`confirm` 的确认码校验委托 `PuddingCode.Platform.ApprovalCode.Matches`（恒时比较）；`confirm`/`reject` 的审计主体 `ResolvedBy` **优先取认证上下文**（`ClaimTypes.NameIdentifier` → `User.Identity.Name`，2026-09-20 发现 Y 加固），请求体自填姓名仅作回退 |
| `AuditController.cs` | 审计记录 |
| `DebugController.cs` | 调试端点（9KB） |
| `GatewayController.cs` | 网关入口 |
| `GraphController.cs` | 知识图谱 |
| `KnowledgeController.cs` | 知识库 |
| `LlmProxyController.cs` | LLM 代理转发 |
| `MessageIngressController.cs` | 消息入口（飞书等） |
| `RuntimeRegistryController.cs` | 运行时注册 |
| `SessionController.cs` | 会话管理 |
| `StorageController.cs` | 存储 |
| `UserController.cs` | 用户 |
| `WorkspaceController.cs` | 工作区 |

## Services（15 个）

| 文件 | 用途 |
|------|------|
| `SessionRouter.cs` | 会话路由（核心，24KB） |
| `RuntimeDispatcher.cs` | 运行时调度 |
| `RuntimeRegistryService.cs` | 运行时注册服务 |
| `InMemorySessionRepository.cs` | 会话内存存储 |
| `InMemoryWorkspaceCatalog.cs` | 工作区目录 |
| `InMemoryApprovalService.cs` | 审批服务——**进程内 `ConcurrentDictionary`** 实现（2026-09-20 起名副其实）。此前硬依赖**从未注册**的 `IConnectionMultiplexer`，使四个 `/api/approval/*` 端点在已认证请求下恒 500（死接口），现已改为进程内存储并在组合根注册。状态迁移经 `TryUpdate` 比较交换 ⇒ 并发确认只有一次成功；确认码生成/校验委托 `PuddingCode.Platform.ApprovalCode`；确认码失败累计达 `ApprovalCode.MaxFailedAttempts`（10）即把审批单置 `Expired` 作废，**作废后即使提交正确确认码也不放行**。过期记录在写入/查询路径**惰性回收**（避免字典随运行时长单调增长）。代价：状态不跨进程共享（当前单进程部署可接受） |
| `InMemoryAuditEventStore.cs` | 审计存储 |
| `InMemoryRouteDecisionStore.cs` | 路由决策存储 |
| `AuthorizationService.cs` | 授权服务 |
| `AgentTemplateRegistry.cs` | 模板注册 |
| `ControllerLlmProxyService.cs` | LLM 代理服务；严格按 `LlmConfig` 中的模型协议路由 Chat Completions/Responses/Anthropic Messages |
| `GatewayEgressService.cs` | 网关出口 |
| `KnowledgeBaseService.cs` | 知识库服务 |
| `KnowledgeGraphService.cs` | 知识图谱服务 |
| `UnifiedStorageService.cs` | 统一存储 |

## Data & Migrations

| 目录 | 用途 |
|------|------|
| `Data/` | 数据层 |
| `Migrations/` | EF Core 迁移 |

## 测试

—（无独立测试项目，集成在 Platform/WebApi 测试中）

- 授权边界：`PuddingWebApiTests/ApprovalControllerAuthTests.cs`（匿名 3 端点必须 401）
