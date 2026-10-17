# PuddingAgent 代码地图（根索引）

> 源指纹: Source/**=aee8699fb133, Tests/**=59505e8e824f, Tools/**=a21cab083cdf, TestScripts/**=3f29759b31b3 · 条目数: 120 · 最近整理: 2026-10-17
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
| `Source/Pudding.DesktopService/` | Desktop 侧能力服务：准入与 UI 调度边界及宿主装配；另含两段式验收判定与交付清单链（见下「关键语义」） | [code_map](Source/Pudding.DesktopService/code_map.md) |
| `Source/Pudding.DeliveryManifest.Cli/` | 交付清单**采集入口**（B7 内部一侧）：目录 → 自检清单 → JSON；不合格以退出码 2 拒绝产出（见下「关键语义」） | — |
| `Source/PuddingDesktop.CapabilityHost/` | WinUI 侧平台适配：`DispatcherQueue` 调度实现（只引用 Contracts） | [code_map](Source/PuddingDesktop.CapabilityHost/code_map.md) |
| `Source/Pudding.DesktopSurface.Browser/` | 浏览器侧 Desktop 表面与目标注册表桥；不含 UI 代码，可脱 UI 测试 | [code_map](Source/Pudding.DesktopSurface.Browser/code_map.md) |
| `Source/PuddingDesktop.Foundation/` | BCL-only 布局与外观偏好（`ShellLayout`、`WorkbenchAppearance`） | [code_map](Source/PuddingDesktop.Foundation/code_map.md) |
| `Source/PuddingDesktop.WpfArchive/` | 旧 WPF 入口/验证基线；无 UI 的启动器与协议源文件由 WinUI 项目链接编译 | [code_map](Source/PuddingDesktop.WpfArchive/code_map.md) |
| `Source/Pudding.Rpc.IpcProbe/` | 真实端点技术探针（Named Pipe / h2c 服务端替身，退出码 0/1） | — |
| `Source/PuddingCodeIndex/` | 索引组件：契约与变更捕获管线及范围注册；不得引用 `PuddingCodeIntelligence` | [code_map](Source/PuddingCodeIndex/code_map.md) |
| `Source/PuddingContextPolicy/` | 上下文容量与压缩候选的**纯策略**组件（BCL-only）：容量算术、候选边界与指纹、严格适用校验、净收益准入、退避判定（见下「关键语义」） | [code_map](Source/PuddingContextPolicy/code_map.md) |
| `Source/PuddingSsh/` | SSH 远程执行叶子组件（`ProjectReference=0`，包 `SSH.NET`）：合同/端口与错误分类、协调器与独占连接（见下「关键语义」） | [code_map](Source/PuddingSsh/code_map.md) |
| `Source/PuddingDeepSeekWeb/` | 🔑 DeepSeek Search/Fetch 协议叶子组件（零引用；传输是端口，不直连 HTTP/不持凭据） | [code_map](Source/PuddingDeepSeekWeb/code_map.md) |
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
| `Source/PuddingBrowser.Automation/` | 🔑 浏览器自动化可靠性组件（叶子）：统一控制权状态机、页面授权与执行租约、证据化回执判定、变更类后置条件、operationId 账本（见下「关键语义」） | [code_map](Source/PuddingBrowser.Automation/code_map.md) |
| `Source/PuddingDiagnostics/` | 🔑 可诊断基础设施叶子组件：稳定因果码/分类器/有界证据/脱敏/事故投影/故障场景；不得引用任何 Pudding 程序集 | [code_map](Source/PuddingDiagnostics/code_map.md) |

**关键语义（§2「子项目索引」表·用途列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**`Source/Pudding.DesktopService/`**

  Desktop 侧能力服务：准入与 UI 调度边界及宿主装配；另含两段式验收判定（`DesktopAcceptance`）与交付清单链（`DeliveryManifest` / 生成器 / TRX 解析 / 日志提取 / 目录采集）

**`Source/Pudding.DeliveryManifest.Cli/`**

  交付清单**采集入口**（B7 内部一侧）：目录 → 自检清单 → JSON；不合格以退出码 2 拒绝产出。配合 `TestScripts/delivery-manifest.ps1` 使用

**`Source/PuddingContextPolicy/`**

  上下文容量与压缩候选的**纯策略**组件（BCL-only，`ProjectReference`/`PackageReference` 均为 0）：容量算术、候选边界与指纹、严格适用校验、净收益准入、退避判定；不负责计时器/数据库/网络/锁/后台任务。**S1–S4 已交付，尚未 S5 接入**（未登记 slnx、未进 DI）

**`Source/PuddingSsh/`**

  SSH 远程执行叶子组件（`ProjectReference=0`，包 `SSH.NET` 2026.0.0）：合同/端口、known_hosts/pin、POSIX 封装、错误分类、协调器与独占连接；不解析 DataRoot/Agent 主体、不做审批与持久化。宿主接入前置约束见 [ADR-096](Docs/07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md)

**`Source/PuddingBrowser.Automation/`**

  🔑 浏览器自动化可靠性组件（叶子，只引用 `Pudding.Contracts`）：统一控制权状态机、页面授权与执行租约、证据化回执判定、变更类后置条件、operationId 账本；**S5 已部分落地**（登记解决方案 + DesktopService/Host 引用）

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
| **DeepSeek 网页搜索/抓取工具** | `Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeek*Tool.cs`、`Source/PuddingDeepSeekWeb/` | `deepseek_search`/`deepseek_fetch` 低风险只读（AutoAllowed 免授权）；凭据 `search.providers.json` 的 `deepseek_search` 优先、回退 `llm.providers.json` 的 `providerId=deepseek`；协议在叶子组件，传输经端口适配 |
| **浏览器控制权与页面授权** | `Source/PuddingBrowser.Automation/` + [设计方案](Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md) | 进程内**唯一** `IBrowserAutomationAuthority`：页面授权按 read/write/manage 绑定实例+世代+页面+frame，写请求复检世代；`BrowserMutationPostcondition` 统一判定变更类后置条件；唯一事实源 `BrowserPageLifecycle`（见下「关键语义」） |
| **多模态视觉链路** | `Source/PuddingRuntime/` + [ADR-077](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) | typed `ContentPart{type=image, artifactId, detail}` 同事务写入 `ChatMessages.ContentPartsJson`；文本模型只收 `artifact://` 占位 |
| **记忆与存储治理** | `Source/PuddingMemoryEngine/`、`Source/PuddingHost/Storage/` + [ADR-076](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) | Library/Book/Chapter 是 Agent 主动维护的当前结论与索引；`StorageMaintenanceCoordinator` 是唯一在线维护 writer，保留策略读 `system.json` 且 CAS + fail-closed |
| **插件 / Hook / 事件 / 投影** | `Source/PuddingCore/` + [参考架构](Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | Plugin/Function/Hook/Event/Projection 五类合同；outbox → DomainEventLog → per-consumer checkpoint/retry/dead-letter |
| **Chat 前端投影与虚拟化** | `Source/PuddingPlatformAdmin/`（`src/pages/chat/`） | 服务端出 canonical 事件；`TurnSurfaceStore` → `ExecutionFlowProjectionIndex` → `MessageViewportRuntime` → `TurnContentStream`；快照消息顺序按共享身份锚点合并，实时归属优先 window.turnId，同回合仅最新助手消息接收完整投影 |
| **认证与外部 API** | `Source/PuddingPlatform/Security/` | 看板 Access Token（`pdt_v1_` opaque、摘要存储）、External API v1（ETag/幂等/`202 + Location`）、Loopback ControlToken 双通道鉴权 |
| **前端构建与版本** | `Source/PuddingPlatformAdmin/package.json` | 版本号以 `version` 字段为准，构建期注入 `__PUDDING_FRONTEND__`；改前端必须递增版本并重新构建部署 `wwwroot/admin` |
| **可诊断基础设施** | `Source/PuddingDiagnostics/` + [设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 失败必须收敛为稳定因果码 + 阶段 + 有界证据（脱敏、键≤32/值≤512/总≤8KiB）+ 显式截断标记；分类器是纯函数，故障场景「注入 → 期望结论」是门禁；新组件不得反向引用消费方 |

**关键语义（§3「关键概念与组件」表·要点/不变量列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**浏览器控制权与页面授权**

  进程内**唯一** `IBrowserAutomationAuthority`：接管/暂停/关闭/世代改变推进控制世代，接管**不**撤销阅读授权；页面授权按 `read/write/manage` 范围绑定实例+世代+页面+frame，子代理只能取父任务子集；写请求入队签发租约、触碰页面前复检世代。**变更类后置条件由 `BrowserMutationPostcondition` 统一判定**（两条传输共用，不按动作名推断导航）。**page 
  级准入闸门**：`browser.snapshot/locate/interact/wait_for/tabs` 与 `webview.execute_javascript` 要求目标页是用户选定的 Agent 目标（context trust 是 context 
  级，不能代表页级授权）；目标登记表对**已发布事实非破坏性**（重复登记只合并、版本只进不退、"不表态"不清标记），可信级别创建后不可改写。**接管/暂停的唯一写入点**在浏览器工作区控制器（`SetUserTakeoverAsync`/`SetPausedAsync`），状态推给所有执行闸门；**唯一事实源是 Shell 组合根构造的那一个 `BrowserPageLifecycle`**（其内是 `BrowserAutomationAuthority`）：Bridge 闸门（默认路径）只读它的快照、只写同一个实例，能力通道侧的 
  `DesktopInteractionState` 也只是它的读投影（设计方案 §3.1）。**页面/上下文的创建与关闭只有一个所有者** `BrowserPageLifecycle`（唯一权威 + 两个注册表）：创建侧保证**登记原子性**（任一侧失败即回收另一侧，绝不返回半登记页面），关闭侧同时清两个注册表、该页全部授权与写租约，切换 Agent 目标时撤销旧目标执行权但保留只读（§3.2/§3.3）。**交互回执**由工具层调用一次 `BrowserInteractionReceipt` 判定：`expect_value` 
  声明期望后一次调用即确认写入，表单值只以 matched + 长度回传。**渐进阅读**由 `BrowserReadingFormat` 收敛：`browser_snapshot` 默认 `outline` 只请求无障碍树并按 50 行/4 KB 输出，输出预算与内部扫描预算分开，截断区分 `incomplete_scan` 与 `output_budget`

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
| `Source/PuddingDeepSeekWebTests/` | DeepSeek Search/Fetch 协议叶子组件独立测试：请求形状/参数校验矩阵/响应解析/HTTP 错误映射/有界重试与 `Retry-After`/传输异常与取消/端点归一 + S1–S4 边界断言（含探测器取红）+ 真实 API Live（Search→Fetch，无凭据时 Inconclusive） |
| `Source/PuddingBrowser.AutomationTests/` | 浏览器自动化可靠性组件独立测试：控制权/接管与世代、页面授权与子代理范围、回执与重试裁定、变更类后置条件、交互回执、引用身份与三个观察事实、operationId 账本、S1–S4 边界断言与用例守恒 |
| `Source/PuddingPathFilteringTests/` · `Source/PuddingVectorIndexTests/` · `Source/PuddingFullTextIndexTests/` · `Source/PuddingMemoryEngineBenchmarks/` · `Tests/PuddingBrowser.WebView2.Smoke/` | 路径忽略、向量与全文索引叶子组件 · BenchmarkDotNet 基准 · 浏览器 smoke |
| `Source/PuddingRetrievalEvalTests/` · `Source/PuddingRetrievalEvalProbe/` · `Source/PuddingFullTextIndex.Cli.Tests/` | 检索评测（`ISearchProbe` 端口）与全文索引 CLI |
| `Source/PuddingSshTests/` | SSH 组件独立测试（只引用组件，**122 例**）：known_hosts 明文/逗号/哈希/撤销与 pin 验证（**真实 OpenSSH fixture**）、POSIX 封装与请求校验、错误分类矩阵、有界采集白盒、**假传输故障矩阵（准入/排队/deadline/清理/停止）**、S1–S4 边界断言（变异取红两次均 3/5 红） |
| `Source/PuddingSsh.Probe/`（+ `TestScripts/ssh-probe/`） | SSH 组件的**真实协议探针**（24 场景）：逐场景核对认证次数与客户端密钥指纹、取消远端证据、双流输出预算与驻留内存（见下「关键语义」） |
| `Source/PuddingCodeIntelligenceTests/` · `Source/PuddingCodexServiceTests/` · `Source/PuddingBrowser.WinUITests/` · `Source/PuddingDesktop.FoundationTests/` | 语言智能层 · Codex MCP Service · WinUI 浏览器表面 · Foundation 纯逻辑 |
| `Source/Pudding.ContractsTests/` · `Source/Pudding.Rpc.ProtocolTests/` · `Source/Pudding.DesktopSurface.BrowserTests/` | 契约与 wire 协议形状断言 · 浏览器表面 |
| `Source/Pudding.DesktopConnectionTests/` · `Source/Pudding.DesktopServiceTests/` · `Source/Pudding.CapabilityBrokerTests/` · `Source/Pudding.CapabilityBroker.AspNetCoreTests/` | 能力通道四个组件的独立测试（假服务端 / 假 UI 调度器 / 自托管 Kestrel） |
| `TestScripts/BrowserDeliveryReview/` | 浏览器任务授权、原租约复检、完整请求去重及子代理身份的行为回归；[README](TestScripts/BrowserDeliveryReview/README.md) 说明显式注入与迁入常规套件的方式 |
| `Tests/PuddingDesktop.Tests/` · `Tests/PuddingHost.Tests/` · `Tests/PuddingBrowser.AgentTools.Tests/` | Desktop 进程与配置、调试模式（路由/反向代理/SSE/WS）· Bridge Endpoint · 七项 Agent Tools |
| `Tests/PuddingAgent.IntegrationTests/` · `Tests/PuddingNativeChat.IntegrationTests/` · `Tests/e2e/` · `TestScripts/` · `Tests/PuddingBrowser.TestSite/` | 集成测试 · 端到端脚本 · 生命周期/部署/smoke/性能探针 · 测试站点与门禁脚本（见下「关键语义」） |

**关键语义（§5「测试工程索引」表·覆盖列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**`Source/PuddingSsh.Probe/`（+ `TestScripts/ssh-probe/`）**

  SSH 组件的**真实协议探针**：只引用 `PuddingSsh`；`run-ssh-probe.ps1` 起受控 WSL OpenSSH 夹具，**24 个场景**（含 `coordinator` 全链路、`knownhosts` 明文/哈希/撤销/换 key、两类 `.ssh` 的 `dirs`、`ignoreterm`、`disconnect`、错账号 `auth`、cwd 守卫 `cwd`，以及 R5 的 
  `precancel`/`lateconnect`/`noeof`/`submitfail`/`rounds`/`signal`，切片 B 的 `transfer`）逐场景核对认证次数与实际使用的客户端密钥指纹、取消的远端证据、双流输出预算与驻留内存；`make-known-hosts-fixtures.sh` 生成 known_hosts fixture

**§5 行 134**

  集成测试 · 端到端脚本 · 生命周期/部署/smoke/性能探针 · 测试站点（`fixtures/` 含 SERP/正文/滚动/**页面事件**四条 fixture 路由）· 门禁脚本 `TestScripts/check-round-gates.ps1`（Front Matter/链接/BOM 总数 + `-FixLinks`）与 `TestScripts/commit-round.ps1`（先门禁后提交）· `TestScripts/check-dependent-suites.ps1`（按引用图找"谁真的加载某程序集"的测试套件，可选 
  -Run）

## 6. 架构与设计文档索引

| 文档 | 主题 |
|------|------|
| [`Docs/README.md`](Docs/README.md) · [`Agents.md`](Agents.md) · [`Agents-Hygiene.md`](Docs/10_conventions/Agents-Hygiene.md) | 文档总索引与阅读顺序 · 开发与提交纪律 · 仓库卫生 |
| [`组件化交付规程`](Docs/10_conventions/组件化交付规程.md) · [`调试入口`](Docs/08_how_debuge/README.md) · [`架构`](Docs/07_architecture/架构.md) · [`ADR 全表`](Docs/07_architecture/README.md) | 组件化交付 S1–S5 门禁（强制）· 调试与日志诊断 · 架构总览与模块分册 |
| [可诊断基础设施设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 诊断事实/类型化因果/事故查询面/故障场景资产：叶子组件边界、S1–S5 施工与门禁、查询与脱敏的强制项 |
| [工具插件化架构与实施方案](Docs/12_features/工具插件化架构与实施方案-2026-10-09.md) | 提案：不可变插件包、独立 SDK 与 Core 能力接口、Worker 故障隔离、启动元数据扫描、Agent 挂载租约与卸载、诊断闭环、Web 管理及 P0–P6 组件交付门禁 |
| [全生命周期注册点目录](Docs/12_features/Agent全生命周期Hook事件与动作注册点目录-2026-10-09.md) · [管道架构](Docs/12_features/Agent生命周期与Hook管道架构设计-2026-10-09.md) · [代码设计](Docs/12_features/Agent生命周期与Hook管道代码设计-2026-10-09.md) · [现状审计](Docs/14_reports/2026-10-09-Agent生命周期与Hook边界审计.md) | 提案：动作/Hook 注册点与管道、生命周期收口（见下「关键语义」） |
| [浏览器自动化可靠性与渐进阅读设计](Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md) | 浏览器控制权与页面授权、动作回执、稳定 ref、渐进 DOM/markdown 阅读、截图视觉和受约束委派的目标设计与交付门禁（提案） |
| [B1 身份准入与下一阶段施工](Docs/12_features/B1身份准入接线与浏览器交付下一阶段实施方案-2026-10-09.md) | Core 可信身份跨进程传递、任务授权签发、Desktop 准入/复检、B6 接线及外部验收顺序 |
| [浏览器交付修订施工](Docs/12_features/浏览器交付修订施工方案-2026-10-12.md) · [DeepSeek 施工指令](Docs/15_tasks/2026-10-13-DeepSeek浏览器修订施工指令.md) | 两条传输的共用执行器、不可变许可、任务选择绑定、完整指纹账本、Runtime 委派和原生截图输出的逐文件规格；R1–R6 顺序、反例与交付验收要求 |
| [Agent 生成式交互界面与浏览器应用运行时](Docs/12_features/Agent生成式交互界面与浏览器应用运行时设计方案-2026-10-08.md) | HTML/表单/SVG/图表/涂鸦作品、页面 SDK、持久状态、实时版本更新、具名 JS 调用与用户提交闭环的目标设计（提案） |
| [ADR-095](Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) · [上下文维护施工方案](Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) | Proposed：Session软维护与逐请求硬保护、唯一候选提交/checkpoint、请求实测与估算分离；独立策略组件先行，尚未接入 |
| [`ADR-089`](Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) · [检索设计](Docs/12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md) · [`ADR-077`](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) · [视觉设计](Docs/12_features/原生视觉与统一取图截图优化设计-2026-09-12.md) | 统一检索入口与索引组件拆分 · 原生视觉、typed parts 与截图链路 |
| [`ADR-076`](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) · [存储设计](Docs/12_features/遥测调试数据自动过期与Web存储管理设计方案.md) | 存储治理、语义目录、保留策略 |
| [`ADR-079`](Docs/07_architecture/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md) · [内容流设计](Docs/12_features/Agent消息交错内容流与最新行为组披露完整实施方案.md) | Agent 回合单一有序内容流与披露 owner |
| [`ADR-074`](Docs/07_architecture/89ADR-074Goal持久目标自主续行与自动压缩ADR.md) · [Goal 设计](Docs/12_features/Goal持久目标自主续行与自动压缩完整设计方案.md) | 持久 GoalRun、证据验证、Task-bound Goal |
| [`自动派发`](Docs/12_features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md) · [`夜间调度`](Docs/12_features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md) · [`ADR-073`](Docs/07_architecture/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md) | 自动派发 · Execution 生命周期闭环 · 产品施工总表 |
| [Token 效率](Docs/12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md) · [Harness 兼容](Docs/12_features/AgentHarness兼容与工具调用效率修复设计方案.md) · [`ADR-081`](Docs/07_architecture/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md) | 分级压缩与缓存命中验收 · Harness 兼容边界与工具协议适配 |
| [`tool-infrastructure-layering.md`](Docs/07_architecture/tool-infrastructure-layering.md) · [自动工具审批](Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md) | Tool 分层、强制委派合同 · 自动工具审批唯一设计入口 |
| [SSH 工具设计](Docs/12_features/SSH工具组件化设计与实施方案-2026-10-08.md) · [ADR-096](Docs/07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md) · [S5 前置修补](Docs/12_features/SSH-S5前置修补与代码级施工方案-2026-10-12.md) · [SSH 调研](Docs/07_architecture/71PuddingSSH远程执行能力调研报告.md) | SSH 的 `.ssh`/主机信任、远程执行与 SFTP 设计（见下「关键语义」） |
| [产品架构裁定](Docs/12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md) · [能力通道](Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) · [余额与计费](Docs/12_features/服务商余额查询与多服务商计费适配器设计方案.md) | WinUI Shell + Web UI + 独立 Core · gRPC 能力通道切片 · 余额徽标与计费适配 |
| [参考架构总蓝图](Docs/19_references/deepseek-reference-architecture-master-plan-2026-08-14.md) · [插件/Hook/事件](Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | 「一切业务能力皆插件」总蓝图（T00–T16）· Plugin/Function/Hook/Event/Projection 合同 |
| [工具系统对齐](Docs/19_references/deepseek_harness/deepseek-harness-tool-system-alignment-2026-08-14.md) · [消息卡片对齐](Docs/19_references/deepseek_harness/deepseek-harness-message-card-alignment-2026-08-14.md) | 工具 canonical output / callId / 结构化错误 / spill · 消息与推理的 UI 投影合同 |
| [`Docs/00_changelog/`](Docs/00_changelog/README.md) · `Docs/14_reports/` · `Docs/16_qa/` · `Docs/15_tasks/` | 修改日志唯一去处（规则见其 README）· 诊断与验收报告 · 验收记录 · 历史任务（只读证据，不是索引） |

**关键语义（§6「架构与设计文档索引」表·主题列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**全生命周期注册点目录**

  提案：Agent/Pudding 动作与时间、执行前后 Hook、只读事实、过滤器与管道、中央 C# 扩展方法注册、确定顺序、owner/快照、生命周期收口与渐进迁移

**SSH 工具设计**

  SSH 的 `.ssh`/主机信任、远程执行与 SFTP 设计；工作目录、终止与输出完整性合同及修补门禁

## 7. 运行时目录与构建入口

- `D:\data` — 开发环境 DataRoot（由 `dev-up.py` 的环境变量/启动参数决定，见 PathHelper）。
- `<DataRoot>/config/system.json` — Core 系统配置：端口、ControlToken、启动超时、保留策略、ToolReview 覆盖。
- `<DataRoot>/config/security.json` — 登录态 JWT（`jwt.key/issuer/audience/expiryHours`，缺省 7 天；密钥缺失时由引导期生成并写回，代码内无硬编码兜底）。
- `<DataRoot>/config/llm.providers.json` — LLM 服务商/模型配置来源（apiKey 支持 `${ENV}` / vault 引用，不回显）；DeepSeek Search/Fetch 在未配置专用节时回退读取其中 `providerId=deepseek` 的 `baseUrl`+`apiKey`。
- `<DataRoot>/config/search.providers.json` — 搜索类 provider 配置（anysearch / doubao_search / zhihu_search / deepseek_search）；`deepseek_search` 节（enabled/baseUrl/apiKey/timeoutSeconds）优先于 `llm.providers.json` 的 DeepSeek provider。
- `D:\data\workspaces\default` · `DesktopHome/desktop.json` · `.pudding-host.lock` — 默认工作空间 · Shell 侧 DataRoot/Core 路径/关闭行为 · Console 与 DesktopChild 共用的单实例文件句柄租约（不删锁文件绕互斥）。
- `.pudding/context-tool-results/` · `temp/build/` · `temp/test-out/` — 工具结果完整原文（模型只收 8 KiB 有界摘要）· 唯一允许的临时产物目录。
- `dotnet build PuddingRuntime --no-restore` · `dotnet build Source\PuddingDesktop\PuddingDesktop.csproj --no-restore --nologo` · `dotnet test Tests\PuddingDesktop.Tests\PuddingDesktop.Tests.csproj --no-restore --nologo` — 编译入口 · Desktop 定向构建与测试（build/test/publish 必须串行）。
- `python dev-up.py --frontend-only / --restart / --rebuild / --status / --down` — 源码开发态前后端与工具进程。
