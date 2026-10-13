# PuddingAgent CodeMAP

> 入口项目（薄壳） | Console / DesktopChild 双模式宿主入口 · DI 组合扩展 · 全部业务实现在 PuddingHost/Runtime/Platform

## 入口

| 文件 | 用途 |
|------|------|
| `Program.cs` | 🔑 薄入口：固定 stdout/stderr 与组合根启动就绪信号（见下「关键语义」） |

## DI 组合扩展

| 文件 | 用途 |
|------|------|
| `Services/PuddingServiceCollectionExtensions.Connectors.cs` | 连接器组合：P2P/mDNS、Webhook/Http/WebSocket/MQTT、Feishu、GatewayAuth 注册（见下「关键语义」） |
| `Services/PuddingServiceCollectionExtensions.Runtime.cs` | Runtime 组合：TaskDelegationPolicy、会话/记忆、JSONL 读写与工具运行时服务（见下「关键语义」） |

## 配置与部署

| 文件 | 用途 |
|------|------|
| `appsettings.json` | 运行配置 |
| `Properties/launchSettings.json` | VS 启动配置 |

## 说明

- 本项目的 `.csproj` 仅引用 `PuddingHost` 并导入其 `PuddingHostContent.props` 共享 content；几乎不直接持有业务代码。
- 业务逻辑分布：`../PuddingHost/`（组合根）、`../PuddingRuntime/`（Agent Loop/工具）、`../PuddingPlatform/`（API/EF Core/网关）。

## 关键语义

### 入口 · `Program.cs`

`Program.cs` 用途原文（逐字保留）：
🔑 薄入口：`--desktop-child` 在首条输出前固定 stdout/stderr 为无 BOM UTF-8，与 Desktop 重定向读取一致；分派 `PuddingHostOptionsFactory`，
委托 `PuddingApplicationHost` 组合根完成构建/初始化/启动；初始化期间每 5 秒输出 `PUDDING_DESKTOP_STARTING` 单调租约，最终输出 `PUDDING_DESKTOP_READY` 
JSON 就绪信号；`partial class Program` 支持 WebApplicationFactory 集成测试

### DI 组合扩展 · `Services/PuddingServiceCollectionExtensions.Connectors.cs`

`Services/PuddingServiceCollectionExtensions.Connectors.cs` 用途原文（逐字保留）：
连接器组合：P2P mDNS 发现、Webhook/Http/WebSocket/MQTT 连接器、Feishu 连接器与流式投影/TTS/图片上传准备、GatewayAuth、`ILlmResolver`、
DirectLlm/HttpFetch/SkillPackageDL/DashScope 等 HttpClient 注册

### DI 组合扩展 · `Services/PuddingServiceCollectionExtensions.Runtime.cs`

`Services/PuddingServiceCollectionExtensions.Runtime.cs` 用途原文（逐字保留）：
Runtime 组合：TaskDelegationPolicy、会话/记忆（MemoryEngine/Library/Librarian/Recall/Fact）、JSONL 会话读写、潜意识处理、Agent 
日志/摘要、ChatTranscript、工具运行时服务注册
