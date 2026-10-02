# PuddingAgent 模块清单（module_map）

> 日期：2026-09-18｜作者：dsh（`default.global_general-assistant.0e0`）
> 背景：用户要求「先列出 PuddingAgent 有几个模块组成」。仓库根的 `code_map.md`（639 行，55KB）**实为按日期倒序的变更日志，不含模块清单** ⇒ 本文补齐该缺口。
> 数据来源：`temp/modules.ps1` 全量扫描（`temp/modules.txt`）+ `temp/arch-metrics{.ps1,2}` + Explorer 子代理 ProjectReference 盘点。

---

## 零、先对齐口径：**三个数字都对，取决于你数什么**

| 口径 | 数字 | 说明 |
| --- | --- | --- |
| **顶层工程目录** | `Source/` **32 个** · `Tests/` **10 个** | 含 `build/`、`tmp/`、`.pudding/`、`e2e/` 等非工程目录 |
| **C# 工程（csproj）** | **38 个** = `Source/` **29** + `Tests/` **9** | 29 中含 1 个嵌套夹具 `CodeIndexer.Cli/TestFixtures/MiniProject` |
| **C# 工程按性质** | 生产 **20** · 测试 **8** · 夹具 **1**（Source 内）· 测试/工具 **9**（Tests 内） | 另有基准工程 `MemoryEngineBenchmarks` 计入生产侧 |
| **非 C# 工程** | **1 个**：`PuddingPlatformAdmin`（前端：`src/ public/ dist/ docker/ e2e/ types/`）| 无 csproj、不在 slnx |
| **外部 C# 源根** | **3 套**：`Source/`、`src/HarnessAgent/Core`、`external/github.hyfree.GM` | 后两者被 `PuddingHost.csproj:38,39` 引用 |
| **功能模块（域级）** | **约 24 个**（见 §四） | 用于架构排查的「模块」口径 |

---

## 一、`Source/` 生产工程（20）

| # | 项目 | .cs | raw LOC | 子目录（模块内结构） | 职责 |
| --- | --- | --- | --- | --- | --- |
| 1 | **PuddingAgent** | 1 | 107 | — | **唯一可发布 Web 入口**（`Program.cs`）|
| 2 | **PuddingHost** | 77 | 15,022 | Hosting, Extensions, Connectors, Services, Controllers, BrowserBridge, Tools, P2P, Storage, Build | **组合根**（DI 420 次 / HostedService 34）|
| 3 | **PuddingPlatform** | 457 | 112,494 | Services(252), Controllers(78), Migrations(33), Middleware, Utils, Models | Web API + 业务服务（最大工程）|
| 4 | **PuddingRuntime** | 300 | 73,812 | Services(166), Tools(124), Controllers(4), Models | 执行内核（Agent 循环/上下文/工具）|
| 5 | **PuddingCore** | 357 | 44,148 | Abstractions(93), Platform(58), Models(45), Core(22), Runtime(20), Tasks(14), Tools(14), Goals(12), Configuration(11), Orchestration(11), Skills(11), Swarm(10), Observability(9), Scheduling(8), … | 契约 + **大量实现**（名为 Core）|
| 6 | **PuddingController** | 37 | 4,400 | Controllers(14), Services(15), Migrations(3) | 早期「控制器/网关主机」（与 Platform 重叠）|
| 7 | **PuddingDesktop** | 83 | 14,420 | Core(14), Runtime(12), Browser(11), Storage(10), Configuration(6), Debug(6), Bootstrap(5), Views(5), Hosting(4), ViewModels(3), Theming(2) | 桌面宿主 UI（WinExe）|
| 8 | **PuddingMemoryEngine** | 36 | 11,403 | Entities(11), Services(8), Infrastructure(2), Schema | 记忆引擎（SQLite/EF）|
| 9 | **PuddingCodeIntelligence** | 47 | 7,669 | Contracts(20), Services(10), CSharp/TypeScript/Python/Cpp/Lsp/Markdown/Yaml/Json/Bicep/PowerShell | 多语言代码索引与符号 |
| 10 | **PuddingFullTextIndex** | 8 | 1,286 | Contracts(2), Infrastructure(5) | Lucene + Jieba 全文索引 |
| 11 | **PuddingBrowser.Abstractions** | 8 | 632 | — | 浏览器抽象 |
| 12 | **PuddingBrowser.AgentTools** | 12 | 958 | — | 浏览器 Agent 工具 |
| 13 | **PuddingBrowser.Protocol** | 8 | 439 | — | 浏览器协议 |
| 14 | **PuddingBrowser.WebView2** | 9 | 1,299 | — | WebView2 实现 |
| 15 | **PuddingGateway** | 5 | 279 | Adapters(3), Models(1) | 网关/适配器 |
| 16 | **PuddingCodexService** | 9 | 971 | Services(5), Models(1), Tools(1) | Codex 服务 |
| 17 | **PuddingCodeIndexer.Cli** | 3 | 676 | Scripts, pub, TestFixtures | 索引 CLI（内含夹具工程）|
| 18 | **PuddingTaskRecall.Cli** | 2 | 1,061 | — | 任务回忆 CLI |
| 19 | **PuddingMemoryEngineBenchmarks** | 2 | 608 | BenchmarkDotNet.Artifacts | 基准测试 |
| 20 | **PuddingGit.Tools** | **0** | **0** | — | ⚠️ **空壳工程**（见 §五）|

