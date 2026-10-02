# PuddingAgent 代码地图（根索引）

> **本文件是代码地图，不是日志。** 只放四类东西：**关键概念 · 组件 · 关键文件（相对路径）· 用途**。
> 使用与维护规则见 [`Agents.md`](Agents.md) →「code_map.md 使用规则」。
> 任务记录、提交号、测试数字、验收结论一律写 [`Docs/00Changelog/`](Docs/00Changelog/README.md)，**禁止**追加到本文件。
>
> 本文件是**主索引**：每个子项目自己的 `code_map.md` 负责该项目内部的文件级索引。
> 最近一次整理：2026-10-02（当时从本文件迁出的全部历史日志见 [`2026-10-02-根code_map迁出历史记录.md`](Docs/00Changelog/2026Year/10/2026-10-02-根code_map迁出历史记录.md)）。

## 0. 怎么用（30 秒）

| 想找什么 | 去哪里 |
|----------|--------|
| 某个子项目 / 程序集 | §2 子项目索引 → 该项目自己的 `code_map.md` |
| 某个概念或机制的权威位置 | §3 关键概念与组件 |
| 端到端数据流、契约边界、不变量 | §4 关键调用链路 |
| 测试放哪、覆盖什么 | §5 测试工程索引 |
| 设计决策与规格文档 | §6 架构与设计文档索引 |
| 运行时数据与配置文件在哪 | §7 运行时目录与构建入口 |
| 为什么这么改、哪一轮做的 | `Docs/00Changelog/`（日志）、`Docs/Reports/`（诊断/验收报告） |

**改完代码后**：只更新受影响的条目（新增文件就补进对应子项目的 `code_map.md`，新增子项目就登记进 §2），过程记录写 `Docs/00Changelog/`。

## 1. 项目定位

Pudding — Windows First 的 .NET 10 桌面智能助手与 IDE：六层记忆体系、Skill 系统、子代理委派、潜意识后台管道。

- 产品入口 `PuddingDesktop.exe` = **WinUI 3 Shell + WebView2 承载既有 Web UI**，以子进程方式启动并监督独立 ASP.NET Core。
- 业务逻辑、Agent、Connector、数据库、Runtime 全在 **Core**（`core/PuddingAgent.exe --desktop-child`）；Shell 只负责窗口、系统集成与进程监督。
- Console 入口仅用于开发/诊断；`dev-up.py`（本体 `Tools/Dev/dev-up.py`，根目录为转发 shim）只服务源码开发调试，不进入交付包。
- 架构第一原则（组件独立 · 依赖方向由编译期强制 · 可测性优先 · 边界显式）、兼容性与补丁约定、仓库卫生纪律：见 [`Agents.md`](Agents.md) 与 [`Agents-Hygiene.md`](Docs/Conventions/Agents-Hygiene.md)。

## 2. 子项目索引

> 本表只做路由：一句话用途 + 该项目自己的 `code_map.md`。完整程序集清单见 [`PuddingAgentNetwork.slnx`](PuddingAgentNetwork.slnx)。

### 2.1 产品与运行时

