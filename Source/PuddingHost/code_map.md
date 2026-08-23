# PuddingHost CodeMAP

> 唯一 Host 组合根 | Console 与 Desktop 共用 DI · Browser Bridge · 飞书连接器

## 组合根

| 文件 | 用途 |
|------|------|
| `BuiltInAgentTemplates.cs` | 内置 Agent 模板注册 |
| `PuddingHostAssemblyMarker.cs` | 程序集标记 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 成品 Host 的 Platform/Runtime 组合注册；包含 MOA、V2 component registry/compiler、SQLite store/signal、Admin 手动 Run/HTTP Hook command service、SubAgent/图片生成/展示 executor、临时子代理目录两阶段 GC、hosted worker 与 replay-to-live follower；`TaskAgentCommandService` 与 Singleton `task_*` 工具同生命周期，服务内部每次调用通过 DbContextFactory 创建独立 DbContext；不能只在未被产品入口调用的 Runtime 扩展里注册 worker |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 成品 Host 的 Runtime/Tool 组合注册；assembly scan 自动发现的新工具，其构造依赖也必须在这里注册（例如 `SavePreferenceTool` → `IUserPreferenceService`） |
| `Tools/ImageReaderTool.cs` + `Tools/ImageReaderSourceResolver.cs` | ADR-077 V2 取图工具：`path` 唯一必填（http(s) URL/宿主绝对路径/`artifact://`）；High 权限 ReadOnly\|RequiresNetwork；`mode=auto\|native\|delegate`——auto 优先 typed 图片工具结果回交具备 vision 的调用模型（零辅助 invocation），文本模型或显式 delegate 用 manifest `visionHelperModel` 单次可归因 invocation；URL 有界下载每跳 SSRF 重校验（禁内网）、本地只读、内容哈希稳定 `vision-*` Artifact；错误走 ADR-077 §9.1 稳定码 |
| `Hosting/PuddingApplicationInitializer.cs` | 启动期数据库初始化；包含 AppUsers 头像字段及通用编排 SQLite schema bootstrap，已有数据库也必须幂等升级 |
| `Storage/StorageMaintenanceService.cs` | 🔑 Core 所有的 SQLite/代码索引明细与安全清理；固定语义白名单、服务端预览、批量删除、checkpoint/VACUUM |
| `Controllers/StorageManagementController.cs` | `/api/admin/storage/databases` 分析、清理预览与执行 API |
| `Hosting/StorageManagementAuthorization.cs` | 平台 admin JWT，或 DesktopChild Loopback + ControlToken 的管理策略 |

## Browser Bridge（Phase 2A）

| 文件 | 用途 |
|------|------|
| `BrowserBridge/RemoteBrowserRuntime.cs` | Core 侧 Browser 代理（→ 认证 Bridge） |
| `BrowserBridge/RemoteBrowserContext.cs` | Remote Context 代理 |
| `BrowserBridge/RemoteBrowserPage.cs` | Remote Page 代理 |
| `BrowserBridge/BrowserBridgeServiceCollectionExtensions.cs` | 条件注册（仅 DesktopChild + BrowserAutomationEnabled） |

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
| `Services/ConnectorDeliveryDispatcher.cs` | 投递分发 |
| `Services/MessageGatewayIngress.cs` | 消息网关入口（19KB） |
| `Extensions/` | 扩展注册 |

## 服务治理

| 文件 | 用途 |
|------|------|
| `Services/HeartbeatService.cs` | 当前 Agent 心跳编排；实例提示词后追加自主执行契约。目标拆为 scheduler plugin + WakeAgent command + before-run/context Hook，以 `agent.run.settled` 重新调度并发布 scheduled/skipped/run-linked/completed 事实 |
| `Services/CronSchedulerService.cs` | Cron 调度 |
| `Services/ConfigHotReloadService.cs` | 配置热重载 |
| `Services/IndexPrebuildService.cs` | 索引预构建 |
| `Hosting/PuddingHostOptionsFactory.cs` | DesktopChild 固定 `0.0.0.0:<port>` 启动约束 |
| `Hosting/PuddingServerAddressAccessor.cs` | 全网卡监听地址投影为同端口 Loopback 控制地址 |
| `Hosting/PuddingApplicationHost.cs` | 组合根、Kestrel 地址绑定与本机控制地址捕获 |
| `Config/` | 默认配置 |
| `Prompts/` | 系统提示模板 |
| `P2P/` | P2P 通信 |

## 测试

`../Tests/PuddingHost.Tests/` — Browser Bridge、Remote proxy、Storage 管理与 DesktopChild 产品组合根构建验证；组合根测试显式验证 Singleton `task_*` 工具及其命令服务生命周期；Storage 定向测试 4/4 ✅