## 二、`Source/` 测试工程（8）+ 夹具（1）

| 项目 | .cs | raw LOC | 备注 |
| --- | --- | --- | --- |
| PuddingPlatformTests | 180 | 49,756 | Services(151), Controllers(16), Security(6) |
| PuddingRuntimeTests | 151 | 46,873 | Services(102), Tools(48), Architecture(1) |
| PuddingCoreTests | 89 | 20,759 | Vision(12), Tools(8), Swarm(7), Runtime(7), Orchestration(7) |
| PuddingMemoryEngineTests | 20 | 6,711 | |
| PuddingWebApiTests | 22 | 5,905 | Tools(3)；含 `CustomWebApplicationFactory` |
| PuddingCodeIntelligenceTests | 19 | 2,393 | |
| PuddingFullTextIndexTests | 1 | 1,151 | |
| PuddingCodexServiceTests | 2 | 263 | |
| *（夹具）* MiniProject | 2 | — | `CodeIndexer.Cli/TestFixtures/` |

## 三、`Tests/` 目录（9 个 C# 工程 + 1 个非工程目录）

| 项目 | .cs | raw LOC | 归属域 |
| --- | --- | --- | --- |
| PuddingDesktop.Tests | 34 | 5,856 | 桌面（Debug(8), Storage(6), Browser(5), Core(5), Runtime(5)）|
| PuddingHost.Tests | 26 | 3,651 | 组合根（Hosting(12), BrowserBridge(11)）|
| PuddingAgent.IntegrationTests | 8 | 2,589 | 集成（Feishu(8)）|
| HarnessAgent.Core.Tests | 5 | 1,097 | HarnessAgent（Feishu(5)）|
| PuddingBrowser.AgentTools.Tests | 2 | 904 | 浏览器工具 |
| Mcp.Cli | 1 | 779 | MCP CLI（被 PlatformTests 引用）|
| HarnessAgent.Cli | 1 | 341 | HarnessAgent CLI |
| PuddingBrowser.WebView2.Smoke | 1 | 209 | 冒烟 |
| PuddingBrowser.TestSite | 1 | 59 | 测试站点 |
| *e2e（非工程目录）* | 0 | 0 | 端到端脚本 |

## 四、功能模块（域级，约 24 个）——**架构排查的实际对象**

