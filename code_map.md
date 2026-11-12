# PuddingAgent 代码地图（根索引）

> 源指纹: Source/**=4128db41da49, Tests/**=19a07b471379, Tools/**=83d7eb33e1cf, TestScripts/**=9d8c5fa016f7 · 条目数: 128 · 最近整理: 2026-10-31
>
> ⚠️ 2026-10-28：`TestScripts/**` 与 `Source/**` 指纹均已**陈旧**——本轮新增 9 个验收/采样/运维脚本（verify-retrieval-delivery / verify-fulltext-incremental-latency / sample-host-io-shape / correlate-host-read-io / measure-retrieval-resources / watch-read-io-anomaly / verify-storage-vacuum-readiness / swap-compacted-platform-db / probe-read-io-shape），并在 `Source/PuddingPlatform/Services/StorageManagement/` 新增 `PlatformStorageReclaimPolicy.cs`（纯决策核心 + 其测试），故 `Source/**` 同样需重算。而仓库内无 code_map 指纹自检器可重算。**不编造新指纹**，只登记为待刷新；拿到算法后一次算清。
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
| `Source/PuddingDesktop.Guardian/` | 独立 BCL 进程：本地 HTTP 编译/重启命令、Desktop/Core 健康与持久程序集回滚 | [code_map](Source/PuddingDesktop.Guardian/code_map.md) |
| `Source/PuddingDesktop.GuardianTests/` · `Tests/PuddingDesktop.GuardianIntegrationTests/` | 主管独立门禁与真实 WinUI 控制面集成门禁 | [独立测试](Source/PuddingDesktop.GuardianTests/code_map.md) · [集成测试](Tests/PuddingDesktop.GuardianIntegrationTests/code_map.md) |
| `Source/PuddingDesktop.Diagnostics/` | BCL 现场记录：原子快照、脱敏、退出分类与补录 | [code_map](Source/PuddingDesktop.Diagnostics/code_map.md) |
| `Source/PuddingDesktop.ExitObserver/` | 独立观察 Shell 退出码并保存现场，不管理产品生命周期 | [code_map](Source/PuddingDesktop.ExitObserver/code_map.md) |
| `Source/PuddingAgent/` | 入口 `Program.cs`：Console / DesktopChild 两种薄壳 | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingHost/` | Core 组合根：装配 HTTP 宿主、本机控制地址与各连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingSlack/` · `Source/PuddingSlackTests/` | Slack Socket Mode、Web API、持久收件箱与不确定发送回执；BCL-only 叶子 | [组件](Source/PuddingSlack/code_map.md) · [独立测试](Source/PuddingSlackTests/code_map.md) |
| `Source/PuddingDingTalk/` · `Source/PuddingDingTalkTests/` | 企业群来源校验、群会话图文消息、公开问答与本机反馈登记 | [组件](Source/PuddingDingTalk/code_map.md) · [Core 接入](Source/PuddingHost/Services/DingTalkConversationIngress.cs) · [独立测试](Source/PuddingDingTalkTests/code_map.md) |
| `Source/PuddingRuntime/` | Agent Loop · LLM 网关调用 · 工具系统 · 上下文管线 · 子代理 · 后台学习 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/Pudding.Api.Contracts/` | 存储无关的 Agent 配置、单轮执行与确定性评分公共合同（BCL-only） | [code_map](Source/Pudding.Api.Contracts/code_map.md) |
| `Source/Pudding.Api.Sdk/` · `Source/Pudding.Api.SdkTests/` | 配置/执行门面及 HTTP 适配、确定性评分的有类型门面与独立测试 | [SDK](Source/Pudding.Api.Sdk/code_map.md) · [测试](Source/Pudding.Api.SdkTests/code_map.md) |
| `Source/PuddingAgentConfiguration.Contracts/` · `Source/PuddingAgentConfiguration/` | 授权/原子存储端口与配置更新领域组件 | [端口](Source/PuddingAgentConfiguration.Contracts/code_map.md) · [组件](Source/PuddingAgentConfiguration/code_map.md) |
| `Source/PuddingAgentConfigurationTests/` · `Tests/PuddingAgentConfiguration.IntegrationTests/` | 配置组件独立测试及 manifest/HTTP 集成测试 | [独立测试](Source/PuddingAgentConfigurationTests/code_map.md) · [集成](Tests/PuddingAgentConfiguration.IntegrationTests/code_map.md) |
| `Source/PuddingAgentWork.Contracts/` · `Source/PuddingAgentWork/` | 单轮执行授权、受理端口及调用方绑定服务；复用 canonical 执行链 | [端口](Source/PuddingAgentWork.Contracts/code_map.md) · [组件](Source/PuddingAgentWork/code_map.md) |
| `Source/PuddingAgentWorkTests/` · `Tests/PuddingAgentWork.IntegrationTests/` | 单轮执行组件测试及 SQLite/HTTP 因果回执验收 | [独立测试](Source/PuddingAgentWorkTests/code_map.md) · [集成](Tests/PuddingAgentWork.IntegrationTests/code_map.md) |
| `Source/PuddingOperatorAccess.Contracts/` · `Source/PuddingOperatorAccess/` | 算子后端端口、授权目录、输入校验、调用预算与撤销；无 Runtime/插件反向依赖 | [端口](Source/PuddingOperatorAccess.Contracts/code_map.md) · [组件](Source/PuddingOperatorAccess/code_map.md) |
| `Source/PuddingOperatorAccessTests/` · `Tests/PuddingOperatorApi.IntegrationTests/` | 算子独立边界及真实 Worker 到原技能评分器的验收 | [独立测试](Source/PuddingOperatorAccessTests/code_map.md) · [集成](Tests/PuddingOperatorApi.IntegrationTests/code_map.md) |
| `Source/PuddingExecutionResources.Contracts/` · `Source/PuddingExecutionResources/` | 父任务资源端口、分层预算预留/结算、取消与模型租约 | [端口](Source/PuddingExecutionResources.Contracts/code_map.md) · [资源组件](Source/PuddingExecutionResources/code_map.md) |
| `Source/PuddingModelEvaluation/` | 受父预算约束的文本分类与逐项证据评审 | [code_map](Source/PuddingModelEvaluation/code_map.md) |
| `Source/PuddingExecutionResourcesTests/` · `Source/PuddingModelEvaluationTests/` · `Tests/PuddingModelEvaluation.IntegrationTests/` | 独立资源与语义测试、Runtime 模型绑定、真实 Worker 与旧能力回归 | [资源测试](Source/PuddingExecutionResourcesTests/code_map.md) · [评审测试](Source/PuddingModelEvaluationTests/code_map.md) · [集成](Tests/PuddingModelEvaluation.IntegrationTests/code_map.md) |
| `Source/PuddingEventing.Contracts/` · `Source/PuddingEventing/` | 插件可靠事件与任务端口、事务 outbox、至少一次投递、原子 checkpoint、死信与代际围栏；不含 Goal/Review 业务 | [端口](Source/PuddingEventing.Contracts/code_map.md) · [组件](Source/PuddingEventing/code_map.md) |
| `Source/PuddingEventingTests/` | 事件/任务协议的独立测试（内存端口适配器、崩溃/竞争/取红） | [code_map](Source/PuddingEventingTests/code_map.md) |
| `Source/PuddingAgents.Contracts/` · `Source/PuddingAgents/` | Agent 绑定后端端口、目录借用句柄、工作观察/幂等取消、委派与原子池租约；身份由宿主注入 | [端口](Source/PuddingAgents.Contracts/code_map.md) · [组件](Source/PuddingAgents/code_map.md) |
| `Source/PuddingAgentsTests/` | Agent 目录/工作控制/委派租约的独立测试与取红证据 | [code_map](Source/PuddingAgentsTests/code_map.md) |
| `Source/PuddingReviews.Contracts/` · `Source/PuddingReviews/` | 持久 Review Job 端口与领域：逻辑唯一键、冻结输入/证据校验、建议级结论与证据级别、owner/revision 围栏与完成 outbox；不写 Goal 终态 | [端口](Source/PuddingReviews.Contracts/code_map.md) · [组件](Source/PuddingReviews/code_map.md) |
| `Source/PuddingReviewsTests/` | Review Job 幂等/围栏/证据/恢复的独立测试与取红证据 | [code_map](Source/PuddingReviewsTests/code_map.md) |
| `Source/PuddingModels.Contracts/` · `Source/PuddingModels/` | 有效模型配置（layering/CAS）、能力目录端口、可信报价冻结与租约代际；未知价格不为零、降配不抢占活跃槽 | [端口](Source/PuddingModels.Contracts/code_map.md) · [组件](Source/PuddingModels/code_map.md) |
| `Source/PuddingModelsTests/` | 配置层级/CAS、报价窗口/缓存计价、租约代际的独立测试与取红证据 | [code_map](Source/PuddingModelsTests/code_map.md) |
| `Source/PuddingGoalPolicy.Contracts/` · `Source/PuddingGoalPolicy/` | Goal 续跑纯策略：模式、策略快照、权威事实与 Continue/Wait/Complete/Stop 建议；不复制状态机、不写终态 | [端口](Source/PuddingGoalPolicy.Contracts/code_map.md) · [策略](Source/PuddingGoalPolicy/code_map.md) |
| `Source/PuddingGoalPolicyTests/` | 决策矩阵与最小闭环轨迹测试、变异取红证据 | [code_map](Source/PuddingGoalPolicyTests/code_map.md) |
| `Source/PuddingCapabilityCatalog.Contracts/` · `Source/PuddingCapabilityCatalog/` | 只读能力目录：kind/界限校验、Revision/Generation/完整性与 partial/forbidden 语义；目录项不是执行授权 | [端口](Source/PuddingCapabilityCatalog.Contracts/code_map.md) · [组件](Source/PuddingCapabilityCatalog/code_map.md) |
| `Source/PuddingCapabilityCatalogTests/` | 能力目录只读语义与取红证据 | [code_map](Source/PuddingCapabilityCatalogTests/code_map.md) |
| `Source/PuddingAuthorization.Contracts/` · `Source/PuddingAuthorization/` | 授权判定与审批生命周期：系统 Deny 优先、插件只收紧、批准绑定参数 hash/策略版本、失败不 fail-open | [端口](Source/PuddingAuthorization.Contracts/code_map.md) · [组件](Source/PuddingAuthorization/code_map.md) |
| `Source/PuddingAuthorizationTests/` | 授权/审批决策矩阵与取红证据 | [code_map](Source/PuddingAuthorizationTests/code_map.md) |
| `Source/PuddingWorkspace.Contracts/` · `Source/PuddingWorkspace/` | 工作区只读/文件策略：受权列表、逻辑环境快照、路径规范化、越界/链接拒绝、CAS 原子写 | [端口](Source/PuddingWorkspace.Contracts/code_map.md) · [组件](Source/PuddingWorkspace/code_map.md) |
| `Source/PuddingWorkspaceTests/` | 工作区路径/链接/CAS 与取红证据 | [code_map](Source/PuddingWorkspaceTests/code_map.md) |
| `Source/PuddingContext.Contracts/` · `Source/PuddingContext/` | 上下文有序构建/裁剪：canonical 顺序与当前用户围栏不变，外部数据只能是 tool，预算/贡献上限/哈希失败均 typed | [端口](Source/PuddingContext.Contracts/code_map.md) · [组件](Source/PuddingContext/code_map.md) |
| `Source/PuddingContextTests/` | 围栏/冒名/裁剪/预算与取红证据 | [code_map](Source/PuddingContextTests/code_map.md) |
| `Source/PuddingMcp.Contracts/` · `Source/PuddingMcp/` | MCP 生命周期：启动超时/失败 typed、generation 围栏、超时/不确定为 Unknown 不重试、SSE 事件游标去重/缺口 | [端口](Source/PuddingMcp.Contracts/code_map.md) · [组件](Source/PuddingMcp/code_map.md) |
| `Source/PuddingMcpTests/` | MCP 生命周期/游标/超时与取红证据 | [code_map](Source/PuddingMcpTests/code_map.md) |
| `Source/PuddingTools.Contracts/` · `Source/PuddingTools/` | 工具门面：只做搜索/检查/状态、不可见工具不泄露、未知可用性原样、源失败 typed（无执行入口） | [端口](Source/PuddingTools.Contracts/code_map.md) · [组件](Source/PuddingTools/code_map.md) |
| `Source/PuddingToolsTests/` | 工具搜索/可见性/状态与取红证据 | [code_map](Source/PuddingToolsTests/code_map.md) |
| `Source/PuddingInvocationPipeline.Contracts/` · `Source/PuddingInvocationPipeline/` | 扩展点注册与管道：Observer/Advice/Transform/Guard 分类、grant/代际围栏、Transform 不可改工具身份/授权/退出码/预算/失败 | [端口](Source/PuddingInvocationPipeline.Contracts/code_map.md) · [组件](Source/PuddingInvocationPipeline/code_map.md) |
| `Source/PuddingInvocationPipelineTests/` | 扩展注册/排序/grant/Transform 围栏与取红证据 | [code_map](Source/PuddingInvocationPipelineTests/code_map.md) |
| `Samples/AgentWorkClient/` · `Tests/AgentWorkClient.Tests/` | 单轮执行 SDK 离线消费示例与假 API 测试；不读取凭据或连接宿主 | [示例](Samples/AgentWorkClient/code_map.md) · [测试](Tests/AgentWorkClient.Tests/code_map.md) |
| `Samples/AgentDirectoryClient/` · `Tests/AgentDirectoryClient.Tests/` | 代理目录离线消费示例与假 API 测试；借用句柄只读，不创建/翻页/连宿主 | [示例](Samples/AgentDirectoryClient/code_map.md) · [测试](Tests/AgentDirectoryClient.Tests/code_map.md) |
| `Samples/AgentModelPolicyClient/` · `Tests/AgentModelPolicyClient.Tests/` | 有效模型配置离线只读示例与假 API 测试；不实现 Update、不读配置、不发模型请求 | [示例](Samples/AgentModelPolicyClient/code_map.md) · [测试](Tests/AgentModelPolicyClient.Tests/code_map.md) |
| `Samples/ExecutionBudgetClient/` · `Tests/ExecutionBudgetClient.Tests/` | 跨轮预算快照离线示例与假 API 测试；Unknown/null 上限原样保留，不计算可用余额 | [示例](Samples/ExecutionBudgetClient/code_map.md) · [测试](Tests/ExecutionBudgetClient.Tests/code_map.md) |
| `Samples/GoalDecisionPresenter/` · `Tests/GoalDecisionPresenter.Tests/` | Goal 决策只读展示函数与 golden 逐字测试；不补写结论、不推导状态 | [示例](Samples/GoalDecisionPresenter/code_map.md) · [测试](Tests/GoalDecisionPresenter.Tests/code_map.md) |
| `Samples/CapabilityCatalogClient/` · `Tests/CapabilityCatalogClient.Tests/` | 能力目录只读示例与假 API 测试；partial/Forbidden 原样，不执行工具/MCP | [示例](Samples/CapabilityCatalogClient/code_map.md) · [测试](Tests/CapabilityCatalogClient.Tests/code_map.md) |
| `Samples/Plugins/ReviewAudit/` · `Tests/ReviewAuditPlugin.Tests/` | 只读审计插件示例：观察 review.completed 写一条幂等审计记录；不发消息/不执行工具 | [示例](Samples/Plugins/ReviewAudit/code_map.md) · [测试](Tests/ReviewAuditPlugin.Tests/code_map.md) |
| `Samples/ModelEvaluationClient/` · `Tests/ModelEvaluationClient.Tests/` | 模型分类/证据评审离线消费示例与假 API 测试；只回显回执，不连宿主、不生成 CallId | [示例](Samples/ModelEvaluationClient/code_map.md) · [测试](Tests/ModelEvaluationClient.Tests/code_map.md) |
| `Source/Pudding.ToolPlugins.Contracts/` | 无宿主依赖的插件合同与 Worker 端口 | [code_map](Source/Pudding.ToolPlugins.Contracts/code_map.md) |
| `Source/Pudding.ToolPlugins.Sdk/` | 插件作者基类；只引用 Contracts | [code_map](Source/Pudding.ToolPlugins.Sdk/code_map.md) |
| `Source/Pudding.ToolPlugins.AgentSdk/` · `Source/Pudding.ToolPlugins.AgentSdkTests/` | 插件到公共配置与确定性评分 SDK 的有类型适配；不接受调用方身份或任意目标 | [SDK](Source/Pudding.ToolPlugins.AgentSdk/code_map.md) · [测试](Source/Pudding.ToolPlugins.AgentSdkTests/code_map.md) |
| `Source/PuddingAgentDelegation/` · `Source/PuddingAgentDelegationTests/` | 当前调用的配置授权租约、额度、到期与撤销排空；复用公共配置组件 | [组件](Source/PuddingAgentDelegation/code_map.md) · [测试](Source/PuddingAgentDelegationTests/code_map.md) |
| `Source/Pudding.ToolPlugins.Protocol/` | 有界命名管道帧、版本与请求关联 | [code_map](Source/Pudding.ToolPlugins.Protocol/code_map.md) |
| `Source/Pudding.ToolPlugins.Runtime/` | 不可变包、作用域实例、工具与建议 Hook、Core 服务 | [code_map](Source/Pudding.ToolPlugins.Runtime/code_map.md) |
| `Source/Pudding.ToolPlugins.Windows/` | Windows Job 与同用户命名管道监督 | [code_map](Source/Pudding.ToolPlugins.Windows/code_map.md) |
| `Source/Pudding.ToolPlugins.Worker/` | 唯一装载插件 DLL 的独立进程，附发布 SDK 与示例 | [code_map](Source/Pudding.ToolPlugins.Worker/code_map.md) |
| `Source/Pudding.ToolPlugins.RuntimeTests/` | 包、权限、状态与租约独立测试 | [code_map](Source/Pudding.ToolPlugins.RuntimeTests/code_map.md) |
| `Tests/PuddingToolPlugins.Fixture/` | 真实 DLL 故障注入夹具 | [code_map](Tests/PuddingToolPlugins.Fixture/code_map.md) |
| `Tests/PuddingToolPlugins.IntegrationTests/` | Worker、子进程回收与隔离验收 | [code_map](Tests/PuddingToolPlugins.IntegrationTests/code_map.md) |
| `Tests/PuddingPluginAgentApi.IntegrationTests/` | 真实 Worker → Runtime broker → 既有配置 writer；CAS、去重、作用域、卸载与重载 | [code_map](Tests/PuddingPluginAgentApi.IntegrationTests/code_map.md) |
| `Tests/PuddingToolPlugins.HostTests/` | Buffered/Streaming 同任务动态工具与权限验收 | [code_map](Tests/PuddingToolPlugins.HostTests/code_map.md) |
| `Source/PuddingCore/` | 跨层抽象与契约：接口与基础模型类型（含 Goal 与编排） | [code_map](Source/PuddingCore/code_map.md) |
| 渠道开发权限 | 工具归属工作区与 Agent；渠道绑定识别发送者，Core 执行时核对操作权限 | [策略服务](Source/PuddingPlatform/Services/ChannelDevelopmentTrust.cs) · [渠道设置](Source/PuddingPlatformAdmin/src/pages/workspace/ChannelTrustSettings.tsx) |
| `Source/PuddingPlatform/` | Session · Web 与外部 API · EF Core + SQLite · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | Library/Book/Chapter 记忆库 · FTS5 · 潜意识检索 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingMemoryEngineBenchmarks/` | 记忆库与记忆召回的 BenchmarkDotNet 基准工程（不参与运行时装配） | — |
| `Source/PuddingGateway/` | LLM 服务商网关适配：openai / responses / anthropic 三协议 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端（Chat 工作台与任务看板等页面）；`dist` 部署到 Core `wwwroot/admin` | [code_map](Source/PuddingPlatformAdmin/code_map.md) |
| `Source/PuddingTaskRecall.Cli/` | 历史脏数据诊断/修复 CLI（默认 dry-run，`--apply` 才写库） | [code_map](Source/PuddingTaskRecall.Cli/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 工具声明（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/PuddingFinance/` | 金融数据源零依赖叶子组件：必盈端点注册与响应整形 + 官方源采集（白名单/重试/有界读取/SHA-256）+ 巨潮公告查询（分页/PDF 直链/东八区归属日/选标规则） | — |
| `Source/PuddingFinance.Documents/` | PDF 文本抽取的独立组件：唯一引用 PdfPig 的地方，对外只暴露 `IPdfTextSource` | — |
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
| `Source/PuddingDesktop.Foundation/` | BCL-only 布局/外观偏好、启动面板文案与设计令牌（字号/圆角/颜色）+ XAML 字面量与 {ThemeResource} 键集门禁 | [code_map](Source/PuddingDesktop.Foundation/code_map.md) |
| `Source/PuddingDesktop.WpfArchive/` | 旧 WPF 入口/验证基线；无 UI 的启动器与协议源文件由 WinUI 项目链接编译 | [code_map](Source/PuddingDesktop.WpfArchive/code_map.md) |
| `Source/Pudding.Rpc.IpcProbe/` | 真实端点技术探针（Named Pipe / h2c 服务端替身，退出码 0/1） | — |
| `Source/PuddingCodeIndex/` | 索引组件：契约与变更捕获管线及范围注册；不得引用 `PuddingCodeIntelligence` | [code_map](Source/PuddingCodeIndex/code_map.md) |
| `Source/PuddingDomain.Contracts/` | 跨层共享值类型（BCL-only）：`CodeSymbolKind` / `CodeRelationKind`；命名空间刻意保持 `PuddingCodeIndex.Contracts`（零调用方改动） | [code_map](Source/PuddingDomain.Contracts/code_map.md) |
| `Source/PuddingRetrieval.Contracts/` | 跨 provider 检索契约（请求 / 结果 / 覆盖声明 / 能力矩阵）；只依赖 `PuddingDomain.Contracts` | [code_map](Source/PuddingRetrieval.Contracts/code_map.md) |
| `Source/PuddingGrep/` | grep 字面量/正则**独立匹配内核**（BCL-only，零工程依赖）。**生产端零引用**（2026-11-01 实测：全仓仅本工程与其测试工程提到它）—— `Runtime/SearchGrepTool` 仍用自带实现，语义收归单一真源**未做** | [code_map](Source/PuddingGrep/code_map.md) |
| `Source/PuddingContextPolicy/` | 上下文容量与压缩候选的**纯策略**组件（BCL-only）：容量算术、候选边界与指纹、严格适用校验、净收益准入、退避判定（见下「关键语义」） | [code_map](Source/PuddingContextPolicy/code_map.md) |
| `Source/PuddingContextPruning/` | 零 LLM 确定性载荷剪枝：工具结果去重与超大载荷裁剪，保护尾部（见下「关键语义」） | [code_map](Source/PuddingContextPruning/code_map.md) |
| `Source/PuddingContextHandoff/` | 工作模型交接摘要（BCL-only）：有界不可变笔记、会话/Agent 隔离与恢复定位 | [code_map](Source/PuddingContextHandoff/code_map.md) |
| `Source/PuddingConversationReadModel/` | 会话首屏读模型纯规则组件（BCL-only；C2 已接入 Platform/slnx）：事件归约、预算与三游标合同 | [code_map](Source/PuddingConversationReadModel/code_map.md) |
| `Source/PuddingSsh/` | SSH 远程执行叶子组件（`ProjectReference=0`，包 `SSH.NET`）：合同/端口与错误分类、协调器与独占连接（见下「关键语义」） | [code_map](Source/PuddingSsh/code_map.md) |
| `Source/PuddingHomeAssistant/` | Home Assistant 叶子组件：REST 客户端、域策略三档、输出预算与历史裁剪；四个工具见 code_map | [code_map](Source/PuddingHomeAssistant/code_map.md) |
| `Source/PuddingDeepSeekWeb/` | 🔑 DeepSeek Search/Fetch 协议叶子组件（零引用；传输是端口，不直连 HTTP/不持凭据） | [code_map](Source/PuddingDeepSeekWeb/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 语言智能与查询：Roslyn 与 TypeScript 抽取及 outliner 与符号查询 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingIndexChunking/` | 分块组件：源文件 → 可独立索引的块（叶子，outline 由 `IOutlineSource` 注入） | [code_map](Source/PuddingIndexChunking/code_map.md) |
| `Source/PuddingHtml/` | HTML 正文抽取 + Markdown 转换叶子组件（零引用；`http_fetch` 与 `browser_snapshot` 的 text 模式共用） | [code_map](Source/PuddingHtml/code_map.md) |
| `Source/PuddingPathFiltering/` | 路径忽略合同的权威实现（噪声目录名 + `.gitignore` 语义） | [code_map](Source/PuddingPathFiltering/code_map.md) |
| `Source/PuddingTokenization/` | 按模型自己的词表做 token 计数的叶子组件（`ProjectReference=0`，包 `Microsoft.ML.Tokenizers`）：DeepSeek 全链路预分词，回退带原因可观测 | [code_map](Source/PuddingTokenization/code_map.md) |
| `Source/PuddingVectorIndex/` | 向量索引组件：文本 → 向量 → 内存有界 top-k（叶子，`IEmbeddingProvider` 端口） | [code_map](Source/PuddingVectorIndex/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎（Lucene）与供给协调 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingFullTextIndex.Cli/` | 全文索引供给的离线驱动工具（`plan` / `status` / `build` / `cancel`） | [code_map](Source/PuddingFullTextIndex.Cli/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingLlmUsage.Storage/` | 余额采样的 SQLite 适配器：尝试表 + 币种观测表分离（失败也留痕）、金额存 TEXT 保精度、Running 遗留由启动扫描改写为 Interrupted。独立库 `llm_usage.db`，不塞进平台大库。测试工程 `PuddingLlmUsageStorageTests`（12 条）——**尚未进 slnx** | — |
| `Source/PuddingLlmUsage/` | LLM 余额消耗判定（叶子，零工程依赖）：观测→区间的状态机（消耗 / 充值 / 持平 / 缺口 / 无法判定）+ 窗口汇总。**S1–S4 已完成**，测试工程 `PuddingLlmUsageTests` 独立运行（17 条），尚未进 slnx（S5 时登记） | — |
| `Source/PuddingRetrievalEval/` | 检索评测组件（叶子，只依赖 `ISearchProbe` 端口） | [code_map](Source/PuddingRetrievalEval/code_map.md) |
| `Source/PuddingRetrievalEvalProbe/` | 检索评测的真实探针入口：语料构建 + Lucene/向量/RRF 混合检索的可复现测量；R0-02 起新增 `--mode production`（直接驱动生产 `IRetrievalPort`，快照冻结 + 环境冻结块） | — |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约（驱动与 Agent 工具共用） | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议 | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver（DOM/元素/页面操作） | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 八项 `browser_*` 工具类；canonical 集合限制见子索引 | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.WinUI/` | WinUI 浏览器表面宿主（与 WPF 适配层共享驱动源文件） | [code_map](Source/PuddingBrowser.WinUI/code_map.md) |
| `Source/PuddingBrowser.Authorization/` | 独立会话授权规则，由 DesktopService 持有；票据/写世代与可信 owner，见 ADR-097 | [code_map](Source/PuddingBrowser.Authorization/code_map.md) |
| `Source/PuddingBrowser.Automation/` | 🔑 浏览器自动化可靠性组件（叶子）：统一控制权状态机、页面授权与执行租约、证据化回执判定、变更类后置条件、operationId 账本（见下「关键语义」） | [code_map](Source/PuddingBrowser.Automation/code_map.md) |
| `Source/PuddingComputer.Windows/` | Windows Computer Use：物理截图、候选 ID、原生焦点与有回执的输入；Host computer_observe → 共享观察会话 → Runtime computer_input → 新截图验证 | [code_map](Source/PuddingComputer.Windows/code_map.md) |
| `Source/PuddingDiagnostics/` | 🔑 可诊断基础设施叶子组件：稳定因果码/分类器/有界证据/脱敏/事故投影/故障场景；不得引用任何 Pudding 程序集 | [code_map](Source/PuddingDiagnostics/code_map.md) |

**关键语义（§2「子项目索引」表·用途列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**`Source/Pudding.DesktopService/`**

  Desktop 侧能力服务：准入与 UI 调度边界及宿主装配；另含两段式验收判定（`DesktopAcceptance`）与交付清单链（`DeliveryManifest` / 生成器 / TRX 解析 / 日志提取 / 目录采集）

**`Source/Pudding.DeliveryManifest.Cli/`**

  交付清单**采集入口**（B7 内部一侧）：目录 → 自检清单 → JSON；不合格以退出码 2 拒绝产出。配合 `TestScripts/delivery-manifest.ps1` 使用

**`Source/PuddingContextPolicy/`**

  上下文容量与压缩候选的**纯策略**组件（BCL-only，`ProjectReference`/`PackageReference` 均为 0）：容量算术、候选边界与指纹、严格适用校验、净收益准入、退避判定；不负责计时器/数据库/网络/锁/后台任务。**已被 Runtime 消费**：候选选择、双预算、严格 CAS 与净收益准入；静态纯策略不需要 DI

**`Source/PuddingContextPruning/`**

零 LLM 确定性载荷剪枝（叶子，只引用 `PuddingCore`）：相同工具结果去重、超大工具结果首尾摘要、超大工具参数截断，按序执行，保护尾部并校验准入地板；Runtime 在 `LlmRequestBudgetGuard.Prepare` 冻结测量前消费。

**`Source/PuddingSsh/`**

  SSH 远程执行叶子组件（`ProjectReference=0`，包 `SSH.NET` 2026.0.0）：合同/端口、known_hosts/pin、POSIX 封装、错误分类、协调器与独占连接；不解析 DataRoot/Agent 主体、不做审批与持久化。宿主接入前置约束见 [ADR-096](Docs/07_architecture/110ADR-096SSH执行前置围栏与有界终止ADR.md)

**`Source/PuddingBrowser.Automation/`**

  🔑 浏览器自动化可靠性组件（叶子，只引用 `Pudding.Contracts`）：统一控制权状态机、页面授权与执行租约、证据化回执判定、变更类后置条件、operationId 账本；**S5 已部分落地**（登记解决方案 + DesktopService/Host 引用）

## 3. 关键概念与组件

| 概念 | 权威位置 | 要点 / 不变量 |
|------|----------|----------------|
| **Turn 执行与上下文** | `Source/PuddingRuntime/Services/` | 一次请求 = 一个 canonical Turn；`ContextPipeline`/`ContextWindowManager`/`ContextCompactionService` 分层组装+绝对窗口压缩；`AgentExecutionService` 用 `[CURRENT USER TURN input_sha256=…]` 围栏输入，缺失即拒绝；实例提示词经 `AgentPromptSnapshot` 按 manifest 读取，正文指纹驱动下一轮刷新 |
| **Agent 配置公共 API** | SDK → 配置组件 → `WorkspaceAgentFileService.Configuration.cs` | 名称/描述窄补丁、版本冲突、按调用者去重、同文件原子回执与事件；不暴露 JSON/数据库；管理端 JWT 授权，插件委托授权尚未开放 |
| **Agent 单轮执行公共 API** | `AgentWork` SDK → `AgentWorkService` → `CanonicalAgentWorkBackend` → 既有受理与 Journal | 稳定操作 ID；原子输入/回执/排队；精确 Turn/Command/Run 终态证据；Completed 不等于目标验收；[契约与接入](Docs/12_features/Agent单轮执行API-P2-契约与接入说明-2026-11-05.md) |
| **插件 Agent 配置能力** | `Context.AgentConfiguration()` → 调用租约 → 既有配置组件与 Store | Core API 1.1；按当前执行 Agent/session 授权；写入需工具 effect；工作提交尚未开放；[设计与算子开放计划](Docs/12_features/插件Agent配置租约与算子开放设计-2026-11-05.md) |
| **判定算子公共基础** | `Source/PuddingCore/Operators/` · `Source/PuddingRuntime/Operators/` | 既有评分、判断、分类投影及场景注册表；消费能力与注册/替换权限分离；[开放计划](Docs/12_features/插件Agent配置租约与算子开放设计-2026-11-05.md) |
| **模型分类与证据验收** | `Context.ModelEvaluations()` → `ModelEvaluationApiFactory` → `PuddingModelEvaluation` → `IModelResourcePool` | Core API 1.3；宿主父身份/预算、单次模型派发、未知用量不返还、取消后排空；结果为建议，不直接完成 Goal；[合同](Docs/12_features/父任务预算与模型算子API-契约与接入-2026-11-05.md) |
| **插件与上层确定性评分** | `Context.Operators()` → 独立调用租约 → `SkillValueAccessAdapter` → 原评分器 | Core API 1.2；保留量纲、原因与证据；数据不足不变成零分；[合同](Docs/12_features/确定性算子公共API-契约与接入-2026-11-05.md) |
| **飞书主动收件人** | `Source/PuddingCore/Platform/FeishuRecipientContracts.cs` | `list_feishu_recipients` 查询绑定渠道白名单；`send_message` 按 Open ID 发送，无历史消息依赖；单人默认，多人显式选择 |
| **Slack 渠道** | `Source/PuddingSlack/` → `Source/PuddingHost/Connectors/SlackConnector.cs` → Message Gateway → Web 渠道会话 | 持久化后 ACK；DM / @提及；`slack_lookup_member` 绑定当前路由查成员 ID；显式提及与 bot 防循环；线程隔离和过程可见；Unknown 不盲目重试；[接入](Docs/01_message_channels/Slack接入.md) |
| **Message Fabric / send_message** | `Source/PuddingPlatform/Services/` | `MessageDeliveryPolicy` + `MessageDeliveryDispatcher`：inform/report_result 只通知，仅 ask/request_review/delegate 执行；`agent_reply` 永远被动 |
| **Task 自动派发 / Goal / WorkUnit** | `Source/PuddingPlatform/Services/Scheduling/` | availability → backlog 精炼 → Plan/WorkUnit 编译 → 五态跟踪；promotion/start/repair 只有一个 CAS 写入者 |
| **工具系统与审批** | `Source/PuddingRuntime/Tools/` + [自动审批设计](Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md) | 复杂任务前三次内进入 `smart_*` 或 `spawn_sub_agent`；只读/构建秒放、危险秒拒、未知形态交 LLM 审批；工具 schema 投递受 `toolExposure` 预算约束（默认 48 个/40 KiB，turn 边界按最近使用淘汰非核心，`search_tools` 可重载） |
| **代码索引组件族** | §2 + [ADR-089](Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) · [目标边界](Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md) | CodeIndex 不反向引用 CodeIntelligence；覆盖否定、生产向量及多语言 provider 边界以设计为准；拟新增工程不等于已有组件 |
| **Desktop ↔ Core 能力通道** | §2 + [能力通道](Docs/12_features/Desktop-Contracts-Grpc-Capability-Plan-2026-10-01.md) | 两端只经 `Pudding.Contracts`（DTO）与 `Pudding.Rpc.Protocol`（wire）；启用前必须过 `DesktopCapabilityChannelPreflight` |
| **Core 启动与就绪契约** | `Source/PuddingHost/Hosting/`、`Source/PuddingDesktop/Hosting/` | 每 5s 发 `PUDDING_DESKTOP_STARTING`，全部 hosted service `StartAsync` 返回后才发 `PUDDING_DESKTOP_READY`；`/health/ready` 不可省 |
| **Desktop 意外退出现场** | `Source/PuddingDesktop.Diagnostics/`、`Source/PuddingDesktop.ExitObserver/` | Shell 每 5 秒保存状态；独立观察器记录退出码；同时终止时下次启动补录。关闭请求不等于关闭完成，来源未知时不猜测 |
| **Desktop 启动状态面板** | `Source/PuddingDesktop/MainWindow.xaml[.cs]` + `Source/PuddingDesktop.Foundation/StartupPanelPresenter.cs` | 启动/取消/自动恢复/加载工作台/失败共用一张文案映射表（Foundation 纯逻辑）；真实阶段只从 `CoreLogBuffer` 的 `[StartupPhase]` 行聚合（`StartupPhaseActivity`）；取消复用 `_startCts`，WebView2 加载有 20 s watchdog |
| **浏览器自动化链路** | `Source/PuddingHost/BrowserBridge/`、`Source/PuddingBrowser.WebView2/` | ref 调用携带观察来源 expected_page_version，含 Bridge 动作前 locate；快照游标进入分页器、包围盒穿过结果适配器；动作后新观察，收包与串行执行分离，详见 ADR-097 |
| **DeepSeek 网页搜索/抓取工具** | `Source/PuddingRuntime/Tools/BuiltIns/Search/DeepSeek*Tool.cs`、`Source/PuddingDeepSeekWeb/` | `deepseek_search`/`deepseek_fetch` 低风险只读（AutoAllowed 免授权）；凭据 `search.providers.json` 的 `deepseek_search` 优先、回退 `llm.providers.json` 的 `providerId=deepseek`；协议在叶子组件，传输经端口适配 |
| **浏览器控制权与页面授权** | `Source/Pudding.DesktopService/BrowserPageLifecycle.cs` + [ADR-097](Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) | Bridge 会话准入与工作区执行器；channel 保留旧权威路径；响应合同须与 Core 消费者配对（见下「关键语义」） |
| **多模态视觉链路** | `Source/PuddingRuntime/` + [ADR-077](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) | typed `ContentPart{type=image, artifactId, detail}` 同事务写入 `ChatMessages.ContentPartsJson`；文本模型只收 `artifact://` 占位 |
| **记忆与存储治理** | `Source/PuddingMemoryEngine/`、`Source/PuddingHost/Storage/` + [ADR-076](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) | Library/Book/Chapter 是 Agent 主动维护的当前结论与索引；`StorageMaintenanceCoordinator` 是唯一在线维护 writer，保留策略读 `system.json` 且 CAS + fail-closed |
| **产品工具内核与按会话挂载** | [产品工具清单](Source/PuddingHost/ToolPackages/) · [插件运行手册](Docs/04_tools_and_skills/运行时工具插件.md) | 宿主按工作区、Agent、会话激活工具，按实际 Agent 策略发现，并在调用时执行授权。Agent 自建 DLL 从该轮执行工作目录导入 |
| **插件 / Hook / 事件 / 投影** | `Source/PuddingCore/` + [参考架构](Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md) | Plugin/Function/Hook/Event/Projection 五类合同；outbox → DomainEventLog → per-consumer checkpoint/retry/dead-letter |
| **Agent 自建工具插件** | `plugin_import/load/unload` → `ExecutablePluginService` → `PluginManager` → Windows Worker | 会话/实际 Agent 作用域；代号工具名；下轮模型清单刷新；建议 Hook 保留执行事实；[使用与编写](Docs/04_tools_and_skills/运行时工具插件.md) |
| **Chat 前端投影与虚拟化** | `Source/PuddingPlatformAdmin/`（`src/pages/chat/`） | 服务端出 canonical 事件；`TurnSurfaceStore` → `ExecutionFlowProjectionIndex` → `MessageViewportRuntime` → `TurnContentStream`；快照消息顺序按共享身份锚点合并，实时归属优先 window.turnId，同回合仅最新助手消息接收完整投影；用户行空占位不代表助手完成，终态按 messageId 保留回复再切换活动 Turn |
| **认证与外部 API** | `Source/PuddingPlatform/Security/` | 看板 Access Token（`pdt_v1_` opaque、摘要存储）、External API v1（ETag/幂等/`202 + Location`）、Loopback ControlToken 双通道鉴权 |
| **外部 Agent 消息客户端** | [.pudding/skills/puddingagent-message/SKILL.md](.pudding/skills/puddingagent-message/SKILL.md) | Codex / 外部客户端向 PuddingAgent Agent 投递；凭据在仓库外，幂等请求与回执落盘，区分接单、会话 ACK 和终态回复 |
| **Codex 会话 HTTP 插件** | [插件与接口](Tools/CodexPlugins/plugins/chat-http/README.md) · [隔离测试](TestScripts/test-codex-chat-http.mjs) | 本机客户端通过回环 HTTP 管理 Codex 聊天、投递消息和读取可见历史 |
| **前端构建与版本** | `Source/PuddingPlatformAdmin/package.json` | 版本号以 `version` 字段为准，构建期注入 `__PUDDING_FRONTEND__`；改前端必须递增版本并重新构建部署 `wwwroot/admin` |
| **可诊断基础设施** | `Source/PuddingDiagnostics/` + [设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 失败必须收敛为稳定因果码 + 阶段 + 有界证据（脱敏、键≤32/值≤512/总≤8KiB）+ 显式截断标记；分类器是纯函数，故障场景「注入 → 期望结论」是门禁；新组件不得反向引用消费方 |
| **数据库语句级统计探针** | `Source/PuddingPlatform/Data/Diagnostics/` + `Source/PuddingHost/Services/SqlStatementMetricsReportService.cs` | 回答"哪条 SQL 被跑了几百万次"——文件级观测（句柄/IO 计数器/ETW）只能说明在读哪个文件，这一层才说明是哪条查询。EF `DbCommandInterceptor` 按**归一化 SQL 形状**累计 calls/avg/max/rows；`SqlStatementKey` 只替换字符串字面量、参数、数字字面量，且数字必须先检查是否属于标识符（否则 table1/table2 会被错合并），字符串必须先于参数（否则字符串里的 @x 被误当参数）；`SqlStatementMetrics` 有界 512、溢出归 `<overflow>` 且继续计数；行数未知记 `unknown` 而非 0；**默认关闭**，配置 `Diagnostics:SqlStatementMetrics:Enabled=true` 才挂拦截器并每分钟落 Top10 日志 |
| **会话失败报告链路** | `TurnExecutorAdapter` → `TurnTerminalInfo.Diagnostic` → `ExecutionRunCoordinator` → `SqliteExecutionJournal` → `ErrorDiagnosticPanel` | 脱敏报告随 canonical turn.failed 持久化；实时透传，历史通过 ConversationOpenQueryService 按 conversation/turn 显式点查；报告与日志共用 Error ID |

**关键语义（§3「关键概念与组件」表·要点/不变量列逐字原文）**

> 上表超长单元格已缩为语义摘要并以 `（见下「关键语义」）` 指回本段；下列为**逐字原文**（行序与上表一致），折行为软换行、字面不增删。

**浏览器控制权与页面授权**

`BrowserPageLifecycle` 持有会话权威与页面登记；Bridge dispatcher 管准入、复检、释放和账本，ExecutorRoute 映射到工作区执行器，ResultAdapter 负责命令级响应。产品的上下文/页签管理经 `IBrowserWorkspaceLifecycle` 进入 `BrowserWorkspaceController`，创建成功须完成页签投影、生命周期登记与所需激活；
每页保留所属 ContextId。Host 代理严格消费 Protocol DTO，单项/列表/无值不可混用；发现元数据与页面控制分离。channel 旧权威路径及 handler 回退尚保留；目标边界见 [ADR-097](Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) 与[契约修复方案](Docs/12_features/浏览器发现契约与配套部署修复方案-2026-10-18.md)。
Snapshot ref 绑定页面版本，动作后以新观察验证。真实空白文档由驱动签发正数版本；`BrowserWorkspaceTargetBridge.ObserveAgentTarget` 观察 UI 目标快照，空/重复快照不等于用户撤权。显式选页/撤权与页面关闭分别走命令及生命周期端口。

压缩现场链路：`AgentExecutionService` → `BoundedCompactionSummary`（逐段决策、会话级完整分段复用）→ `CompactionDiagnosticJournal`（本地有界脱敏记录）；准备以有效进展续期，提交只在前台安全边界；`compactionId` 连接计划、分段请求/返回、取消原因、最终提交与错误报告，参见 [Runtime 地图](Source/PuddingRuntime/code_map.md)。

压缩原文回查：宿主 `CompactionEvidenceLocator` → `query_session_logs` → `ICompactionEvidenceReader` / `CompactionEvidenceReader` → 已提交覆盖清单 → canonical Turn ID 或旧 checkpoint。`RawSessionLogService` 按源会话与 Turn 读取原始转录；无 Turn ID 的运行期归档按有界窗口读取。来源关系独立于摘要正文，重复压缩不依赖模型记住旧 ID。

手动压缩调用链：`RequestCompactionHandler` → `ContextCompactionService`（保存摘要）→ `CompactionSessionSuccessor`（主会话转移）→ 持久终态。各阶段分别持久化；`CompactionFailure` 分离摘要提交与切换/通知失败，HTTP 和事件共享 errorId；跳过不转移会话。会话 JSON 先持久化再发布内存，Windows 文件占用仅做有界原子替换重试。

工作模型交接：`ContextHandoffHook` 在压力升高时建议当前模型调用 `context_handoff` → `PuddingContextHandoff/HandoffStore` 保存有界、按会话隔离的不可变笔记 → 压缩器保留续接状态及恢复入口；笔记不替代原始证据或后续用户纠正。见[交接摘要设计](Docs/12_features/2026-11-01-上下文交接摘要与压缩保真.md)。

## 4. 关键调用链路

| 层 / 项目 | 符号 | 一句话作用 |
|----------|------|----------|
| `Desktop → Core` | `PuddingDesktop/Hosting` → `PuddingHost/Hosting` | Shell 以子进程点火 Core：`STARTING`（每 5s 带协议/PID/序号）→ 全部 hosted service 就绪后 `READY` |
| `bootstrap_reboot → Guardian → Desktop → Core` | `PuddingRuntime/Tools/BuiltIns/Management/BootstrapRebootTool.cs` → `PuddingDesktop.Guardian` → `PuddingDesktop/Hosting/DesktopApplicationCoordinator.Guardian.cs` | 独立进程先构建候选，认证 HTTP 控制停止/启动，UI 响应与 readiness 稳定验收；快照排除 Core 安装根下运行数据，失败恢复旧包，不回滚用户数据 |
| `Core 组合根` | `Source/PuddingHost/` | HTTP 宿主、本机控制地址、Browser Bridge、飞书连接器的装配起点 |
| `SDK / JWT 管理入口 → 单轮执行` | `IAgentWorkApi` → `AgentWorkService` → `CanonicalAgentWorkBackend` → `ISubmitTurnHandler` | 复用原子受理与 worker；以操作 ID 查回确切 Turn/Command 的终态，不创建另一套 Work 状态 |
| `入口薄壳` | `Source/PuddingAgent/Program.cs` | Console 与 DesktopChild 两种模式分派；Console 只用于开发/诊断 |
| `WebUI → Runtime` | `TurnExecutorAdapter` → `AgentExecutionAdmissionCoordinator` | 前台 Turn 准入并抢占同 agent 后台执行，旧执行立即 defer 回队列 |
| `Runtime` | `ContextPipeline` → `AgentExecutionService` | 组装 stable system prefix + volatile User tail，再用 `[CURRENT USER TURN input_sha256=…]` 围栏本轮输入（缺失 fail-closed） |
| `Runtime → Gateway` | `LlmInvocationService` → `DirectLlmClient` | 按 `model.protocol` 路由 openai / responses / anthropic 三协议网关 |
| `Gateway → Platform` | `ILlmGatewayUsageRecorder` → `llm_gateway_usage_events` | usage 落库后供 StatsApi 与日聚合取本地计费口径 |
| `Runtime` | `PrefixCompactionPolicy` → `WarmPrefixCompaction` / `BoundedCompactionSummary` → 持久 CAS | 按注册模型窗口和配置比例提前准备，前缀保持稳定；达到同步阈值等待，完整批次边界重验冻结前缀、保留追加消息并按当前出站信封重新计量。新工具结果首次入场限幅并保存原文；协议层区分应用摘要与原生 reasoning。设计见 [前缀缓存友好的会话压缩](Docs/07_architecture/前缀缓存友好的会话压缩.md) |
| `Runtime` | `ToolExposurePlanner` → `BuildFrozenToolManifest` → L1-TOOLS / provider tools | 每个 turn 冻结有界可见集（核心 ∪ 最近使用的非核心，默认 48 个/40 KiB，`toolExposure` 配置）；L1 Available/Deferred 名单与 schema 投递面同口径；turn 内只追加，淘汰仅发生在 turn 边界 |
| `Platform` | `MessageDeliveryPolicy` → `MessageDeliveryDispatcher` | inform/report_result 只通知；ask/request_review/delegate 才进执行 |
| `Runtime` | `SubAgentInvocationService` → `SubAgentManager` | 子代理委派：model 须为 providerId/modelId；run archive 固化实际预算 |
| `Platform` | `TaskAutoDispatchWorker` → `TaskExecutionTracker` | 5 min 有界派发：availability → backlog 精炼 → Plan/Node 编译 → 五态跟踪 |
| `Platform` | `GoalContinuationWorker` → `GoalSettlementStore` | 续行前重读 Command→Goal→Binding→Plan/Node 围栏；终态原子结算 |
| `Runtime` | `search_tools` + `ToolApprovalCommandFirewall` | 工具发现默认 3 / 上限 8；只读与构建秒放、危险秒拒、未知形态交 LLM 审批 |
| `Runtime → 索引族` | `AddPuddingRetrievalPort` → `RetrievalPortV1` | 当前查询连接符号与全文适配；分块/向量由评测探针消费，生产接线见 ADR-089 目标；维护由 CodeIndex 调度 |
| `Runtime → Host → Desktop` | `Browser Tools` → Host 路由 → Bridge / capability channel | 当前按活动会话选择传输；一期固定 Bridge 并统一 Desktop 执行入口，见 ADR-097 |
| `Desktop ↔ Core` | `CapabilityBroker` ⇄ `DesktopConnection` / `DesktopService` | 两端只经 Contracts + wire 协议；启用前过 preflight，否则保持旧 Bridge |
| `Runtime → Platform` | `ContentPart{type=image}` → `image_reader` | 视觉内容同事务写 ChatMessages 投影；文本模型只收 `artifact://` 占位 |
| `Platform → WebUI` | `AgentConversationProjectionService` → `TurnSurfaceStore` | canonical 事件 → 投影索引 → 虚拟化视口 → 交错内容流 |

## 5. 测试工程索引

| 测试工程 | 覆盖 |
|------|------|
| `Source/PuddingCoreTests/` · `Source/PuddingRuntimeTests/` · `Source/PuddingPlatformTests/` · `Source/PuddingMemoryEngineTests/` · `Source/PuddingWebApiTests/` | 工具契约、LLM 网关、MessageFabric · Agent Loop、上下文管线、语音/图片 · 渠道配置与 Artifact · 记忆库与 FTS5 · Web API |
| `Source/PuddingCodeIndexTests/` · `Source/PuddingIndexChunkingTests/` | 索引组件独立测试：变更管线/调度/维护/存储 + 分块（含边界断言） |
| `Source/PuddingContextPolicyTests/` | 上下文策略独立测试：容量算术、配置比例与预测、完整候选边界、严格及显式追加校验、真实摘要收益、冷却不能屏蔽硬保护，以及程序集依赖边界 |
| `Source/PuddingContextHandoffTests/` | 交接摘要独立测试：持久恢复、版本回读、作用域隔离、UTF-8 预算、失败保留与 BCL 依赖边界 |
| `Source/PuddingConversationReadModelTests/` | 会话读模型纯组件独立测试（**191 例**）：事件归约/重放幂等/跳号/终态迟到/父子 run 隔离、子代理摘要限长与完整性截断、历史/过程页条数与字节预算、三种游标合同、SSE 追赶预算、UTF-8 截断 + S1–S4 边界断言（含变异取红） |
| `Source/PuddingDiagnosticsTests/` | 可诊断基础设施独立测试：故障场景→期望因果码全量、事故复刻与证据、脱敏与证据预算、事故投影、S1–S4 边界断言（含探测器自检与取红） |
| `Source/PuddingDeepSeekWebTests/` | DeepSeek Search/Fetch 协议叶子组件独立测试：请求形状/参数校验矩阵/响应解析/HTTP 错误映射/有界重试与 `Retry-After`/传输异常与取消/端点归一 + S1–S4 边界断言（含探测器取红）+ 真实 API Live（Search→Fetch，无凭据时 Inconclusive） |
| `Source/PuddingBrowser.AutomationTests/` | 浏览器自动化可靠性组件独立测试：控制权/接管与世代、页面授权与子代理范围、回执与重试裁定、变更类后置条件、交互回执、引用身份与三个观察事实、operationId 账本、S1–S4 边界断言与用例守恒 |
| `Source/PuddingComputer.WindowsTests/` | 分析窗口视觉评分、检测几何与实际落点授权；[文件索引](Source/PuddingComputer.WindowsTests/code_map.md) |
| `TestScripts/ComputerUseSmoke/` | 可选交互式 WebView2 验收；[入口脚本](TestScripts/test-computer-use.ps1) 使用生产输入路径，DOM 只读核对焦点/值/按钮效果 |
| `Source/PuddingPathFilteringTests/` · `Source/PuddingVectorIndexTests/` · `Source/PuddingFullTextIndexTests/` · `Source/PuddingMemoryEngineBenchmarks/` · `Tests/PuddingBrowser.WebView2.Smoke/` | 路径忽略、向量与全文索引叶子组件 · BenchmarkDotNet 基准 · 浏览器 smoke |
| `Source/PuddingRetrievalEvalTests/` · `Source/PuddingRetrievalEvalProbe/` · `Source/PuddingFullTextIndex.Cli.Tests/` | 检索评测（`ISearchProbe` 端口）与全文索引 CLI |
| `Source/PuddingSshTests/` | SSH 组件独立测试（只引用组件，**289 例**）：known_hosts 明文/逗号/哈希/撤销与 pin 验证（**真实 OpenSSH fixture**）、POSIX 封装与请求校验、错误分类矩阵、有界采集白盒、**假传输故障矩阵（准入/排队/deadline/清理/停止）**、S1–S4 边界断言（变异取红两次均 3/5 红） |
| `Source/PuddingTokenizationTests/` | token 计数叶子组件独立测试（**18 例**）：结构断言（128000 vocab / 127741 merges / 1283 special / 3 级 Split / ByteLevel）、**官方向量逐 token 比对**（`@huggingface/tokenizers` 生成，10/10 一致）、回退四类原因、变异取红（`ByteLevel=false` 使中文与混合往返 2 例失败）+ S1–S4 边界断言 |
| `Source/PuddingHomeAssistantTests/` | HA 组件独立测试（只引用组件，**102 例**）：域策略三档与永久黑名单矩阵、状态投影/字节预算/`friendly_name` 恒带、`device_class` 与名称过滤、`after` 游标分页无重无漏、历史裁剪（合法 JSON/保序/丢弃计数）、REST 传输与错误码映射、引用闭包边界断言（变异取红 4 次） |
| `Source/PuddingSsh.Probe/`（+ `TestScripts/ssh-probe/`） | SSH 组件的**真实协议探针**（24 场景）：逐场景核对认证次数与客户端密钥指纹、取消远端证据、双流输出预算与驻留内存（见下「关键语义」） |
| `Source/PuddingCodeIntelligenceTests/` · `Source/PuddingCodexServiceTests/` · `Source/PuddingBrowser.WinUITests/` · `Source/PuddingDesktop.FoundationTests/` | 语言智能层 · Codex MCP Service · WinUI 浏览器表面 · Foundation 纯逻辑 |
| `Source/Pudding.ContractsTests/` · `Source/Pudding.Rpc.ProtocolTests/` · `Source/Pudding.DesktopSurface.BrowserTests/` | 契约与 wire 协议形状断言 · 浏览器表面 |
| `Source/Pudding.DesktopConnectionTests/` · `Source/Pudding.DesktopServiceTests/` · `Source/Pudding.CapabilityBrokerTests/` · `Source/Pudding.CapabilityBroker.AspNetCoreTests/` | 能力通道四个组件的独立测试（假服务端 / 假 UI 调度器 / 自托管 Kestrel） |
| `TestScripts/BrowserDeliveryReview/` | 浏览器任务授权、原租约复检、完整请求去重及子代理身份的行为回归；[README](TestScripts/BrowserDeliveryReview/README.md) 说明显式注入与迁入常规套件的方式 |
| `Tests/PuddingDesktop.Tests/` · `Tests/PuddingHost.Tests/` · `Tests/PuddingBrowser.AgentTools.Tests/` | Desktop 进程与配置、调试模式（路由/反向代理/SSE/WS）· Bridge Endpoint · 七项 Agent Tools |
| `Tests/PuddingDesktop.Diagnostics.Tests/` | 独立现场组件：退出分类、脱敏、文件容量、保留策略、写入失败与补录 |
| `Source/PuddingDesktop.GuardianTests/` · `Tests/PuddingDesktop.GuardianIntegrationTests/` · `TestScripts/test-pudding-guardian.ps1` | Guardian 独立事务/认证测试 · 真实 WinUI 控制端点 · 隔离产品的构建/故障注入/回滚/停止验收；[运行手册](Docs/13_runbooks/Desktop-Guardian.md) |
| `Tests/PuddingAgent.IntegrationTests/` · `Tests/PuddingNativeChat.IntegrationTests/` · `Tests/e2e/` · `TestScripts/` · `Tests/PuddingBrowser.TestSite/` | 集成测试 · 端到端脚本 · 生命周期/部署/smoke/性能探针 · 测试站点与门禁脚本（见下「关键语义」） |
| `TestScripts/verify-retrieval-delivery.ps1` · `TestScripts/verify-fulltext-incremental-latency.ps1` · `TestScripts/sample-host-io-shape.ps1` · `TestScripts/correlate-host-read-io.ps1` · `TestScripts/measure-retrieval-resources.ps1` | 检索交付验收三件套（2026-10-28）：**部署门禁**（进程新鲜度 + 程序集 token 双编码探 + 正负对照 + SUSPECT 判据 + 配置）；**G2 增量延迟**（PASS/FAIL/**INCONCLUSIVE** 三态，不把「没跑」写成「不达标」）；**宿主资源/IO 采样**（稳定/突发判形，采样循环不递归扫盘） |
| `TestScripts/watch-read-io-anomaly.ps1` · `TestScripts/verify-storage-vacuum-readiness.ps1` · `TestScripts/swap-compacted-platform-db.ps1` · `TestScripts/probe-read-io-shape.ps1` | 宿主读 IO 异常捕获与存储回收（2026-10-28）：**异常现场捕获**（超阈值当场写 MBps / ops / blockBytes / priv / user / privateDropMB / shape，退出码 3 表示抓到；`blockBytes≠4096` 追加 `BLOCK-NOT-4096`，表示那是另一个现象、不得混谈）；**VACUUM 可行性预检**（READY / BLOCKED / UNKNOWN 三态，只读，BLOCKED 属正常态）；**停机窗口换库**（VACUUM INTO → 设 INCREMENTAL → 恢复 WAL → 全量校验 → rename 备份 → 换入 → 复检；每步失败都明确"什么都没替换"，并打印回滚命令）；**读 IO 形态采样**（按 4096 字节/页判 PAGE-READ / BULK-READ / MIXED） |

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
| [LLM 请求交付可靠性](Docs/12_features/LLM请求交付可靠性架构与实施规划-2026-10-30.md) | Proposed：冻结报文、独立传输预算、统一恢复与上下文版本协调；组件门禁与端到端验收 |
| [交互面板 ADR-099](Docs/07_architecture/113ADR-099交互面板编辑状态与制成品归属ADR.md) | 提案：Agent 生成交互界面、SDK 双向事件、可恢复状态与 Core 资产归属；图片编辑为应用场景 |
| [浏览器一期 ADR](Docs/07_architecture/111ADR-097Agent浏览器控制一期闭环与单一执行链ADR.md) · [实施方案](Docs/12_features/Agent浏览器控制一期整体实施方案-2026-10-18.md) · [现有架构](Docs/07_architecture/浏览器工具与浏览器控制能力-架构与施工进度-2026-10-18.md) | 真实浏览器闭环、单一执行链与分层验收；[命令合同与配套部署](Docs/12_features/浏览器发现契约与配套部署修复方案-2026-10-18.md)，审阅证据见架构入口 |
| [`Docs/README.md`](Docs/README.md) · [`Agents.md`](Agents.md) · [`Agents-Hygiene.md`](Docs/10_conventions/Agents-Hygiene.md) | 文档总索引与阅读顺序 · 开发与提交纪律 · 仓库卫生 |
| [`组件化交付规程`](Docs/10_conventions/组件化交付规程.md) · [`调试入口`](Docs/08_how_debuge/README.md) · [`架构`](Docs/07_architecture/架构.md) · [`ADR 全表`](Docs/07_architecture/README.md) | 组件化交付 S1–S5 门禁（强制）· 调试与日志诊断 · 架构总览与模块分册 |
| [可诊断基础设施设计](Docs/12_features/可诊断基础设施设计-2026-10-07.md) | 诊断事实/类型化因果/事故查询面/故障场景资产：叶子组件边界、S1–S5 施工与门禁、查询与脱敏的强制项 |
| [会话首屏与渐进加载方案](Docs/12_features/会话首屏与渐进加载架构优化方案-2026-10-18.md) | 提案：有界读模型、统一打开、历史与过程分页、快照游标、增量同步与回填治理 |
| [工具插件化架构与实施方案](Docs/12_features/工具插件化架构与实施方案-2026-10-09.md) | 通用插件系统的工具贡献切片：不可变包、独立 SDK、Worker、Agent 挂载与卸载、诊断及 Web 管理；软件功能/策略/事件等贡献由公共 API 规划统一定义 |
| [核心组件与插件公共 API](Docs/12_features/核心组件与插件公共API规划-2026-11-04.md) | 基础设施与通用软件功能插件：工具、功能、策略/provider、事件/Job、渠道与受控界面贡献；组件声明扩展点、插件经公共服务组合行为；[两阶段目标](Docs/12_features/基础设施服务生命周期与最小原型两阶段目标-2026-11-07.md)定义服务/实例合同、生命周期、事件/订阅/Hook，先独立交付再组合最小原型；[任务书 1～8](Docs/12_features/Agent组件化任务书-总则与依赖-2026-11-05.md)规定基础设施/插件先于 Goal 的接入门禁；[S5 架构裁定](Docs/12_features/Agent组件化S5架构裁定与接线门禁-2026-11-06.md)约束事件、预算与身份；[基础设施评估](Docs/14_reports/2026-11-06-基础设施组件化评估与接入缺口.md)与[收敛计划](Docs/12_features/基础设施组件化收敛实施计划-2026-11-06.md)区分独立组件、真实适配和生命周期；[DS-I1-04](Docs/12_features/DS-I1-04-预算账户绑定与幂等创建任务书-2026-11-07.md)限定账户绑定施工范围；[A 阶段组件清单与合同卡](Docs/12_features/基础设施组件清单与合同卡-A阶段-2026-11-07.md)登记 11 个基础域的真实类型/四档等级/缺口、11 项现有领域边界与 A1–A6 门禁对照；[A 阶段第二批施工单](Docs/12_features/A阶段第二批施工任务书-2026-11-07.md)规定账户冲突、编译边界及 Work 到模型发送的施工范围 |
| [CORE-I1-05 决策记录](Docs/12_features/CORE-I1-05-单轮Work预算身份贯穿-决策记录-2026-11-07.md) · [任务书](Docs/12_features/CORE-I1-05-单轮Work预算身份贯穿任务书-2026-11-07.md) · [第二批任务书](Docs/12_features/A阶段第二批施工任务书-2026-11-07.md) · [CORE-I1-06 决策记录](Docs/12_features/CORE-I1-06-单次模型发送与租约结算-决策记录-2026-11-08.md) | 单轮 Work 预算身份：命令/受理行/受理标记与 frozen canonical 后端交叉校验，限额与模型报价来源冻结，Coordinator→buffered/streaming 载体与 fail-closed；受管策略默认关闭，激活待 I1-06 门禁 |
| [P0/P1 DeepSeek 施工任务书](Docs/12_features/Agent配置API-P0P1-DeepSeek施工任务书-2026-11-05.md) | 配置 API 冻结核心与三项独立施工单：只读 SDK 示例、分页扩展、wire 边界测试；文件白名单、固定签名与停止条件 |
| [ADR-098](Docs/07_architecture/112ADR-098Core事件驱动与严格分层基础设施ADR.md) · [事件施工](Docs/12_features/Core事件驱动基础设施施工与代码设计-2026-10-18.md) · [Core 审阅](Docs/14_reports/2026-10-18-Core严格分层与Hook架构审阅.md) | 提案：内部事件持久受理、订阅路由、独立投递、工作交接与 Hook 分层；E0–E8 施工 |
| [全生命周期注册点目录](Docs/12_features/Agent全生命周期Hook事件与动作注册点目录-2026-10-09.md) · [管道架构](Docs/12_features/Agent生命周期与Hook管道架构设计-2026-10-09.md) · [代码设计](Docs/12_features/Agent生命周期与Hook管道代码设计-2026-10-09.md) · [现状审计](Docs/14_reports/2026-10-09-Agent生命周期与Hook边界审计.md) | 提案：动作/Hook 注册点与管道、生命周期收口（见下「关键语义」） |
| [浏览器自动化可靠性与渐进阅读设计](Docs/12_features/浏览器自动化可靠性与渐进阅读设计方案-2026-10-08.md) | 浏览器控制权与页面授权、动作回执、稳定 ref、渐进 DOM/markdown 阅读、截图视觉和受约束委派的目标设计与交付门禁（提案） |
| [Computer Use 代码设计](Docs/12_features/Computer-Use代码设计与分阶段施工方案-2026-10-20.md) | 提案：Windows 控件与键鼠工具、独立执行器、控制权和观察契约、分阶段接入 |
| [搜索框输入诊断方案](Docs/12_features/Computer-Use搜索框输入诊断与实现方案-2026-11-03.md) | 焦点/合成输入交叉对照、菜单粘贴、无障碍写值及 IME/HID 边界；先独立探针后接工具 |
| [B1 身份准入与下一阶段施工](Docs/12_features/B1身份准入接线与浏览器交付下一阶段实施方案-2026-10-09.md) | Core 可信身份跨进程传递、任务授权签发、Desktop 准入/复检、B6 接线及外部验收顺序 |
| [浏览器交付修订施工](Docs/12_features/浏览器交付修订施工方案-2026-10-12.md) · [DeepSeek 施工指令](Docs/15_tasks/2026-10-13-DeepSeek浏览器修订施工指令.md) | 两条传输的共用执行器、不可变许可、任务选择绑定、完整指纹账本、Runtime 委派和原生截图输出的逐文件规格；R1–R6 顺序、反例与交付验收要求 |
| [Agent 生成式交互界面与浏览器应用运行时](Docs/12_features/Agent生成式交互界面与浏览器应用运行时设计方案-2026-10-08.md) | HTML/表单/SVG/图表/涂鸦作品、页面 SDK、持久状态、实时版本更新、具名 JS 调用与用户提交闭环的目标设计（提案） |
| [ADR-095](Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) · [上下文维护施工方案](Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) | Proposed：Session软维护与逐请求硬保护、唯一候选提交/checkpoint、请求实测与估算分离；独立策略组件先行，尚未接入 |
| [`ADR-089`](Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) · [统一检索设计](Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md) · [施工门禁](Docs/12_features/增强索引分阶段施工计划-2026-10-18.md) | Proposed：覆盖与否定、生产语义召回、多语言能力、grep/地图及组件边界；按 R0–R6 依赖门禁实施 |
| [`ADR-077`](Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md) · [视觉设计](Docs/12_features/原生视觉与统一取图截图优化设计-2026-09-12.md) | 原生视觉、typed parts 与截图链路 |
| [`ADR-076`](Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md) · [存储设计](Docs/12_features/遥测调试数据自动过期与Web存储管理设计方案.md) | 存储治理、语义目录、保留策略 |
| [`ADR-079`](Docs/07_architecture/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md) · [内容流设计](Docs/12_features/Agent消息交错内容流与最新行为组披露完整实施方案.md) | Agent 回合单一有序内容流与披露 owner |
| [`ADR-074`](Docs/07_architecture/89ADR-074Goal持久目标自主续行与自动压缩ADR.md) · [Goal 设计](Docs/12_features/Goal持久目标自主续行与自动压缩完整设计方案.md) | 持久 GoalRun、证据验证、Task-bound Goal |
| [`自动派发`](Docs/12_features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md) · [`夜间调度`](Docs/12_features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md) · [`ADR-073`](Docs/07_architecture/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md) | 自动派发 · Execution 生命周期闭环 · 产品施工总表 |
| [Token 效率](Docs/12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md) · [Harness 兼容](Docs/12_features/AgentHarness兼容与工具调用效率修复设计方案.md) · [`ADR-081`](Docs/07_architecture/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md) | 分级压缩与缓存命中验收 · Harness 兼容边界与工具协议适配 |
| [`tool-infrastructure-layering.md`](Docs/07_architecture/tool-infrastructure-layering.md) · [自动工具审批](Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md) | Tool 分层、强制委派合同 · 自动工具审批唯一设计入口 |
| [Agent 权限请求与单次、短时授权](Docs/12_features/Agent权限请求与单次短时授权设计方案-2026-10-20.md) | 提案：卡片/弹窗与指令共用请求状态机；精确许可、原子消费、等待恢复与组件交付门禁 |
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
- `cmd /c Tools\Dev\build-all-tests.cmd` — **全仓测试工程编译门禁**（只 build 不跑；只把 `error CS` 算失败，MSB3021 锁/路径笔误不算），结果写 `temp\alltests.txt`；覆盖 **`Source\*Tests` 与 `Tests\*Tests` 两处**（后者 2026-10-24 才补上，此前 6 个工程从未被构建）；给接口加成员后必跑（实现方的测试替身不会自动提醒）。
- `python dev-up.py --frontend-only / --restart / --rebuild / --status / --down` — 源码开发态前后端与工具进程。
