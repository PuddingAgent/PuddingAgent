---
title: PuddingHost CodeMAP
author: hyfree
date: 2026-08-07
last_reviewed: 2026-10-02
status: active
description: 飞书 WS 底座在 ../../src/HarnessAgent/Core/Connectors/Feishu/FeishuWebSocket.cs：端点发现 HttpClient 与 WS 握手各 15s 上限，避免外网黑洞把连接器卡在 Starting 100s。
categories: [docs]
tags: [code, map]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-02-PuddingHost-code_map迁出的变更记录.md]
related_files: [Docs/00_changelog/2026Year/10/2026-10-02-PuddingHost-code_map迁出的变更记录.md]
slug: code-map
draft: false
---

# PuddingHost CodeMAP

> 唯一 Host 组合根 | Console 与 Desktop 共用 DI · Browser Bridge · 飞书连接器
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-PuddingHost-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-PuddingHost-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

## 组合根

| 文件 | 用途 |
|------|------|
| `PuddingHostAssemblyMarker.cs` | 程序集标记 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 成品 Host 的 Platform/Runtime 组合注册；内置 Agent 模板直接使用 PuddingCore 唯一权威源；包含 MOA、V2 component registry/compiler、SQLite store/signal、Admin 手动 Run/HTTP Hook command service、SubAgent/图片生成/展示 executor、临时子代理目录两阶段 GC、hosted worker 与 replay-to-live follower；`TaskAgentCommandService` 与 Singleton `task_*` 工具同生命周期，服务内部每次调用通过 DbContextFactory 创建独立 DbContext；不能只在未被产品入口调用的 Runtime 扩展里注册 worker |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 成品 Host 的 Runtime/Tool 组合注册；assembly scan 自动发现的新工具，其构造依赖也必须在这里注册（例如 `SavePreferenceTool` → `IUserPreferenceService`、`SkillEnforcerService` 的可选 `ISkillUsageTelemetrySink`：注册缺失时该可选参数**静默为 null**，不报错也不抛异常；同理 `SearchGrepTool` 的可选 `IFullTextIndexFreshnessProbe` 解析既有 `IFullTextSearchEngine` 实例再 fail-closed 转型，缺注册则索引新鲜度静默不上报） |
| `Tools/ImageReaderTool.cs` + `Tools/ImageReaderSourceResolver.cs` | 原生阅读与预处理：metadata/read/prepare，四档 detail，缩略图、裁剪、90度旋转、灰度、Gaussian降噪、jpeg/png/webp编码；源支持本地/URL/聊天artifact；复用 Platform ImagePreprocessing 和派生缓存，不调用模型/Agent，无 helper 路由。低权限只读源文件，URL每跳SSRF校验；输出源/派生引用供多次区域读取 |
| `Hosting/PuddingApplicationInitializer.cs` | 启动期数据库初始化；包含 AppUsers、WorkspaceTask、TaskPlanning/WorkUnit/AwaitHandle、Goal 及通用编排 SQLite schema bootstrap，已有数据库也必须幂等升级；GoalSchemaBootstrapper 后执行 GoalRestartReconciler 启动 reconcile（按 `goal_runs.resume_policy` 分流：默认 disarm 为 paused；`auto_resume_on_restart` 则保持 Active 并换发 activation fence；输出 disarmed / auto-resumed 计数） |
| `Storage/StorageMaintenanceService.cs` | 🔑 Core 所有的 SQLite/代码索引明细与安全清理；固定语义白名单、服务端预览、批量删除、checkpoint/VACUUM |
| `Controllers/StorageManagementController.cs` | `/api/admin/storage/databases` 分析、清理预览与执行 API |
| `Hosting/StorageManagementAuthorization.cs` | 平台 admin JWT，或 DesktopChild Loopback + ControlToken 的管理策略 |
| `Controllers/IndexAdminController.cs` | `GET /api/admin/index/status`（Admin JWT 只读）**并列两块**：`fullText`（S-A，字段名与结构冻结）+ `codeIndex`（S-A2 新增：逐项目 projectId/displayName/rootPath/注册态/维护态 23 字段/rootPathExists/stale）；D1/D2 的 fail-closed 做进数据（未登记或根路径不存在 ⇒ stale） |
| `Services/CodeIndexStatusProbe.cs` | S-A2：codeIndex 块的只读探针。枚举真源 `ICodeIndexScopeRegistry.ListScopesAsync` ∪ `ICodeIndexMaintenance.GetScopeStatuses`（未登记 scope 也能被看见），原始注册态取自 `ICodeProjectRegistry.ListProjectsAsync`；维护态 23 字段原样透传；只调查询型 API，失败降级为 null/空列表，绝不 500 |
| `Services/FullTextIndexStatusProbe.cs` | S-A：`fullText` 块探针（只产块，不再产响应根对象）；响应根对象 `IndexAdminStatusSnapshot` 由控制器组装 |
| `Controllers/TransportStatusController.cs` | 切片 F 的**退役判据证据面**：`GET /api/admin/transport/status`（Admin JWT）只读返回传输用量 —— 能力通道/旧 Bridge/无路由计数 + `channelProven` + `canRetireLegacyBridge`；浏览器自动化未启用时计数器未注册 ⇒ 如实 `available = false` |

## Browser Bridge（Phase 2A）

| 文件 | 用途 |
|------|------|
| `BrowserBridge/RemoteBrowserRuntime.cs` | Core 侧 Browser 代理（→ 认证 Bridge） |
| `BrowserBridge/RemoteBrowserContext.cs` | Remote Context 代理 |
| `BrowserBridge/RemoteBrowserPage.cs` | Remote Page 代理 |
| `BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs` | 条件注册（仅 DesktopChild + BrowserAutomationEnabled）；同时注册**窄端口**（路由实现）与能力调用上下文工厂 |

