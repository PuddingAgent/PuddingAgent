# PuddingHost CodeMAP

> 唯一 Host 组合根 | Console 与 Desktop 共用 DI · Browser Bridge · 飞书连接器

## 组合根

| 文件 | 用途 |
|------|------|
| `BuiltInAgentTemplates.cs` | 内置 Agent 模板注册 |
| `PuddingHostAssemblyMarker.cs` | 程序集标记 |
| `Extensions/PuddingServiceCollectionExtensions.Platform.cs` | 成品 Host 的 Platform/Runtime 组合注册；包含 MOA、V2 component registry/compiler、SQLite store/signal、Admin 手动 Run/HTTP Hook command service、SubAgent/图片生成/展示 executor、hosted worker 与 replay-to-live follower；不能只在未被产品入口调用的 Runtime 扩展里注册 worker |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 成品 Host 的 Runtime/Tool 组合注册；assembly scan 自动发现的新工具，其构造依赖也必须在这里注册（例如 `SavePreferenceTool` → `IUserPreferenceService`） |
| `Tools/ImageReaderTool.cs` | Agent 图片复查工具；首选 manifest 的 `imageReaderModel`，失败时仅降级到具备 `vision` 的 Agent 主模型，不使用全局 vision 排序；文本模型附件预观察同样消费该字段 |
| `Hosting/PuddingApplicationInitializer.cs` | 启动期数据库初始化；包含通用编排 SQLite schema bootstrap |
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
| `Services/HeartbeatService.cs` | 心跳编排；实例提示词后追加高优先级自主执行契约，恢复最近非心跳上下文并推进一个安全步骤 |
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

`../Tests/PuddingHost.Tests/` — Browser Bridge、Remote proxy、Storage 管理与 DesktopChild 产品组合根构建验证；Storage 定向测试 4/4 ✅
