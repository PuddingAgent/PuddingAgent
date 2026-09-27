# PuddingController CodeMAP

> 代理控制层 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| REST API · 会话路由 · 审批 · 审计 · 工作区

2026-09-27：`DependencyInjection.cs` 将 `ISessionRepository` 映射到既有 `InMemorySessionRepository` 单例，供原生 Desktop 的主会话服务和既有 Agent 投影直接读取；不是第二份会话存储。

## Controllers（14 个）

| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 文件 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 用途 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `AgentTemplateController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| Agent 模板管理 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `ApprovalController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 工具审批；**类级 `[Authorize]`**（2026-09-20 发现 X 补）；读接口返回脱敏投影 `ApprovalView`，**不下发 `ConfirmationCode`**；`confirm` 的确认码校验委托 `PuddingCode.Platform.ApprovalCode.Matches`（恒时比较）；`confirm`/`reject` 的审计主体 `ResolvedBy` **优先取认证上下文**（`ClaimTypes.NameIdentifier` → `User.Identity.Name`，2026-09-20 发现 Y 加固），请求体自填姓名仅作回退 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `AuditController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 审计记录 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `DebugController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 调试端点（9KB） | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `GatewayController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 网关入口 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `GraphController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 知识图谱 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `KnowledgeController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 知识库 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `LlmProxyController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| LLM 代理转发 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `MessageIngressController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 消息入口（飞书等） | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `RuntimeRegistryController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 运行时注册 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `SessionController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 会话管理 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `StorageController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 存储 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `UserController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 用户 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `WorkspaceController.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 工作区 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|

## Services（15 个）

| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 文件 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 用途 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `SessionRouter.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 会话路由（核心，24KB） | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `RuntimeDispatcher.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 运行时调度 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `RuntimeRegistryService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 运行时注册服务 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `InMemorySessionRepository.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 会话内存存储 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `InMemoryWorkspaceCatalog.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 工作区目录 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `InMemoryApprovalService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 审批服务——**进程内 `ConcurrentDictionary`** 实现（2026-09-20 起名副其实）。此前硬依赖**从未注册**的 `IConnectionMultiplexer`，使四个 `/api/approval/*` 端点在已认证请求下恒 500（死接口），现已改为进程内存储并在组合根注册。状态迁移经 `TryUpdate` 比较交换 ⇒ 并发确认只有一次成功；确认码生成/校验委托 `PuddingCode.Platform.ApprovalCode`；确认码失败累计达 `ApprovalCode.MaxFailedAttempts`（10）即把审批单置 `Expired` 作废，**作废后即使提交正确确认码也不放行**。过期记录在写入/查询路径**惰性回收**（避免字典随运行时长单调增长）。代价：状态不跨进程共享（当前单进程部署可接受） | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `InMemoryAuditEventStore.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 审计存储 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `InMemoryRouteDecisionStore.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 路由决策存储 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `AuthorizationService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 授权服务 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `AgentTemplateRegistry.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 模板注册 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `ControllerLlmProxyService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| LLM 代理服务；严格按 `LlmConfig` 中的模型协议路由 Chat Completions/Responses/Anthropic Messages | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `GatewayEgressService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 网关出口 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `KnowledgeBaseService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 知识库服务 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `KnowledgeGraphService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 知识图谱服务 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `UnifiedStorageService.cs` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 统一存储 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|

## Data & Migrations

| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 目录 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 用途 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|------| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `Data/` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| 数据层 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|
| `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| `Migrations/` | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
| EF Core 迁移 | `Services/RuntimeNodeAdminService.cs` | 运行时节点冻结/解冻应用操作：把「冻结 + 审计」从 `RuntimeRegistryController` 原位下沉——审计原先只在控制器里写，原生管理面直接调 `RuntimeRegistryService` 冻结节点不留痕，而冻结会拒绝该节点的全部原生能力调用（安全相关）。依赖 `InMemoryAuditEventStore` 具体类型：Controller 的 DI 只注册了具体类型，没有注册 `IAuditEventStore` |
|

## 测试

—（无独立测试项目，集成在 Platform/WebApi 测试中）

- 授权边界：`PuddingWebApiTests/ApprovalControllerAuthTests.cs`（匿名 3 端点必须 401）
