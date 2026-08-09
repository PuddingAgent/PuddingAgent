# PuddingHost CodeMAP

> 唯一 Host 组合根 | Console 与 Desktop 共用 DI · Browser Bridge · 飞书连接器

## 组合根

| 文件 | 用途 |
|------|------|
| `BuiltInAgentTemplates.cs` | 内置 Agent 模板注册 |
| `PuddingHostAssemblyMarker.cs` | 程序集标记 |

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
| `Services/HeartbeatService.cs` | 心跳编排（19KB） |
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

`../Tests/PuddingHost.Tests/` — Browser Bridge Endpoint、Remote proxy 测试（56/56 ✅）