| `BrowserBridge/BridgeBrowserCapabilitySurface.cs` | 窄端口的 **Bridge 实现**：把能力形状的请求翻译到 `IBrowserRuntime`，与能力通道实现**同形**（版本门禁 7/7、交互前解析元素、结果事实不升级为失败） |

| `BrowserBridge/TransportRoutedBrowserCapabilitySurface.cs` | 组合根按 `DesktopTransportRouting` **二选一**（通道就绪走通道，否则 Bridge，两者都不可用则如实失败）；同文件含 `DesktopTransportUsageTracker`（退役判据计数 + `MissingEvidenceCalls` 观测）。硬要求：**同一次操作绝不执行两次**；**副作用类能力要求权限证据**（缺失或 `denied` ⇒ `Unauthorized`，只读能力不受影响；见 `Docs/12_features/桌面能力链路权限证据设计-2026-10-02.md`） |

| `Hosting/ConnectedDesktopCallContextFactory.cs` | 能力调用上下文工厂实现：Desktop 实例 ID 有两个**真实来源**（活动能力通道会话 → Bridge 当前连接），都没有则返回 `null`（不猜实例 ID） |

## 飞书连接器

| 文件 | 用途 |
|------|------|
| `Services/FeishuConnectorFactory.cs` | 飞书连接器工厂 |
| `Services/FeishuStreamingProjectionWorker.cs` | 飞书流式投影（31KB） |
| `Services/FeishuImageUploadPreparationService.cs` | 飞书图片上传准备 |
| `Services/FeishuTtsDeliveryService.cs` | 飞书 TTS 投递 |
| `Services/FeishuConnectorIdentity.cs` | 飞书身份标识 |

## 连接器 & 消息

| 文件 | 用途 |
|------|------|
| `Connectors/` | 连接器实现 |
| `Services/ConnectorHost.cs` | 连接器宿主 |
| `Hosting/ConnectorHostLifecycleService.cs` | 连接器生命周期 hosted service：本地注册同步、`StartAllAsync` 后台执行（ApplicationStopping 绑定），Ready 不被 Feishu WS 握手阻塞；单连接器失败隔离进 Faulted |
| `Services/ConnectorDeliveryDispatcher.cs` | 投递分发 |
| `Services/MessageGatewayIngress.cs` | 消息网关入口（19KB） |
| `Extensions/` | 扩展注册 |

飞书 WS 底座在 `../../src/HarnessAgent/Core/Connectors/Feishu/FeishuWebSocket.cs`：端点发现
HttpClient 与 WS 握手各 15s 上限，避免外网黑洞把连接器卡在 Starting 100s。

## 服务治理

| 文件 | 用途 |
|------|------|
| `Services/HeartbeatService.cs` | 当前 Agent 心跳编排（类名 `HeartbeatOrchestrator`，与文件名不一致；日志分类字符串亦为 `[HeartbeatOrchestrator]`）。启动时 + 每次空闲 tick 对**全部**「启用 + 未冻结 + 已绑定主会话」的 Agent 幂等补全登记（2026-09-20；此前只登记单个“默认 Agent”，且“队列为空才补全”不可达，导致其余 Agent 永远没有心跳）；实例提示词后追加自主执行契约；2026-08-26 增加持久 Availability gate，等待 SubAgent/Task/Goal、消息排队、Reservation、Unknown 或重建失败均跳过并重新排队，避免把 runtime 暂停误判为空闲 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 组合 Goal outbox/settlement workers、Task-bound 原子 Store、Availability/Reservation/Dependency/Window/Auto Worker；authoritative flag 前置条件 ValidateOnStart |
| `Services/CronSchedulerService.cs` | Cron 调度 |
| `Services/ConfigHotReloadService.cs` | 配置热重载 |
| `Services/IndexPrebuildService.cs` | 索引预构建 |
| `Hosting/PuddingHostOptionsFactory.cs` | DesktopChild 固定 `0.0.0.0:<port>` 启动约束 |
| `Hosting/PuddingServerAddressAccessor.cs` | 全网卡监听地址投影为同端口 Loopback 控制地址 |
| `Hosting/PuddingApplicationHost.cs` | 组合根、Kestrel 地址绑定与本机控制地址捕获；在发布包 appsettings 默认之上加载 `<DataRoot>/config/system.json` 与 `<DataRoot>/config/security.json`（登录态 JWT 密钥/有效期来源）并支持 hot reload，环境变量/命令行仍为最高优先级；JWT 密钥缺失即启动失败（无硬编码兜底）；注册 ADR-075/082 External Token 的 tasks/workspaces/agents/messages scope+workspace Policies |
| `Hosting/PuddingDataRootBootstrapper.cs` | DataRoot 解析与引导：复制 default-data 缺省文件、建运行时目录、确保 `config/security.json` 存在可用的 JWT 签名密钥（缺失/过短/占位符时生成 48 字节 CSPRNG 密钥并原子写回，其余字段保留）、建默认 Agent 实例 |
| `Config/` | 默认配置 |
| `Prompts/` | 系统提示模板 |
| `P2P/` | P2P 通信 |

## 测试

`../Tests/PuddingHost.Tests/` — Browser Bridge、Remote proxy、Storage 管理与 DesktopChild 产品组合根构建验证；组合根测试显式验证 Singleton `task_*` 工具及其命令服务生命周期；Storage 定向测试 4/4 ✅