| 项目 | 用途 | 文件级索引 |
|------|------|-----------|
| `Source/PuddingDesktop/` | 🔑 WinUI 3 Shell：单实例、托盘、标题栏外观、Core 子进程点火与监督、WebView2 工作台/工具区、运行中心与存储管理 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingAgent/` | 🔑 入口 `Program.cs`：Console / DesktopChild 两种薄壳 | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingHost/` | 🔑 Core 组合根：HTTP 宿主、本机控制地址、Browser Bridge、飞书连接器、组合根装配 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM 网关调用 · 工具系统 · 上下文管线（压缩/冷水合共享 canonical ChatMessages 门禁） · 子代理 · 后台学习 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingCore/` | 🔑 跨层抽象与契约：接口、模型、事件、Goal/Orchestration/Skills 基础类型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · Web 与外部 API（认证、当前用户投影） · EF Core + SQLite · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter 记忆库 · FTS5 · 潜意识检索 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 服务商网关适配（openai / responses / anthropic 三协议） | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端（Chat 工作台、任务看板、编排编辑器、/storage、/index-status 等）；`dist` 经 `PuddingHostContent.props` 部署到 Core `wwwroot/admin` | [code_map](Source/PuddingPlatformAdmin/code_map.md) |
| `Source/PuddingTaskRecall.Cli/` | 历史脏数据一次性诊断/修复 CLI（默认 dry-run，`--apply` 才写库） | [code_map](Source/PuddingTaskRecall.Cli/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 工具声明（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `src/HarnessAgent/Core/` | Harness Agent 核心库（与主线并列的 Harness 实现） | — |

### 2.2 Desktop ↔ Core 能力通道（平台/传输无关组件）

| 项目 | 用途 | 文件级索引 |
|------|------|-----------|
| `Source/Pudding.Contracts/` | **契约叶（仅 BCL）**：能力目录、握手协商、错误语义、能力 DTO、审计形状 | [code_map](Source/Pudding.Contracts/code_map.md) |
| `Source/Pudding.Rpc.Protocol/` | **wire 协议叶**：`Protos/desktop_capability.proto` 与生成类型（无业务实现） | [code_map](Source/Pudding.Rpc.Protocol/code_map.md) |
| `Source/Pudding.CapabilityBroker/` | Core 侧能力 Broker：握手协商、会话与世代、命令关联、取消/期限/队列预算、授权接缝、端点命名 | [code_map](Source/Pudding.CapabilityBroker/code_map.md) |
| `Source/Pudding.CapabilityBroker.AspNetCore/` | Core 侧宿主适配：gRPC 服务、服务端流适配、Kestrel/DI/路由装配助手 | [code_map](Source/Pudding.CapabilityBroker.AspNetCore/code_map.md) |
| `Source/Pudding.DesktopConnection/` | Desktop 侧 gRPC 双向流适配：连接状态机、命令关联、取消/期限/背压、重连 | [code_map](Source/Pudding.DesktopConnection/code_map.md) |
| `Source/Pudding.DesktopService/` | Desktop 侧能力服务：目标/可信级别/版本校验、准入、入队后竞态复检、UI 调度边界、宿主装配与生命周期 | [code_map](Source/Pudding.DesktopService/code_map.md) |
| `Source/PuddingDesktop.CapabilityHost/` | WinUI 侧平台适配：`DispatcherQueue` 调度实现（只引用 Contracts） | [code_map](Source/PuddingDesktop.CapabilityHost/code_map.md) |
| `Source/Pudding.DesktopSurface.Browser/` | 浏览器侧 Desktop 表面与目标注册表桥（九项能力映射、表面组装、`BrowserWorkspaceTargetBridge`）；**不含任何 UI 代码**，整条链路可脱 UI 测试 | [code_map](Source/Pudding.DesktopSurface.Browser/code_map.md) |
| `Source/PuddingDesktop.Foundation/` | BCL-only 布局与外观偏好（`ShellLayout`、`WorkbenchAppearance`、`SkeletonSettingsStore`） | [code_map](Source/PuddingDesktop.Foundation/code_map.md) |
| `Source/PuddingDesktop.WpfArchive/` | 旧 WPF 产品入口/验证基线；**无 UI 的启动器与协议源文件由 WinUI 项目链接编译**，不是产品进程 | [code_map](Source/PuddingDesktop.WpfArchive/code_map.md) |
| `Source/Pudding.Rpc.IpcProbe/` | 真实端点技术探针（Kestrel Named Pipe / h2c 服务端替身，退出码 0/1） | — |

### 2.3 代码索引与检索组件族（ADR-089 分层）

| 项目 | 用途 | 文件级索引 |
|------|------|-----------|
| `Source/PuddingCodeIndex/` | 🔑 **索引组件**：契约 / 存储 / 变更捕获管线 / 调度 / 范围注册解析；**不得引用** `PuddingCodeIntelligence`（编译期强制） | [code_map](Source/PuddingCodeIndex/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 语言智能与查询：Roslyn / TypeScript 抽取、outliner、符号与内容查询 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingIndexChunking/` | 分块组件：源文件 → 可独立索引的块（叶子，outline 由端口 `IOutlineSource` 注入） | [code_map](Source/PuddingIndexChunking/code_map.md) |
| `Source/PuddingPathFiltering/` | 路径忽略合同的**唯一真源**（噪声目录名 + `.gitignore` 语义） | [code_map](Source/PuddingPathFiltering/code_map.md) |
| `Source/PuddingVectorIndex/` | 向量索引组件：文本 → 向量 → 内存有界 top-k（叶子，`IEmbeddingProvider` 端口） | [code_map](Source/PuddingVectorIndex/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎（Lucene）与供给协调 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingFullTextIndex.Cli/` | 全文索引供给的离线驱动工具（`plan` / `status` / `build` / `cancel`） | [code_map](Source/PuddingFullTextIndex.Cli/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingRetrievalEval/` | 检索评测组件（叶子，只依赖本组件定义的 `ISearchProbe` 端口） | [code_map](Source/PuddingRetrievalEval/code_map.md) |

### 2.4 浏览器自动化

| 项目 | 用途 | 文件级索引 |
|------|------|-----------|
| `Source/PuddingBrowser.Abstractions/` | Browser 契约（驱动与 Agent 工具共用） | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议 | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver（DOM/元素/页面操作） | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 `browser_*` Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.WinUI/` | WinUI 浏览器表面宿主（与 WPF 适配层共享驱动源文件） | [code_map](Source/PuddingBrowser.WinUI/code_map.md) |

### 2.5 代码地图缺口

下列程序集尚无 `code_map.md`（新增文件时顺手补一份，并在 §2 登记）：`Source/Pudding.DesktopSurface.Browser`、`Source/Pudding.Rpc.IpcProbe`、`Source/PuddingRetrievalEvalProbe`、`Source/PuddingMemoryEngineBenchmarks`、`src/HarnessAgent/Core`、`Tests/*`（见 §5）、`external/github.hyfree.GM`。

## 3. 关键概念与组件

| 概念 | 权威位置 | 用途 / 不变量 |
|------|----------|----------------|
| **canonical Turn 与 current-turn 围栏** | `Source/PuddingRuntime/Services/AgentExecution/` | 一次用户请求 = 一个 canonical Turn。`AgentExecutionService` 用 `[CURRENT USER TURN input_sha256=…]` 围住本轮文本与 typed ContentParts；预算裁剪/投影后围栏缺失即 fail-closed。易变内容（当前消息、日期、召回）不得进 system prompt，保证前缀缓存稳定 |
| **上下文管线与压缩** | `Source/PuddingRuntime/Services/` | `ContextPipeline` / `ContextWindowManager` / `ContextCompactionService`：分层组装（稳→动）、绝对窗口 proactive 压缩、`CompactionCoverageFilter` 去重与 hash 围栏、压缩前 canonical ChatMessages 增量镜像（失败即 fail-closed） |
| **Message Fabric / send_message** | `Source/PuddingPlatform/Services/` | `MessageDeliveryPolicy` + `MessageDeliveryDispatcher` + `ConversationReplyProjectionWorker`：`inform/report_result` = notify（不建 Turn、不调模型），只有 `ask/request_review/delegate` = execute；`agent_reply` 永远被动，切断 A→B→A 回声 |
| **Task 自动派发 / Goal / WorkUnit** | `Source/PuddingPlatform/Services/Scheduling/` | `TaskAutoDispatchWorker` → `AgentAvailabilityProjectionStore` → `BacklogRefinementEvaluator` → `TaskAutoDispatchEvaluator` → `TaskExecutionPlanCompiler` → `TaskExecutionTracker` → `GoalContinuationWorker` → `GoalSettlementStore`。五态跟踪：Binding/Assignment/Reservation/Goal/Iteration；promotion/start/repair 只有一个 CAS/fencing 写入者 |
| **工具系统与强制委派** | `Source/PuddingRuntime/Tools/` | 首次工具调用前判定 Direct / Delegated；复杂任务前三次内进入 `smart_*` 或 `spawn_sub_agent`；`smart_explore` 是统一入口（已退役 `smart_search` / `smart_query_session_log`）；工具结果完整原文落 `.pudding/context-tool-results`，模型只收有界摘要 |
| **工具审批与权限防火墙** | `Source/PuddingRuntime/Tools/`、[自动审批设计](Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md) | `ToolApprovalCommandFirewall` 引号/管道感知解析：已知只读/构建/测试秒放、危险秒拒、未知形态交 LLM 审批；参数级风险由 descriptor + 实际参数 + 系统证据派生，Agent 不能自我降级；用户审批是最后手段 |
| **代码索引组件族** | §2.3 + [ADR-089](Docs/07架构/103ADR-089Agent统一检索与渐进展开工具链ADR.md) | 依赖方向由编译期强制：`PuddingCodeIndex` 不得反向引用 `PuddingCodeIntelligence`；叶子组件 `ProjectReference/PackageReference = 0`；边界由 `ComponentBoundaryTests` 在运行期三重断言 |
| **Desktop ↔ Core 能力通道** | §2.2 + [能力通道计划](Docs/Features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) | 两端只经 `Pudding.Contracts`（DTO）与 `Pudding.Rpc.Protocol`（wire）通信；启用前必须过 `DesktopCapabilityChannelPreflight`；描述解析失败 ⇒ 保持旧 Bridge（fail-safe）。启动顺序：先 preflight 判定，再决定是否构造宿主 |
| **Core 启动与就绪契约** | `Source/PuddingHost/Hosting/`、`Source/PuddingDesktop/Hosting/` | Core 初始化期每 5s 发 `PUDDING_DESKTOP_STARTING`（协议/PID/单调序号），全部 hosted service `StartAsync` 返回后才发 `PUDDING_DESKTOP_READY`；租约不能替代 Ready、PID 校验或 `/health/ready` |
| **浏览器自动化链路** | `Source/PuddingHost/BrowserBridge/`、`Source/PuddingBrowser.WebView2/` | Snapshot ref 必须携带 `PageVersion`；交互提交后不得重查旧 Locator，后续状态用 Wait 或新 Snapshot。底层保持通用，抖音等能力只在上层适配器 |
| **多模态视觉链路** | `Source/PuddingRuntime/` + [ADR-077](Docs/07架构/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) | typed `ContentPart{type=image, artifactId, detail}` 同事务写入 `ChatMessages.ContentPartsJson`；主模型带 vision 时原生进请求，文本模型只收 `artifact://` 占位；`image_reader` 只有 native 一条路径，调用模型无视觉能力即 fail-closed |
| **记忆（Memory）** | `Source/PuddingMemoryEngine/` | Library/Book/Chapter 是 Agent 主动维护的当前结论与索引；聊天/向量命中只是候选证据。正文唯一存放在外部文件或 Book/Page，历史按需查看 |
| **存储治理与保留策略** | `Source/PuddingHost/Storage/` + [ADR-076](Docs/07架构/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) | `StorageMaintenanceCoordinator` 是唯一在线维护 writer（双优先级队列 + `maintenance.lock`）；保留策略读 `<DataRoot>/config/system.json`，CAS + fail-closed；在线全库 VACUUM 已移除 |
| **插件 / Hook / 事件 / 投影** | `Source/PuddingCore/` + [参考架构](Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | Plugin/Function/Hook/Event/Projection 五类合同；Typed Hook（Guard/Transform/Around）同步有界干预；状态提交 + transactional outbox → durable DomainEventLog → per-consumer checkpoint/retry/dead-letter → UI 投影 |
| **Chat 前端投影与虚拟化** | `Source/PuddingPlatformAdmin/src/pages/chat/` | 服务端 `AgentConversationProjectionService` 出 canonical 事件；前端 `TurnSurfaceStore`（turnId 别名归并、eventId 幂等）→ `ExecutionFlowProjectionIndex`（只重投影 dirty Turn）→ `MessageViewportRuntime`（虚拟化/锚点/贴底）→ `TurnContentStream`（TextBlock ⇄ ActivityGroup 交错） |
| **认证与外部 API** | `Source/PuddingPlatform/Security/` | 第三方任务看板 Access Token（`pdt_v1_` opaque、摘要存储）、External API v1（ETag/幂等/`202 + Location`）、Desktop Loopback ControlToken 双通道鉴权 |
| **前端构建与版本** | `Source/PuddingPlatformAdmin/package.json`、`Source/PuddingPlatformAdmin/config/config.ts` | 版本号唯一真源 = `package.json` 的 `version`；构建期注入 `__PUDDING_FRONTEND__` → `Source/PuddingPlatformAdmin/src/utils/frontendBuild.ts` → 页角版本徽标（`Source/PuddingPlatformAdmin/src/components/FrontendVersionBadge`）；改前端必须递增版本并**重新构建部署** `wwwroot/admin`（规则见 `Agents.md`） |

## 4. 关键调用链路

```
1) 用户 Turn → Agent 执行（前台准入）
User Turn → TurnExecutorAdapter → AgentExecutionAdmissionCoordinator（foreground）
  → 抢占同 workspace/agent 的后台 Message Fabric 执行（含 subagent_result），旧执行立即 defer 回队列
  → ContextPipeline 组装 stable system prefix + volatile User tail
  → AgentExecutionService 用 `[CURRENT USER TURN input_sha256=…]` 围住本轮输入（缺失即 fail-closed）
  → LlmInvocationService → DirectLlmClient → 三协议网关之一
  → 工具调用经 Direct/Delegated 判定 → 结果落盘，模型只收有界摘要

2) LLM 网关与计费归因
LlmInvocationService → DirectLlmClient
  → model.protocol=openai       → OpenAiLlmGateway（/chat/completions）
  → model.protocol=responses    → ResponsesLlmGateway（/responses）
  → model.protocol=anthropic    → AnthropicMessagesLlmGateway（/messages）
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议
  → Provider usage → ILlmGatewayUsageRecorder → llm_gateway_usage_events
    → StatsApiController（月度/趋势本地计费口径）
    → TokenUsageDailyAggregateService / ContextLayerDailyRollupService（闭日 UTC 聚合）

3) 上下文压缩
ContextPipeline → ContextWindowManager（绝对窗口 proactive 压缩）→ ContextCompactionService
  → CompactionCoverageFilter（全部 manifest 并集 + 同轮 hash 去重）
  → 压缩前从 platform ChatMessages 按稳定 MessageId 增量镜像到 memory；同步失败即 fail-closed
  → 摘要链标记 CompactedBy；当前 turn 落在压缩范围内即在写入前 fail-closed

4) 消息与子代理（Message Fabric）
Agent send_message → MessageDeliveryPolicy
  → inform/report_result = notify（不建 Turn、不调模型）；ask/request_review/delegate = execute
  → MessageDeliveryDispatcher 按 workspace/Agent 跨 room 原子领取 → ConversationNotificationStore 逐条写 ChatMessage 后 ACK
spawn_sub_agent → SubAgentInvocationService → SubAgentManager
  → model 必须是 providerId/modelId 完整路由（裸 modelId 多 provider 注册时报 ambiguous）
  → 轮内 warm-prefix checkpoint；run archive 固化实际预算与预算通知事件
  → FileSubAgentRunStore：per-run gate + JSONL sharing violation 退避；耗尽写 archive-degraded 降级，不杀死运行

5) 任务自动派发与 Goal 结算
TaskAutoDispatchWorker（5 min，bounded authoritative）
  → AgentAvailabilityProjectionStore（每轮先刷新全部 Agent，候选为 0 也输出 idle/busy/unknown）
  → BacklogRefinementEvaluator/Store（显式 autoDispatchEnabled；CAS Backlog→Ready）
  → TaskAutoDispatchEvaluator（结构化类型/能力/模型路由 + availability/executionWindow）
  → TaskExecutionPlanCompiler（WorkUnit DAG + budget/scope/dependency SHA-256）
  → TaskExecutionTracker（五态跟踪；每 Agent 每轮最多一个、全局最多两个）
  → GoalContinuationWorker → ExecutionRunCoordinator（执行前重读 Command→Goal→Binding→Plan/Node 围栏）
  → GoalSettlementStore（终态原子结算；Task-bound 失败原子释放并保留 Failed 审计历史）

6) 工具发现与权限
首次工具调用前判定 Direct / Delegated；复杂任务前三次内进入 smart_* 或 spawn_sub_agent
  → search_tools 默认 3 / 上限 8；已发现 schema 在 live session 内保持；动态定义在下一 LLM round 单调生效
  → 连续 discovery-only（含换词同族）触发 tool_discovery_stalled 熔断
  → ToolApprovalCommandFirewall（只读/构建秒放、危险秒拒、未知形态交 LLM 审批）
  → 配置 = 程序默认 + <DataRoot>/config/system.json ToolReview 覆盖（review profile 走 llm_resource_pool）

7) 代码索引与检索（ADR-089）
CodeIndexScheduler → PuddingCodeIndex（变更捕获管线 / 调度 / 存储 / 范围注册解析）
  → PuddingIndexChunking（分块）→ PuddingCodeIntelligence（Roslyn / TypeScript 语言智能与 outliner）
  → PuddingFullTextIndex（Lucene）/ PuddingVectorIndex（就地 int8 扫描 + 有界 top-k）
  → PuddingPathFiltering（唯一路径忽略真源：噪声目录名 + .gitignore 语义）
  → PuddingRetrievalEval（检索质量与性能仪器，σ 只依赖 ISearchProbe 端口）
  → search_grep / code_symbol_search / workspace_* 查询；未登记项目 fail-closed

8) 浏览器自动化
Agent Loop → search_tools → Browser Tools（PuddingBrowser.AgentTools）
  → IBrowserRuntime → RemoteBrowserRuntime（PuddingHost/BrowserBridge）
  → WebSocket → DesktopBrowserBridgeClient（Desktop/Browser）→ WebView2（PuddingBrowser.WebView2）
  → Snapshot ref 携带 PageVersion；交互后不得重查旧 Locator，用 Wait 或新 Snapshot

9) Desktop ↔ Core 能力通道
Core 侧：CapabilityBroker（会话与世代 / 命令关联 / 取消期限 / 队列预算）→ CapabilityBroker.AspNetCore（gRPC 服务与流适配）
Desktop 侧：DesktopConnection（双向流状态机 / 重连）→ DesktopService（目标、可信级别、版本校验、准入、UI 线程边界）
  → DesktopCapabilityHost（DispatcherQueue 适配；只引用 Contracts）
  → 组合根顺序：DesktopCapabilityChannelPreflight.Evaluate → 按 ShouldStart 决定 DesktopCapabilityHostFactory.Create

10) Core 启动与就绪
Desktop → Process.Start（core/PuddingAgent.exe --desktop-child）
  → Core 每 5s 发 PUDDING_DESKTOP_STARTING（协议 / PID / 单调序号）
  → 全部 hosted service StartAsync 返回后发 PUDDING_DESKTOP_READY（含 capabilityEndpoint）
  → Desktop 以 startupTimeoutSeconds 为静默超时；冷升级租约允许 10 倍且最高 10 分钟
  → Ready 之外仍需 PID 校验与 /health/ready；飞书等连接器故障只 Faulted 自身，不阻塞 Ready

11) 多模态视觉
typed ContentPart{type=image, artifactId, detail} → 同事务写 ChatMessages.ContentPartsJson（Content 为文本拼接投影）
  → 主模型带 vision：ContentParts 原生进入请求；文本模型只收 artifact:// 占位并显式调用 image_reader
  → image_reader：path 唯一必填（http(s) / 宿主绝对路径 / artifact://），native 单路径零辅助 LLM
  → 调用模型无视觉能力 ⇒ vision_model_capability_mismatch；非 responses 协议 ⇒ vision_tool_output_not_supported

12) 存储治理与保留
Desktop Storage → CoreStorageManagementClient → StorageAdminController（语义 API，Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceCoordinator（唯一在线维护 writer：双优先级队列 + DataRoot maintenance.lock）
  → StorageInventorySampler（有界采样）→ 原子快照 + history.jsonl（每小时一点、90 天趋势）
  → 白名单批量删除 + checkpoint/VACUUM；session_event_log / conversation_events / ChatMessages / memory 永不清理
  → RetentionPruningService 策略读 <DataRoot>/config/system.json storageManagement（CAS + fail closed）

13) Chat 首屏与投影
Chat first paint → AgentConversationProjectionService（最近 20 条可见消息 + active run 最近 64 条过程明细）
  → TurnSurfaceStore（canonical turnId + 别名归并；完成 turn 懒水合，eventId 幂等去重）
  → ExecutionFlowProjectionIndex（同帧按 Turn 合并；只重投影 dirty Turn；session switch 硬 reset）
  → messageProjection → MessageViewportRuntime（虚拟化 / 锚点 / 贴底）
  → AgentTurnCard → TurnContentStream（TextBlock ⇄ ActivityGroup 交错；最新组展开、历史组卸载 DOM）
  → SubAgentActivityDock（活动 run 零归档轮询；终态一次性回放，降级时展示 archive-degraded）
```

## 5. 测试工程索引

| 项目 | 覆盖 |
|------|------|
| `Source/PuddingCoreTests/` | 工具契约、LLM 网关、MessageFabric |
| `Source/PuddingRuntimeTests/` | Agent Loop、上下文管线、语音/图片 |
| `Source/PuddingPlatformTests/` | 渠道配置、Artifact 存储、图片生成 |
| `Source/PuddingMemoryEngineTests/` | Library/Book/Chapter、FTS5、Skill 去重 |
| `Source/PuddingMemoryEngineBenchmarks/` | BenchmarkDotNet |
| `Source/PuddingCodeIntelligenceTests/` | 代码索引的语言智能层（Roslyn/TS 抽取与符号查询） |
| `Source/PuddingWebApiTests/` | Web API |
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client、调试模式（路由/反向代理/SSE/WS 中继/前端监督器/构建部署） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint / Remote proxy |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools |
| `Tests/PuddingAgent.IntegrationTests/`、`Tests/PuddingNativeChat.IntegrationTests/`、`Tests/PuddingBrowser.WebView2.Smoke/`、`Tests/PuddingBrowser.TestSite/`、`Tests/e2e/` | 集成测试、浏览器 smoke、测试站点、端到端脚本 |
| `Source/PuddingCodeIndexTests/` | **索引组件独立测试**：变更管线/调度/维护/存储 + 边界断言 |
| `Source/PuddingIndexChunkingTests/`、`Source/PuddingPathFilteringTests/`、`Source/PuddingVectorIndexTests/`、`Source/PuddingRetrievalEvalTests/`、`Source/PuddingRetrievalEvalProbe/` | 各叶子组件的独立测试（含 `ComponentBoundaryTests` 依赖边界断言） |
| `Source/Pudding.ContractsTests/`、`Source/Pudding.Rpc.ProtocolTests/` | 契约与 wire 协议快照 / 形状断言 |
| `Source/Pudding.DesktopConnectionTests/`、`Source/Pudding.DesktopServiceTests/`、`Source/Pudding.CapabilityBrokerTests/`、`Source/Pudding.CapabilityBroker.AspNetCoreTests/` | 能力通道四个组件的独立测试（假服务端 / 假 UI 调度器 / 自托管 Kestrel） |
| `Source/Pudding.DesktopSurface.BrowserTests/`、`Source/PuddingDesktop.FoundationTests/` | 浏览器表面与 Foundation 纯逻辑 |
| `Source/PuddingCodexServiceTests/`、`Source/PuddingFullTextIndexTests/`、`Source/PuddingFullTextIndex.Cli.Tests/`、`Source/PuddingBrowser.WinUITests/` | Codex MCP Service、全文索引与其 CLI、WinUI 浏览器表面 |
| `TestScripts/`（含 `TestScripts/perf/`） | 生命周期/部署/smoke/性能与 DeepSeek 缓存命中探针脚本 |

## 6. 架构与设计文档索引

| 文档 | 主题 |
|------|------|
| [`Agents.md`](Agents.md) / [`Agents-Hygiene.md`](Docs/Conventions/Agents-Hygiene.md) / [`How-Debuge.md`](How-Debuge.md) | 仓库级开发与提交纪律 / 调试与日志诊断入口 |
| [`Docs/README.md`](Docs/README.md) | 文档总索引与建议阅读顺序 |
| [`Docs/架构.md`](Docs/架构.md) | 架构总览与阅读地图 |
| [`Docs/07架构/README.md`](Docs/07架构/README.md) | 模块级架构分册与 **ADR 全表**（按编号查 ADR 走这里） |
| [`Docs/Conventions/组件化交付规程.md`](Docs/Conventions/组件化交付规程.md) | 组件化交付 S1–S5 门禁与接入前 checklist（强制） |
| [`Docs/Features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md`](Docs/Features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md) | 产品架构裁定：WinUI Shell + Web UI + 独立 Core 子进程 |
| [`Docs/Features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md`](Docs/Features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) | Desktop ↔ Core 能力通道（gRPC）切片计划与端点命名 |
| [`Docs/07架构/103ADR-089Agent统一检索与渐进展开工具链ADR.md`](Docs/07架构/103ADR-089Agent统一检索与渐进展开工具链ADR.md) + [`Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md`](Docs/Features/Agent统一检索与渐进展开工具链设计-2026-09-13.md) | 统一检索入口、索引组件拆分与后台维护 |
| [`Docs/07架构/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md`](Docs/07架构/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) + [`Docs/Features/原生视觉与统一取图截图优化设计-2026-09-12.md`](Docs/Features/原生视觉与统一取图截图优化设计-2026-09-12.md) | 原生视觉、typed parts、Artifact 与截图链路 |
| [`Docs/07架构/91ADR-076遥测与调试数据保留及Core存储管理ADR.md`](Docs/07架构/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) + [`Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md`](Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md) | 存储治理、语义目录、保留策略 |
| [`Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md`](Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md) + [`Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md`](Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md) | Agent 回合单一有序内容流与披露 owner |
| [`Docs/07架构/89ADR-074Goal持久目标自主续行与自动压缩ADR.md`](Docs/07架构/89ADR-074Goal持久目标自主续行与自动压缩ADR.md) + [`Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md`](Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md) | 持久 GoalRun、证据验证、Task-bound Goal |
| [`Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md`](Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md) + [`Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md`](Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md) | 自动派发、夜间有效调度与 Execution 生命周期闭环 |
| [`Docs/07架构/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md`](Docs/07架构/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md) | 产品施工总表（30 项产品任务 + T00–T16 底座任务） |
| [`Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md`](Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md) | Token 成本治理、分级压缩与缓存命中验收合同 |
| [`Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md`](Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md) + [`Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md`](Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md) | Harness 兼容边界与工具协议适配 |
| [`Docs/07架构/tool-infrastructure-layering.md`](Docs/07架构/tool-infrastructure-layering.md) | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| [`Docs/deepseek-reference-architecture-master-plan-2026-08-14.md`](Docs/deepseek-reference-architecture-master-plan-2026-08-14.md) | 「一切业务能力皆插件」参考架构总蓝图（T00–T16） |
| [`Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`](Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | Plugin/Function/Hook/Event/Projection 五类合同与统一生命周期 |
| [`Docs/deepseek-harness-tool-system-alignment-2026-08-14.md`](Docs/deepseek-harness-tool-system-alignment-2026-08-14.md) | 工具 canonical output / callId / 结构化错误 / spill / presentation |
| [`Docs/deepseek-harness-message-card-alignment-2026-08-14.md`](Docs/deepseek-harness-message-card-alignment-2026-08-14.md) | 消息、推理、工具调用与委派的 UI 投影合同 |
| [`Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md`](Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md) | 自动工具审批唯一设计入口 |
| [`Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md`](Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md) | 余额徽标与多服务商计费展示适配器 |
| [`Docs/00Changelog/`](Docs/00Changelog/README.md) | **修改日志唯一去处**（规则见其 README） |
| `Docs/Reports/` · `Docs/QA/` · `Docs/Tasks/` · `Docs/Tasks.md` | 诊断/验收报告 · 验收记录 · 历史任务与设计演进（只读证据，不是索引） |

## 7. 运行时目录与构建入口

| 位置 | 说明 |
|------|------|
| `D:\data` | 开发环境 DataRoot（由 `dev-up.py` 的环境变量/启动参数决定，见 PathHelper） |
| `<DataRoot>/config/system.json` | Core 系统配置：端口、ControlToken、启动超时、保留策略、ToolReview 覆盖 |
| `<DataRoot>/config/llm.providers.json` | LLM 服务商/模型配置真源（apiKey 支持 `${ENV}` / vault 引用，不回显） |
| `D:\data\workspaces\default` | 默认工作空间 |
| `.pudding/context-tool-results/` | 工具结果的完整原文（模型输入只含 8 KiB 有界摘要） |
| DesktopHome/`desktop.json` | Shell 侧 DataRoot、Core 路径、窗口与关闭行为（`ExitAndStopCore` 等） |
| `.pudding-host.lock` | Console / DesktopChild 共用的单实例文件句柄租约（不删除锁文件绕过互斥） |
| `temp/build/`、`temp/test-out/` | 编译/发布输出、测试输出与结果：**唯一允许的临时产物目录** |

| 命令 | 用途 |
|------|------|
| `dotnet build PuddingRuntime --no-restore` | 编译入口 |
| `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo` | Desktop 定向构建（build/test/publish 必须串行） |
| `dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo` | Desktop 定向测试 |
| `python dev-up.py --frontend-only` / `--restart` / `--rebuild` / `--status` / `--down` | 源码开发态前后端与工具进程 |

---

**维护提醒**：新增子项目 → 在 §2 登记并附 `code_map.md` 链接；新增/移动关键文件 → 更新该项目自己的 `code_map.md`（必要时同步 §3/§4）；**任何日志内容都不进本文件**。
