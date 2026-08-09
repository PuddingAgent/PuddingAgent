# PuddingController CodeMAP

> 代理控制层 | REST API · 会话路由 · 审批 · 审计 · 工作区

## Controllers（14 个）

| 文件 | 用途 |
|------|------|
| `AgentTemplateController.cs` | Agent 模板管理 |
| `ApprovalController.cs` | 工具审批 |
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
| `InMemoryApprovalService.cs` | 审批服务 |
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