| # | 功能模块 | 主要落点（工程/目录） | 规模信号 |
| --- | --- | --- | --- |
| 1 | **LLM 资源池 / 路由 / 成本** | Core/Abstractions + Runtime/Services + Platform/Services | 契约 4 个接口 + 实现 12+ 类（见细粒度排查规划 B1）|
| 2 | **记忆引擎** | PuddingMemoryEngine + Core/Abstractions | 36 文件 11.4k；`MemoryEngine.cs` 748 行 |
| 3 | **记忆库管理（Book/Chapter/Admin）** | Platform/Services + Platform/Controllers | `MemoryLibraryAdminService/Controller` |
| 4 | **上下文与压缩** | Runtime/Services（ContextPipeline 3 文件 + Window 1553 行 + Compaction 1776 行）| 14 核心类 |
| 5 | **会话与消息** | Platform/Services + Runtime/Services/Messaging | Session/Conversation/MessageFabric/Jsonl |
| 6 | **工具生态** | Runtime/Tools(124) + Core/Tools(14) + Host/Tools(7) + Git.Tools(空) + Browser.AgentTools(12) | 工具族 + `Smart*Tool` |
| 7 | **审批与安全** | Runtime/Tools/Approval + Core/Security + Host Config | `InMemoryToolApprovalService` 1952 行 |
| 8 | **子代理与 Swarm** | Core/{SubAgents,Swarm} + Platform/Services/SubAgentManager | 8 Worker 之一族 |
| 9 | **Goal 与任务调度** | Core/{Goals,Tasks,Scheduling,Orchestration} + Platform/Services/{Goals,Scheduling,Tasks} | 同域 6 Goal Store + 5 Scheduling Store |
| 10 | **事件与投递** | Runtime/Services/{Events,Messaging} + Core/Events | `MessageDeliveryDispatcher` 2208 行 |
| 11 | **心跳 / 空闲 / 潜意识自动化** | Host/Services/HeartbeatService + Runtime(IdleDetector/Subconscious) | 25.7KB HeartbeatService |
| 12 | **连接器与外部集成** | Host/Connectors(6) | HTTP/WebSocket/MQTT/Webhook + Feishu |
| 13 | **P2P 网络** | Host/P2P(1) + Runtime | `IP2pDiscoveryService` |
| 14 | **存储管理 / 保留策略** | Platform/Services/StorageManagement | Sampler/CleanupExecutor/Retention |
| 15 | **代码智能与检索** | PuddingCodeIntelligence + PuddingFullTextIndex + CodeIndexer.Cli | 47 + 8 + 3 文件 |
| 16 | **浏览器桥与浏览器工具** | Host/BrowserBridge(10) + Browser.* (4 工程) | |
| 17 | **桌面宿主 UI** | PuddingDesktop(83) | `async void` 40 处 |
| 18 | **Web API / 控制器** | Platform/Controllers(78) + Controller/Controllers(14) + Host/Controllers(8) | ~98 Controller 类 |
| 19 | **网关与外部协议** | PuddingGateway + Platform/Controllers/External/V1 | ACP/JSON-RPC、ExternalTask/Token/WorkspaceAgent |
| 20 | **可观测与诊断** | Core/{Observability(9),Diagnostics(2)} + Platform/Services/Diagnostics | TokenUsage 闭环 |
| 21 | **配置与偏好 / KeyVault** | Core/Configuration(11) + Platform(KeyVault 相关）| `PuddingFileLlmConfigService` |
| 22 | **技能与提示词** | Core/Skills(11) + Host/Prompts + Platform/Prompts | SkillRegistry / SkillPackage |
| 23 | **Codex 服务** | PuddingCodexService | 9 文件 |
| 24 | **平台前端** | **PuddingPlatformAdmin**（Vue/TS，无 csproj）| `src/ public/ dist/ docker/ e2e/` |

> 另有 2 个**边界外但仍在本仓**的模块：`src/HarnessAgent/Core`（被 Host 引用）· `Tests/Mcp.Cli`（被 PlatformTests 引用）。

---

## 五、清单一致性：**4 处失真（本次实测）**

| # | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| 1 | **解决方案清单漂移** | `PuddingAgentNetwork.slnx`（43 行）仅登记 **18 生产 + 7 测试**；实际 `Source` 29 + `Tests` 9 ⇒ 漏 `PuddingCodeIndexer.Cli`、`PuddingGit.Tools`、`PuddingFullTextIndexTests`、`MiniProject`、`HarnessAgent.Cli`、`Mcp.Cli` | IDE/CI 视图与实际不一致，新人不清楚「到底有几个模块」 |
| 2 | **前端工程游离** | `Source/PuddingPlatformAdmin/` 有完整前端结构（`src/ public/ dist/ docker/ e2e/ types/ scripts/`）但**无 csproj、不在 slnx** | 全仓「Web 面」实际有 3 套：`PuddingPlatform`(API) + `PuddingPlatformAdmin`(前端) + `PuddingDesktop`(桌面) |
| 3 | **空壳工程** | `Source/PuddingGit.Tools/PuddingGit.Tools.csproj`（23 行，引 `LibGit2Sharp` + `PuddingCore`）但目录内 **0 个 `.cs` 文件**；且**没有任何工程引用它**（邻接表中无入边）| 死工程/半成品；其声明的「Structured Git tools（status/log/diff）」实际能力落在别处 |
| 4 | **`code_map.md` 不含模块清单** | `code_map.md` 639 行 = 按日期倒序的变更日志（最新条目 2026-09-17，最早可追溯到 09-12 及更早）| 「到底有几个模块」无处可查 ⇒ 本文补齐 |

**顺带记账**：`Source/build/`、`Source/tmp/`、`Source/_search_prompts.ps1`、`Source/.pudding/`、`PuddingPlatformAdmin/{dist,dist-dev,.tmp,.tmp-test-out,public}` 等属生成物/临时物；`Source/PuddingAgent/temp/` 曾出现非代码数据目录。

---

## 六、与 `code_map.md` 的关系与建议

- `code_map.md` = **变更日志**（Who/When 改了什么 + 关联报告链接），价值在于「历史决策索引」；
- 本文 = **模块清单**（What exists now），价值在于「结构视图」；
- **建议**：把本文的 §一/§二 精简成一张表插入 `code_map.md` 顶部（或维护独立 `module_map.md`），并在 §五 的 4 处失真修好后同步更新。

**报告口径声明**：`.cs` 计数与 raw LOC 由本机 `temp/modules.ps1` 实测（排除 `obj/bin/node_modules`；LOC=原始行数含空行/注释/生成代码）；ProjectReference 邻接来自 Explorer 子代理逐 csproj 取证；未编译验证。
