# PuddingAgent 代码地图（根索引）

> 源指纹: Source/**=869bcd5ee8b0, Tests/**=b0411cf64a03, Tools/**=f27330dfc0b7, TestScripts/**=46b63da8b4e5 · 条目数: 93 · 最近整理: 2026-10-07
> 本文件是**索引**不是日志：只放 概念 · 组件 · 关键文件（相对路径）· 用途；维护规则见 [`Agents.md`](Agents.md)，过程记录写 [`Docs/00_changelog/`](Docs/00_changelog/README.md)；子项目内部的文件级索引在其自己的 `code_map.md` 中，本文件只做跨项目路由。

## 0. 怎么用（30 秒）

- 找子项目 → §2；找概念或机制的权威位置 → §3；找端到端数据流与契约边界 → §4；找测试 → §5；找设计文档 → §6；找运行时目录与构建入口 → §7。
- 「为什么这么改、哪一轮做的」→ `Docs/00_changelog/`（修改日志唯一去处）· `Docs/14_reports/`（诊断与验收报告）；改完代码只更新受影响条目。

## 1. 项目定位

Pudding — Windows First 的 .NET 10 桌面智能助手与 IDE：六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

- 产品入口 `PuddingDesktop.exe` = **WinUI 3 Shell + WebView2 承载既有 Web UI**，以子进程方式启动并监督独立 ASP.NET Core。
- 业务逻辑、Agent、Connector、数据库、Runtime 全在 **Core**（`core/PuddingAgent.exe --desktop-child`）；Shell 只负责窗口、系统集成与进程监督。
- Console 入口只用于开发/诊断；`dev-up.py`（本体 `Tools/Dev/dev-up.py`，根目录为转发 shim）只服务源码开发调试，不进入交付包；架构第一原则与仓库纪律见 [`Agents.md`](Agents.md)。

## 2. 子项目索引

> 顺序：产品与运行时 → Desktop ↔ Core 能力通道 → 代码索引与检索组件族 → 浏览器自动化；每个项目一行，细节在该项目自己的 `code_map.md`，完整程序集清单见 [`PuddingAgentNetwork.slnx`](PuddingAgentNetwork.slnx)。

| 项目 | 用途 | 文件级索引 |
|------|------|-----------|
| `Source/PuddingDesktop/` | WinUI 3 Shell：承载 WebView2 工作台并监督 Core 子进程生命周期 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingAgent/` | 入口 `Program.cs`：Console / DesktopChild 两种薄壳 | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingHost/` | Core 组合根：装配 HTTP 宿主、本机控制地址与各连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingRuntime/` | Agent Loop · LLM 网关调用 · 工具系统 · 上下文管线 · 子代理 · 后台学习 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingCore/` | 跨层抽象与契约：接口与基础模型类型（含 Goal 与编排） | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | Session · Web 与外部 API · EF Core + SQLite · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | Library/Book/Chapter 记忆库 · FTS5 · 潜意识检索 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingMemoryEngineBenchmarks/` | 记忆库与记忆召回的 BenchmarkDotNet 基准工程（不参与运行时装配） | — |
| `Source/PuddingGateway/` | LLM 服务商网关适配：openai / responses / anthropic 三协议 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端（Chat 工作台与任务看板等页面）；`dist` 部署到 Core `wwwroot/admin` | [code_map](Source/PuddingPlatformAdmin/code_map.md) |
| `Source/PuddingTaskRecall.Cli/` | 历史脏数据诊断/修复 CLI（默认 dry-run，`--apply` 才写库） | [code_map](Source/PuddingTaskRecall.Cli/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 工具声明（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/HarnessAgent.Core/` | Harness Agent 核心库（与主线并列的 Harness 实现） | — |
| `Source/Pudding.Contracts/` | 契约叶（仅 BCL）：能力目录、握手协商与错误语义的 DTO | [code_map](Source/Pudding.Contracts/code_map.md) |
| `Source/Pudding.Rpc.Protocol/` | wire 协议叶：`Protos/desktop_capability.proto` 与生成类型 | [code_map](Source/Pudding.Rpc.Protocol/code_map.md) |
| `Source/Pudding.CapabilityBroker/` | Core 侧能力 Broker：握手协商与会话世代的命令关联入口 | [code_map](Source/Pudding.CapabilityBroker/code_map.md) |
| `Source/Pudding.CapabilityBroker.AspNetCore/` | Core 侧宿主适配：gRPC 服务与服务端流及 Kestrel 装配 | [code_map](Source/Pudding.CapabilityBroker.AspNetCore/code_map.md) |
| `Source/Pudding.DesktopConnection/` | Desktop 侧 gRPC 双向流适配：连接状态机与命令关联（含取消与重连） | [code_map](Source/Pudding.DesktopConnection/code_map.md) |
| `Source/Pudding.DesktopService/` | Desktop 侧能力服务：准入与 UI 调度边界及宿主装配 | [code_map](Source/Pudding.DesktopService/code_map.md) |
| `Source/PuddingDesktop.CapabilityHost/` | WinUI 侧平台适配：`DispatcherQueue` 调度实现（只引用 Contracts） | [code_map](Source/PuddingDesktop.CapabilityHost/code_map.md) |
| `Source/Pudding.DesktopSurface.Browser/` | 浏览器侧 Desktop 表面与目标注册表桥；不含 UI 代码，可脱 UI 测试 | [code_map](Source/Pudding.DesktopSurface.Browser/code_map.md) |
| `Source/PuddingDesktop.Foundation/` | BCL-only 布局与外观偏好（`ShellLayout`、`WorkbenchAppearance`） | [code_map](Source/PuddingDesktop.Foundation/code_map.md) |
| `Source/PuddingDesktop.WpfArchive/` | 旧 WPF 入口/验证基线；无 UI 的启动器与协议源文件由 WinUI 项目链接编译 | [code_map](Source/PuddingDesktop.WpfArchive/code_map.md) |
| `Source/Pudding.Rpc.IpcProbe/` | 真实端点技术探针（Named Pipe / h2c 服务端替身，退出码 0/1） | — |
| `Source/PuddingCodeIndex/` | 索引组件：契约与变更捕获管线及范围注册；不得引用 `PuddingCodeIntelligence` | [code_map](Source/PuddingCodeIndex/code_map.md) |
| `Source/PuddingContextPolicy/` | 上下文容量与压缩候选的**纯策略**组件（BCL-only，`ProjectReference`/`PackageReference` 均为 0）：容量算术、候选边界与指纹、严格适用校验、净收益准入、退避判定；不负责计时器/数据库/网络/锁/后台任务。**S1–S4 已交付，尚未 S5 接入**（未登记 slnx、未进 DI） | [code_map](Source/PuddingContextPolicy/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 语言智能与查询：Roslyn 与 TypeScript 抽取及 outliner 与符号查询 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingIndexChunking/` | 分块组件：源文件 → 可独立索引的块（叶子，outline 由 `IOutlineSource` 注入） | [code_map](Source/PuddingIndexChunking/code_map.md) |
| `Source/PuddingPathFiltering/` | 路径忽略合同的权威实现（噪声目录名 + `.gitignore` 语义） | [code_map](Source/PuddingPathFiltering/code_map.md) |
| `Source/PuddingVectorIndex/` | 向量索引组件：文本 → 向量 → 内存有界 top-k（叶子，`IEmbeddingProvider` 端口） | [code_map](Source/PuddingVectorIndex/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎（Lucene）与供给协调 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingFullTextIndex.Cli/` | 全文索引供给的离线驱动工具（`plan` / `status` / `build` / `cancel`） | [code_map](Source/PuddingFullTextIndex.Cli/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingRetrievalEval/` | 检索评测组件（叶子，只依赖 `ISearchProbe` 端口） | [code_map](Source/PuddingRetrievalEval/code_map.md) |
| `Source/PuddingRetrievalEvalProbe/` | 检索评测的真实探针入口：语料构建 + Lucene/向量/RRF 混合检索的可复现测量 | — |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约（驱动与 Agent 工具共用） | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议 | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver（DOM/元素/页面操作） | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 `browser_*` Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.WinUI/` | WinUI 浏览器表面宿主（与 WPF 适配层共享驱动源文件） | [code_map](Source/PuddingBrowser.WinUI/code_map.md) |
| `Source/PuddingBrowser.Automation/` | 🔑 浏览器自动化可靠性组件（叶子，只引用 `Pudding.Contracts`）：统一控制权状态机、页面授权与执行租约、证据化回执判定、operationId 账本；**S1–S4 阶段、尚未登记解决方案** | [code_map](Source/PuddingBrowser.Automation/code_map.md) |
| `Source/PuddingDiagnostics/` | 🔑 可诊断基础设施叶子组件：稳定因果码/分类器/有界证据/脱敏/事故投影/故障场景；不得引用任何 Pudding 程序集 | [code_map](Source/PuddingDiagnostics/code_map.md) |

## 3. 关键概念与组件

| 概念 | 权威位置 | 要点 / 不变量 |
|------|----------|----------------|
| **Turn 执行与上下文管线** | `Source/PuddingRuntime/Services/` | 一次请求 = 一个 canonical Turn；`ContextPipeline`/`ContextWindowManager`/`ContextCompactionService` 分层组装 + 绝对窗口压缩；`AgentExecutionService` 用 `[CURRENT USER TURN input_sha256=…]` 围栏本轮输入，缺失即 fail-closed |
| **Message Fabric / send_message** | `Source/PuddingPlatform/Services/` | `MessageDeliveryPolicy` + `MessageDeliveryDispatcher`：inform/report_result 只通知，仅 ask/request_review/delegate 执行；`agent_reply` 永远被动 |
| **Task 自动派发 / Goal / WorkUnit** | `Source/PuddingPlatform/Services/Scheduling/` | availability → backlog 精炼 → Plan/WorkUnit 编译 → 五态跟踪；promotion/start/repair 只有一个 CAS 写入者 |
| **工具系统与审批** | `Source/PuddingRuntime/Tools/` + [自动审批设计](Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md) | 复杂任务前三次内进入 `smart_*` 或 `spawn_sub_agent`；只读/构建秒放、危险秒拒、未知形态交 LLM 审批 |
| **代码索引组件族** | §2 + [ADR-089](Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) | 依赖方向由编译期强制；叶子组件 `ProjectReference` = 0；边界由 `ComponentBoundaryTests` 三重断言 |
| **Desktop ↔ Core 能力通道** | §2 + [能力通道](Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) | 两端只经 `Pudding.Contracts`（DTO）与 `Pudding.Rpc.Protocol`（wire）；启用前必须过 `DesktopCapabilityChannelPreflight` |
| **Core 启动与就绪契约** | `Source/PuddingHost/Hosting/`、`Source/PuddingDesktop/Hosting/` | 每 5s 发 `PUDDING_DESKTOP_STARTING`，全部 hosted service `StartAsync` 返回后才发 `PUDDING_DESKTOP_READY`；`/health/ready` 不可省 |
| **浏览器自动化链路** | `Source/PuddingHost/BrowserBridge/`、`Source/PuddingBrowser.WebView2/` | Snapshot ref 必须携带 `PageVersion`；交互提交后不得重查旧 Locator，后续状态用 Wait 或新 Snapshot |
| **浏览器控制权与页面授权** | `Source/PuddingBrowser.Automation/` + [设计方案](Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md) | 进程内**唯一** `IBrowserAutomationAuthority`：接管/暂停/关闭/世代改变推进控制世代，接管**不**撤销阅读授权；页面授权按 `read/write/manage` 范围绑定实例+世代+页面+frame，子代理只能取父任务子集；写请求入队签发租约、触碰页面前复检世代 |
| **多模态视觉链路** | `Source/PuddingRuntime/` + [ADR-077](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) | typed `ContentPart{type=image, artifactId, detail}` 同事务写入 `ChatMessages.ContentPartsJson`；文本模型只收 `artifact://` 占位 |
| **记忆与存储治理** | `Source/PuddingMemoryEngine/`、`Source/PuddingHost/Storage/` + [ADR-076](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) | Library/Book/Chapter 是 Agent 主动维护的当前结论与索引；`StorageMaintenanceCoordinator` 是唯一在线维护 writer，保留策略读 `system.json` 且 CAS + fail-closed |
| **插件 / Hook / 事件 / 投影** | `Source/PuddingCore/` + [参考架构](Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | Plugin/Function/Hook/Event/Projection 五类合同；outbox → DomainEventLog → per-consumer checkpoint/retry/dead-letter |
| **Chat 前端投影与虚拟化** | `Source/PuddingPlatformAdmin/`（`src/pages/chat/`） | 服务端出 canonical 事件；`TurnSurfaceStore` → `ExecutionFlowProjectionIndex` → `MessageViewportRuntime` → `TurnContentStream` |
| **认证与外部 API** | `Source/PuddingPlatform/Security/` | 看板 Access Token（`pdt_v1_` opaque、摘要存储）、External API v1（ETag/幂等/`202 + Location`）、Loopback ControlToken 双通道鉴权 |
| **前端构建与版本** | `Source/PuddingPlatformAdmin/package.json` | 版本号以 `version` 字段为准，构建期注入 `__PUDDING_FRONTEND__`；改前端必须递增版本并重新构建部署 `wwwroot/admin` |
| **可诊断基础设施** | `Source/PuddingDiagnostics/` + [设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 失败必须收敛为稳定因果码 + 阶段 + 有界证据（脱敏、键≤32/值≤512/总≤8KiB）+ 显式截断标记；分类器是纯函数，故障场景「注入 → 期望结论」是门禁；新组件不得反向引用消费方 |

## 4. 关键调用链路

| 层 / 项目 | 符号 | 一句话作用 |
|----------|------|----------|
| `Desktop → Core` | `PuddingDesktop/Hosting` → `PuddingHost/Hosting` | Shell 以子进程点火 Core：`STARTING`（每 5s 带协议/PID/序号）→ 全部 hosted service 就绪后 `READY` |
| `Core 组合根` | `Source/PuddingHost/` | HTTP 宿主、本机控制地址、Browser Bridge、飞书连接器的装配起点 |
| `入口薄壳` | `Source/PuddingAgent/Program.cs` | Console 与 DesktopChild 两种模式分派；Console 只用于开发/诊断 |
| `WebUI → Runtime` | `TurnExecutorAdapter` → `AgentExecutionAdmissionCoordinator` | 前台 Turn 准入并抢占同 agent 后台执行，旧执行立即 defer 回队列 |
| `Runtime` | `ContextPipeline` → `AgentExecutionService` | 组装 stable system prefix + volatile User tail，再用 `[CURRENT USER TURN input_sha256=…]` 围栏本轮输入（缺失 fail-closed） |
| `Runtime → Gateway` | `LlmInvocationService` → `DirectLlmClient` | 按 `model.protocol` 路由 openai / responses / anthropic 三协议网关 |
| `Gateway → Platform` | `ILlmGatewayUsageRecorder` → `llm_gateway_usage_events` | usage 落库后供 StatsApi 与日聚合取本地计费口径 |
| `Runtime` | `ContextWindowManager` → `ContextCompactionService` | 绝对窗口 proactive 压缩；压缩前从 ChatMessages 增量镜像到 memory，失败 fail-closed。软维护按「容量 + 候选变化」准入：请求仍能安全容纳时只登记 `context.compaction.skipped`（stage=deferred）并延期到 canonical 终态之后，越过有效输入上限才同步保护（ADR-095 D2） |
| `Platform` | `MessageDeliveryPolicy` → `MessageDeliveryDispatcher` | inform/report_result 只通知；ask/request_review/delegate 才进执行 |
| `Runtime` | `SubAgentInvocationService` → `SubAgentManager` | 子代理委派：model 须为 providerId/modelId；run archive 固化实际预算 |
| `Platform` | `TaskAutoDispatchWorker` → `TaskExecutionTracker` | 5 min 有界派发：availability → backlog 精炼 → Plan/Node 编译 → 五态跟踪 |
| `Platform` | `GoalContinuationWorker` → `GoalSettlementStore` | 续行前重读 Command→Goal→Binding→Plan/Node 围栏；终态原子结算 |
| `Runtime` | `search_tools` + `ToolApprovalCommandFirewall` | 工具发现默认 3 / 上限 8；只读与构建秒放、危险秒拒、未知形态交 LLM 审批 |
| `Runtime → 索引族` | `CodeIndexScheduler` → `PuddingCodeIndex` | 变更捕获 → 分块 → 语言智能 → Lucene / 向量索引；路径忽略与检索评测各为叶子 |
| `Runtime → Host` | `Browser Tools` → `RemoteBrowserRuntime` | 浏览器工具经 WebSocket 到达 Desktop 的 WebView2 |
| `Desktop ↔ Core` | `CapabilityBroker` ⇄ `DesktopConnection` / `DesktopService` | 两端只经 Contracts + wire 协议；启用前过 preflight，否则保持旧 Bridge |
| `Runtime → Platform` | `ContentPart{type=image}` → `image_reader` | 视觉内容同事务写 ChatMessages 投影；文本模型只收 `artifact://` 占位 |
| `Platform → WebUI` | `AgentConversationProjectionService` → `TurnSurfaceStore` | canonical 事件 → 投影索引 → 虚拟化视口 → 交错内容流 |

## 5. 测试工程索引

| 测试工程 | 覆盖 |
|------|------|
| `Source/PuddingCoreTests/` · `Source/PuddingRuntimeTests/` · `Source/PuddingPlatformTests/` · `Source/PuddingMemoryEngineTests/` · `Source/PuddingWebApiTests/` | 工具契约、LLM 网关、MessageFabric · Agent Loop、上下文管线、语音/图片 · 渠道配置与 Artifact · 记忆库与 FTS5 · Web API |
| `Source/PuddingCodeIndexTests/` · `Source/PuddingIndexChunkingTests/` | 索引组件独立测试：变更管线/调度/维护/存储 + 分块（含边界断言） |
| `Source/PuddingContextPolicyTests/` | 上下文策略组件独立测试：容量算术与压力分类（含事故两条样本的记录值回放）、候选边界与指纹、严格适用校验（新消息追加必须让旧候选失效）、净收益准入（真实摘要重算 after）、退避判定（硬保护不被退避屏蔽）+ S1–S4 边界断言（含探测器自检与取红） |
| `Source/PuddingDiagnosticsTests/` | 可诊断基础设施独立测试：故障场景→期望因果码全量、事故复刻与证据、脱敏与证据预算、事故投影、S1–S4 边界断言（含探测器自检与取红） |
| `Source/PuddingBrowser.AutomationTests/` | 浏览器自动化可靠性组件独立测试：控制权/接管与世代、页面授权与子代理范围、回执与重试裁定、operationId 账本、S1–S4 边界断言与用例守恒 |
| `Source/PuddingPathFilteringTests/` · `Source/PuddingVectorIndexTests/` · `Source/PuddingFullTextIndexTests/` · `Source/PuddingMemoryEngineBenchmarks/` · `Tests/PuddingBrowser.WebView2.Smoke/` | 路径忽略、向量与全文索引叶子组件 · BenchmarkDotNet 基准 · 浏览器 smoke |
| `Source/PuddingRetrievalEvalTests/` · `Source/PuddingRetrievalEvalProbe/` · `Source/PuddingFullTextIndex.Cli.Tests/` | 检索评测（`ISearchProbe` 端口）与全文索引 CLI |
| `Source/PuddingCodeIntelligenceTests/` · `Source/PuddingCodexServiceTests/` · `Source/PuddingBrowser.WinUITests/` · `Source/PuddingDesktop.FoundationTests/` | 语言智能层 · Codex MCP Service · WinUI 浏览器表面 · Foundation 纯逻辑 |
| `Source/Pudding.ContractsTests/` · `Source/Pudding.Rpc.ProtocolTests/` · `Source/Pudding.DesktopSurface.BrowserTests/` | 契约与 wire 协议形状断言 · 浏览器表面 |
| `Source/Pudding.DesktopConnectionTests/` · `Source/Pudding.DesktopServiceTests/` · `Source/Pudding.CapabilityBrokerTests/` · `Source/Pudding.CapabilityBroker.AspNetCoreTests/` | 能力通道四个组件的独立测试（假服务端 / 假 UI 调度器 / 自托管 Kestrel） |
| `Tests/PuddingDesktop.Tests/` · `Tests/PuddingHost.Tests/` · `Tests/PuddingBrowser.AgentTools.Tests/` | Desktop 进程与配置、调试模式（路由/反向代理/SSE/WS）· Bridge Endpoint · 七项 Agent Tools |
| `Tests/PuddingAgent.IntegrationTests/` · `Tests/PuddingNativeChat.IntegrationTests/` · `Tests/e2e/` · `TestScripts/` · `Tests/PuddingBrowser.TestSite/` | 集成测试 · 端到端脚本 · 生命周期/部署/smoke/性能探针 · 测试站点 |

## 6. 架构与设计文档索引

| 文档 | 主题 |
|------|------|
| [`Docs/README.md`](Docs/README.md) · [`Agents.md`](Agents.md) · [`Agents-Hygiene.md`](Docs/10_conventions/Agents-Hygiene.md) | 文档总索引与阅读顺序 · 开发与提交纪律 · 仓库卫生 |
| [`组件化交付规程`](Docs/10_conventions/组件化交付规程.md) · [`调试入口`](Docs/08_how_debuge/README.md) · [`架构`](Docs/07_architecture/架构.md) · [`ADR 全表`](Docs/07_architecture/README.md) | 组件化交付 S1–S5 门禁（强制）· 调试与日志诊断 · 架构总览与模块分册 |
| [可诊断基础设施设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 诊断事实/类型化因果/事故查询面/故障场景资产：叶子组件边界、S1–S5 施工与门禁、查询与脱敏的强制项 |
| [浏览器自动化可靠性与渐进阅读设计](Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md) | 浏览器控制权与页面授权、动作回执、稳定 ref、渐进 DOM/markdown 阅读、截图视觉和受约束委派的目标设计与交付门禁（提案） |
| [ADR-095](Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) · [上下文维护施工方案](Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) | Proposed：Session软维护与逐请求硬保护、唯一候选提交/checkpoint、请求实测与估算分离；独立策略组件先行，尚未接入 |
| [`ADR-089`](Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) · [检索设计](Docs/12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md) · [`ADR-077`](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) · [视觉设计](Docs/12_features/原生视觉与统一取图截图优化设计-2026-09-12.md) | 统一检索入口与索引组件拆分 · 原生视觉、typed parts 与截图链路 |
| [`ADR-076`](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) · [存储设计](Docs/12_features/遥测调试数据自动过期与Web存储管理设计方案.md) | 存储治理、语义目录、保留策略 |
| [`ADR-079`](Docs/07_architecture/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md) · [内容流设计](Docs/12_features/Agent消息交错内容流与最新行为组披露完整实施方案.md) | Agent 回合单一有序内容流与披露 owner |
| [`ADR-074`](Docs/07_architecture/89ADR-074Goal持久目标自主续行与自动压缩ADR.md) · [Goal 设计](Docs/12_features/Goal持久目标自主续行与自动压缩完整设计方案.md) | 持久 GoalRun、证据验证、Task-bound Goal |
| [`自动派发`](Docs/12_features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md) · [`夜间调度`](Docs/12_features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md) · [`ADR-073`](Docs/07_architecture/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md) | 自动派发 · Execution 生命周期闭环 · 产品施工总表 |
| [Token 效率](Docs/12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md) · [Harness 兼容](Docs/12_features/AgentHarness兼容与工具调用效率修复设计方案.md) · [`ADR-081`](Docs/07_architecture/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md) | 分级压缩与缓存命中验收 · Harness 兼容边界与工具协议适配 |
| [`tool-infrastructure-layering.md`](Docs/07_architecture/tool-infrastructure-layering.md) · [自动工具审批](Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md) | Tool 分层、强制委派合同 · 自动工具审批唯一设计入口 |
| [产品架构裁定](Docs/12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md) · [能力通道](Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) · [余额与计费](Docs/12_features/服务商余额查询与多服务商计费适配器设计方案.md) | WinUI Shell + Web UI + 独立 Core · gRPC 能力通道切片 · 余额徽标与计费适配 |
| [参考架构总蓝图](Docs/19_references/deepseek-reference-architecture-master-plan-2026-08-14.md) · [插件/Hook/事件](Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | 「一切业务能力皆插件」总蓝图（T00–T16）· Plugin/Function/Hook/Event/Projection 合同 |
| [工具系统对齐](Docs/19_references/deepseek_harness/deepseek-harness-tool-system-alignment-2026-08-14.md) · [消息卡片对齐](Docs/19_references/deepseek_harness/deepseek-harness-message-card-alignment-2026-08-14.md) | 工具 canonical output / callId / 结构化错误 / spill · 消息与推理的 UI 投影合同 |
| [`Docs/00_changelog/`](Docs/00_changelog/README.md) · `Docs/14_reports/` · `Docs/16_qa/` · `Docs/15_tasks/` | 修改日志唯一去处（规则见其 README）· 诊断与验收报告 · 验收记录 · 历史任务（只读证据，不是索引） |

## 7. 运行时目录与构建入口

- `D:\data` — 开发环境 DataRoot（由 `dev-up.py` 的环境变量/启动参数决定，见 PathHelper）。
- `<DataRoot>/config/system.json` — Core 系统配置：端口、ControlToken、启动超时、保留策略、ToolReview 覆盖。
- `<DataRoot>/config/security.json` — 登录态 JWT（`jwt.key/issuer/audience/expiryHours`，缺省 7 天；密钥缺失时由引导期生成并写回，代码内无硬编码兜底）。
- `<DataRoot>/config/llm.providers.json` — LLM 服务商/模型配置来源（apiKey 支持 `${ENV}` / vault 引用，不回显）。
- `D:\data\workspaces\default` · `DesktopHome/desktop.json` · `.pudding-host.lock` — 默认工作空间 · Shell 侧 DataRoot/Core 路径/关闭行为 · Console 与 DesktopChild 共用的单实例文件句柄租约（不删锁文件绕互斥）。
- `.pudding/context-tool-results/` · `temp/build/` · `temp/test-out/` — 工具结果完整原文（模型只收 8 KiB 有界摘要）· 唯一允许的临时产物目录。
- `dotnet build PuddingRuntime --no-restore` · `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo` · `dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo` — 编译入口 · Desktop 定向构建与测试（build/test/publish 必须串行）。
- `python dev-up.py --frontend-only / --restart / --rebuild / --status / --down` — 源码开发态前后端与工具进程。
