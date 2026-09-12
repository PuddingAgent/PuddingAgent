# PuddingAgent CodeMAP

## 2026-09-12 MSBuild 非法历史输出路径修复

`Docs/Reports/MSBuild非法输出路径缓存修复-2026-09-12.md`：PuddingCodeIntelligence/PuddingFullTextIndex 的 `obj/Debug/net10.0/*.csproj.FileListAbsolute.txt` 残留150条含引号的历史输出路径，触发Visual Studio MSB3541。已备份并定点移除，两个项目经VS MSBuild实际构建通过；无需改业务代码或SDK targets。复发诊断与OutDir正确传参见 `How-Debuge.md`。

## 2026-09-12 原生视觉与截图优化入口

`Docs/Features/原生视觉与统一取图截图优化设计-2026-09-12.md`、ADR-088、`Docs/Reports/原生视觉优化看板修订-2026-09-12.md`：当前主力模型文件配置缺 `vision`；Reader 内 Responses/helper 分叉；Resolver 的 Data URI→Planner 解码上传；旧 384 图片估计；WebView2/RemoteBrowserPage ScreenshotAsync Unsupported。按 V5–V10 收敛能力/预算、Reader、Provider 流式传输，补齐 Web/Desktop 采集与共享主子代理图片轨迹。ADR-077 V0–V3 已有实现，本次仅新增设计/看板；V4 真实新构建验收待完成。

## 2026-09-12 子代理弹性与双向交互修订

`Docs/Features/子代理弹性预算与双向交互设计-2026-09-12.md`、ADR-087、`Docs/Reports/子代理弹性交互看板修订-2026-09-12.md`：600改为可选实例，任意合法正整数预算；系统管理生命周期；query_sub_agents共享快照、send_message主子双向/插嘴、ask_question持久等待120秒与StopRun；Web轨迹空白先修canonical事件/回放，检查器改善发现/状态/行为/操作。复用Message Fabric/AwaitHandle/Steering/Cancellation/SubAgent投影，不建平行生命周期系统；本轮仅设计/看板。


## 2026-09-12 下一阶段设计：缓存99、Memory长程自治与600轮纠偏

权威方案：`Docs/Features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md`；ADR-084/085/086；交付包与13张看板映射：`Docs/Reports/PuddingAgent-Next-Phase-2026-09-12/README.md`。重点入口为SubAgentManager/TaskExecutionPlanCompiler预算二次截断、ContextPipeline/AgentMemorySummaryContextBuilder首轮装配、SubconsciousRecallPipeline检索与后台miss、C01/C02最终请求、ToolInvocationService与legacy执行分支。预算纠偏（N00，commit f096bc5）已实施：子代理/WorkUnit 支持任意合法正整数轮次（8/32/600/1200/10000…），未填由系统 profile 决定，600 仅为 profile 默认示例；已删除 32/40/120 下压与强抬 600 双向补丁。缓存99/Memory/30日等其余新目标仍待实施/验收。


## 2026-09-12 抖音与 WebView2 续建入口

`Docs/Reports/DouyinCreatorTools-WebView2复用与看板方案-2026-09-12.md`：外部仓库 e35dbe2 源码调研及 DY-00/01/02 验收、只读适配、可靠回复拆分。现有实现入口为 `Source/PuddingBrowser.AgentTools`、`Source/PuddingHost/BrowserBridge/RemoteBrowserPage.cs`、`Source/PuddingDesktop/Browser/BrowserBridgeCommandDispatcher.cs`、`Source/PuddingBrowser.WebView2/WebView2DomClient.cs`。七工具已存在，Evaluate/CDP 等仍 Unsupported；本轮仅文档/看板更新，Douyin 业务尚待实施。

> 顶层快速索引 | 2026-09-12 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 2026-09-12 GLM 进度复核与看板同步

后续运行审计入口：`Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md`及同目录`02-任务看板登记与实施顺序.md`。8心跳/12子Run/898请求；gateway与归因投影分开、token加权命中95.48%。主要源码定位：`MessageDeliveryDispatcher` recovery→`AgentInvocationDispatchFactory`的msg会话回退；`AgentExecutionService.Buffered`结构化文本工具路径与native路径审计不一致（F01 41工具仅2归档）；`TerminalProcessManager`输出/退出并发；`SubconsciousJobQueue`两表重复schedule_skip。方案为父级身份+canonical接续、HeartbeatOutcome/WorkUnit、统一工具审计、错误家族熔断与自修复/外部部署证据闭环。窗口外`c89920f`已提交C01-A、`b0cfa3a`已接Authorization入口；以下旧“WIP”是前轮时点，当前转为待独立验收与明确新构建验证。

当前入口：`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/06-实施进度复核与看板状态修订-2026-09-12.md`。`19ec137/9537f60/33c1489/15842ff` 已提交；原10探针转绿，前端28、Platform48、Runtime40定向通过。F01 hook恢复/失败/重试无真实消费者，S01-B invocation/attempt/provenance持久化和本地故障恢复待补；C01-A有未提交WIP待验收，C01-B/C02仍待完成。已修订7张描述、5张状态为NeedsReview；S01-A-R/T01-R源码accepted，不等于Completed或产品验收。C02保持Backlog，总卡InProgress；回执在同包`audit-evidence/2026-09-12/`。

## 2026-09-11 GLM 批次1独立审计

历史结论见 `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/04-批次1独立审计与下一步.md`；当时任务 ID/回执见同目录 `05-后续任务与看板回执.md`。9月11日既有91项通过、TypeScript/入口构建通过，新增10个边界探针失败；这些探针已在9月12日转绿，最新剩余工作以06为准。A01删除与编译层面通过，未做本批生产验收。

## 2026-09-11 GLM 优化批次1实现入口（原交付记录）

`Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/` 的 15 个工作包中，批次1已有以下实现；完成状态以以上独立审计为准：

- **F01 明细水合**：新增 `Source/PuddingPlatformAdmin/src/pages/chat/runtime/detailHydrationScheduler.ts`（页面级 capacity=2 总并发、稳定请求 key、generation/owner token、AbortController、僵尸占位、401/404/transient 分类与有界退避）；`useTurnSurfaceStore` 改为订阅衔接，`registerVisibleTurn` 配对 `unregisterVisibleTurn`（引用计数），MessageRow→MessageList→ChatMain→ChatLayout→index 全链路透传 `onTurnInvisible`；MessageRow 视口观察不再首次相交即 disconnect。测试：`detailHydrationScheduler.test.ts` 7/7、`turnSurfaceStore.hydration.test.ts` 7/7（含 8 可见×20 重渲染并发≤2、A→B→A、迟到回调、离视口剪枝、401 停止、unmount）、`MessageRow.focus.test.tsx` 9/9。
- **S01-A usage 并发**：`TokenUsageRecorder.RecordCoreAsync` 改为 BEGIN IMMEDIATE 单写事务（明细幂等读 + 月度聚合读改写同事务，decimal 语义不变；同 source 异 payload 抛冲突，best-effort 路径吞掉），新增 `Services/UsageWriteConflict.cs` 唯一索引兜底；`LlmGatewayUsageRecorder` SaveChanges 捕获唯一冲突按幂等成功。顺手收尾了 dirty 树遗留的 `occurredAtUtc` 半成品重构（该文件此前无法编译）。测试：`TokenUsageRecorderConcurrencyTests` 5/5（50 并发对齐、重复 source 计一次、冲突拒绝、best-effort 跳过），usage 相关回归 39/39。
- **T01 记忆工具**：`SaveMemoryTool` important 分支身份改由 `ToolExecutionContext.AgentInstanceId` 派生（原 root 读取的 agent_instance_id 不可达），upsert 增加 preference 必 key / fact 必 content 前置校验（空 content 由旧“警告后照写”改为 fail-closed 零写入，`MemoryToolsTests` 对应用例同步更新）。测试：`SaveMemoryToolContractTests` + MemoryToolsTests 共 27/27。
- **A01 组合根**：删除 `Source/PuddingAgent/Services/` 两个旧服务注册副本（msbuild Compile 由 3 项减至 Program.cs 1 项，入口构建 0 错误）；`Docs/架构.md` 开头标注 Desktop + Core 当前形态、单进程 P2P 叙述移为历史背景；PuddingHost.Tests 组合守卫通过，Desktop 不引用 Host。

以上为首批历史记录。批次2+最新状态以同目录06为准：S01-B首片已提交、C01-A WIP待验收，其余未验收工作仍沿01/02设计推进。

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `Docs/Reports/GLM前端首批交互审计与下一步-2026-09-11.md` | GLM 首批 Chat 交互独立审计；草稿、重复 Steering、排队目标、图片门禁、受理恢复、停止与队列回执共 7 个契约反例；needs_changes，附看板所有权与产品验收门禁 |
| `Docs/Reports/前端交互体验优化建议-2026-09-11.md` | 当前前端交互评估；发送/排队/停止一致性、可信反馈、阅读与草稿连续性、导航/交付物衔接和视觉规则；第一批（鼠标排队/补充当前任务/独立停止+服务端取消接线/失败保留草稿/操作回执/键盘可达）已于 2026-09-11 实施，第二批及以后仍为 Proposed |
| `Docs/Reports/PuddingAgent-GLM-Optimization-2026-09-11/01-代码审阅与优化设计.md` | 原始15包设计和hash基线；同目录06为最新进度复核与看板状态，03–05保留各阶段历史；先补F01/S01-B，验收C01-A后推进C01-B/C02 |
| `README.md` / `README_zh-CN.md` | 中英文产品与目标架构入口；Windows Desktop/Core 产品边界、Plugin/Function/Hook/Event/Projection 五类合同、Agent FSM、函数图编排、前端思想、现状缺口与路线 |
| `Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md` | 工作区 TODO 台账、Agent 认领/拒绝/回报、durable 自动派发与定时消息、可信 idle、心跳 0、峰谷 WorkAdmissionFence，以及 Hook 触发的临时质询子代理、GoalRun 有界循环、manifest/Admin 模型路由、防无限循环熔断和公共 Plugin/Function/Event/Projection 映射 |
| `Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md` | `/goal` 完整专项设计；统一 Web/Desktop/Connector 命令、持久 GoalRun、事件驱动 continuation、256 个外层 Goal Iteration、证据 Verifier、用户抢占、重启停用、自动压缩和 Task-bound Goal；明确不依赖 Heartbeat |
| `Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md` | 低峰自动执行施工图；2026-08-29 已由 Desktop 构建/加载新程序集并完成真实自动派发 smoke：同步后代 Token 预算传播、Task-bound Failed 释放和 stale Message target 淘汰已验证；warm cache 97.31%（DeepSeek 98.78%）仍低于 >99%，事件驱动 intent、Goal 成本/后代工具归因、AwaitHandle/checkpoint、动态模型反馈与 7 夜验收仍未完成 |
| `Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md` | 2026-08-31 夜间调度复盘的代码级收口方案；把有效调度冻结为 Intent→Decision/Outcome→Assignment/Reservation→TaskGoalBinding/GoalRun→ExecutionRun→Verifier/Task terminal，并细化 legacy false-busy 修复、事件 Intent 可靠结算、staged mode、scan-run 持久化、Blocked 恢复 UI、Runtime 预算与无人工 smoke 的实施顺序和验收门禁 |
| `Docs/deepseek-reference-architecture-master-plan-2026-08-14.md` | 本次会话的 deepseek-harness/pi 参考架构总蓝图；以“一切业务能力皆插件”为第一原则，覆盖 Model/Tool/Skill/Session/Agent Loop/Sandbox/Storage/Schedule/UI、统一运行事实、文件级改造矩阵、任务图与 T00-T16 施工步骤 |
| `Docs/07架构/67ADR-066*.md` | Browser 能力与 Douyin 分层决策 |
| `Docs/07架构/68*.md` | WebView2 自动化分阶段实施规格 |
| `Docs/07架构/69*.md` | Desktop 浏览器工作区/运行中心/存储 |
| `Docs/07架构/70–73*.md` | Phase 2A-1 Bridge 与双标签工作区（✅） |
| `Docs/07架构/74*.md` | Phase 2A-2 Remote Browser + Agent Tools（✅） |
| `Docs/07架构/75–76*.md` | Phase 2A-3 Snapshot/Locator/Interact/Wait（✅） |
| `Docs/07架构/77–79*.md` | Phase 2A-3B/C DeepSeek 验收与闭环 |
| `Docs/07架构/80ADR-069*.md` | MOA 子代理设计委员会编排核心；Phase 1–3 计划编译、纯状态机与运行时适配 |
| `Docs/07架构/81ADR-070*.md` | 通用 Agent 编排图；V2 组件/多模态端口、SQLite 事实、Graph/Run 发现、Revision/Layout 双 CAS、replay-to-live SSE，以及 React Flow 节点/端口/Edge/Graph Input 编辑器 |
| `Docs/07架构/82ADR-071*.md` | 通用 Agent 编排平台完整目标设计；JSON 图、Revision/Layout/Deployment/Run 事实边界、Agent/Tool/Graph 统一 Function、不可变图生成流程、有界循环、多模态、Agent 工具与 MOA 统一 |
| `Docs/07架构/83*.md` | 后端执行内核与 Control Plane 施工图；契约、SQLite、API、状态转换、Function Runtime/Invoker、Typed Hook Pipeline、Parent/Child Run、Outbox、Scheduler、Trigger 与权限 |
| `Docs/07架构/84*.md` | Admin 蓝图编辑器和组件系统施工图；Node/Edge/Input/Trigger、Revision/Deployment/Run、多模态 UX、Pudding 视觉语言、原因优先状态、Function Catalog、插件 Presentation 与系统构成检查器 |
| `Docs/07架构/85*.md` | 分期交付、测试、安全、性能、Desktop 部署、浏览器 smoke、恢复与验收证据图册 |
| `Docs/07架构/86ADR-072*.md` | 工作区 TODO 第一阶段任务领域 ADR；覆盖五列 Board、Task Failed/Reopen、Task Ledger、手工/Auto 派发、受限 Cron/Message Event、Agent Availability、Task executionWindow 与 provider/model 价格时段 Resolver；完整 Auto 受 Goal 前置约束，不新增 `work-policy.json` |
| `Docs/07架构/87ADR-073*.md` | 当前产品施工总表与冲突裁决基线；列出 30 项产品任务、17 项 T00–T16 平台底座任务及专项 Phase 去重映射，覆盖目标、优先级、工作量、难度、依赖、设计位置和里程碑 |
| `Docs/07架构/89ADR-074*.md` | Goal 专项架构决策；冻结外层 GoalRun/内层 Agent Loop 双层预算、256 accepted Iteration、durable outbox、证据验证、Task-bound Goal、Availability 与低峰派发；2026-08-26 G2/G3 和 Task-bound authoritative 源码链已落但默认关闭，真实低峰/完整 Verifier/Admin/进程外门禁未通过，ADR 仍为 Proposed |
| `Docs/07架构/90ADR-075*.md` / `Docs/Features/第三方任务看板AccessToken与外部API详细设计方案.md` | 第三方任务看板开发合同；冻结 hashed opaque Access Token、ASP.NET Core 独立 scheme + scope/workspace Policy、外部 API v1、ETag/幂等、追加式 TaskEvaluation 与 Admin Access Token 管理器；P1（Token 后端）+ P3（Admin UI）+ P2 基本功能已实现：`pdt_v1_` opaque Token 摘要存储、PuddingExternalAccessToken scheme、Admin 管理 API/UI、last-used 合并写、External Task API v1（list/get/create/patch/comments/evaluations/commands + ETag/428/412 + 简化幂等）共 65 项后端测试；SSE Watch/RateLimiter/OpenAPI 与 P4（部署收口）未实现，External API 默认关闭 |
| `Docs/07架构/96ADR-082*.md` / `Docs/Features/Pudding外部工作空间Agent消息API设计与使用说明.md` | External API v1 的 Workspace/Agent/消息扩展；新增 `workspaces.read`、`agents.read`、`messages.send`，安全目录投影、`canonical_turn` Message Fabric ingress、幂等 `202 + Location` 和 Token-owned execution receipt；明确 Delivery accepted 不等于 Agent terminal。External/Token 7/7 + Dispatcher 2/2 聚焦测试与 Desktop Loopback 真实模型 smoke 已通过；非 Loopback HTTPS、RateLimiter/OpenAPI/P4 运维收口待完成 |
| `Docs/07架构/97ADR-083*.md` / `Docs/Features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md` | Agent 系统预制模板 v2 目标设计；目录包、完整 Creation Snapshot、版本/内容哈希/许可来源、显式导入升级与 drift 保护，Workspace 创建时选择模板即原子填充全部六组配置；重写通用助手并新增原创文本的 `deepseek-whalechan` 社区角色预制，既有 Agent 不被模板更新反向覆盖。当前仅设计完成，未实施或产品验收 |
| `Docs/07架构/91ADR-076*.md` / `Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md` | 遥测/Debug 存储治理设计 + 首轮实现（Phase 0–3 已落地：语义目录/快照估算/单 writer 协调器/语义 API/Web /storage 页面；Phase 4 生产验收待做）；上下文日聚合复用既有 `context_layer_daily_rollups`、retention 索引收编目录所有权、旧 /databases 端点与 Desktop 旧页面捆绑退役、appsettings Retention 节已迁移 system.json |
| `Docs/07架构/92ADR-077*.md` | 原生视觉基础：typed parts、Workspace Artifact、Files API、多轮恢复；V0–V3 已有实现，V4 真实新构建验收待做。后续 ADR-088 收敛 Reader/能力/图片预算/流式传输并补齐 Web/Desktop 截图，自动 helper 移除及显式通用子代理第二意见仍待实施 |
| `Docs/Features/Chat图片消息回放与前端旧Bundle缓存修复方案.md` | 2026-08-26 Chat 图片占位事故的可施工修复方案；冻结 Agent-first `contentParts` 透传、typed parts 优先兼容、localhost 旧 Service Worker 清理、入口/哈希资源缓存合同、build identity 和两段式产品验收；关联 P1 Task `ceba781342aa4353901654d1897092cb`，尚未实施 |
| `Docs/Features/子代理活动轨迹实时回放与运行检查器修复方案.md` | 2026-08-26 子代理检查器空时间线事故的证据化施工方案；活动 Run 继续零 archive 轮询，改由 Conversation SSE + active-subagent gap replay + 可对账状态水位恢复；修正有界工具详情导致的聚合少计、增加轨迹同步降级与 build identity 门禁；关联 P1 Task `791d062fa6ea44f18bfe5027a37696d0`，尚未实施 |
| `Docs/07架构/tool-infrastructure-layering.md` | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | 对照 deepseek-harness 的消息、推理和工具调用 UI 目标架构；定义 TurnStatus、Reasoning/Tool/Delegation 行、toolCallId 投影、分期与验收矩阵 |
| `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md` | 聊天前端「行为链 + 质感」升级：harness 质感纪律与 Hermes/业界 12 原则调研、四档灰阶 token、交错时间线（路径 A/B 统一 ViewModel）、五类 presentation renderer 设计与实施记录 |
| `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md` | Flash 代码级施工合同：canonical sequence、TextBlock ⇄ ActivityGroup、会话级唯一最新披露 owner、完整 reasoning、工具详情懒加载、柔和收起/卸载、逐文件任务卡、测试命令和双阶段验收 |
| `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md` | 冻结 AgentTurnCard 单一有序内容流与唯一正文源；当前最新 Agent 回合的最后行为组持续展开，最终正文不关闭，新行为/新回合转移 owner 并柔和收起旧组 |
| `Docs/07架构/94ADR-080任务看板分层读取子任务与命令化拖拽ADR.md` / `Docs/Features/任务看板状态机子任务渐进披露与高性能拖拽优化设计方案.md` | 任务看板下一阶段 Proposed 设计：Ready 证据化直达 Completed、单层独立状态子任务、普通 List/工具仅 id+title、Index/Card/Detail 三层投影、评论/备注分型、命令化拖拽、global-cursor Watch 修复与 10k 任务性能门禁；尚未实现或验收 |
| `Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md` / `Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md` | 模型后训练 Harness 适配；canonical 工具保持唯一，`rg/exec_command/write_stdin/apply_patch/pwsh` 在统一门禁前归一化，WSL 作为显式 Unix 通道，搜索 no-match 与真实失败分离，完整普通文本五段报告同轮收口；`BuiltInAgentTemplates` 单一权威，Low 投影保留读取/搜索/`search_tools`；动态定义在下一 LLM round 单调生效，连续 8 次 discovery-only 触发 `tool_discovery_stalled`；Token 归因使用 RuntimeExecutionIdentity；聚合报表和部署 smoke 待完成 |
| `Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md` / `Docs/superpowers/specs/2026-06-06-runtime-steering-queue-design.md` | current-Turn Steering + 无人值守队列合同：普通消息立即进 canonical Turn；队列只含未认领 delivery/Turn，认领后由消息卡与轨迹接管；Agent/heartbeat delivery 受理为 canonical Turn；独立 `⚡` 复用 Steering admission。源码已实施，产品进程重启/smoke 待做 |
| `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md` | 对照 deepseek-harness 的工具定义与执行协议；规划 canonical output、端到端 callId、结构化错误、管线、并发、spill、可回放 presentation 与 DeepSeek Code Mode |
| `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` | 对照 deepseek-harness 与 pi 的统一目标架构与 2026-08-15 复评；定义 Plugin/Function/Hook/Event/Projection、Agent Transition+Effect FSM、Function Graph、Composition Snapshot、前端解释层、底座缺口与分期路线 |
| `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md` | 7 日 Token 构成、工具结果重放、搜索失败和 ZIP 稀疏度基线；2026-08-28 登记 95.92% 事故基线、Harness 对齐的 warm-prefix checkpoint、prefix-v2 历史锚点、LLM purpose 分桶、审批控制面降耗，以及 DeepSeek 连续 7 日严格 >99% 验收 |
| `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md` | 聊天页主代理余额徽标 + 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器（DeepSeek `/user/balance` 首个落地，`/v1` 剥离修复）+ 前端 `providerBilling.ts` 展示适配器；新服务商扩展步骤、5min 低频轮询/手动刷新、apiKey 不进日志约束 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `Directory.Build.props` | 全项目通用 .NET 构建属性；SDK 默认项统一排除项目内 `temp/**`、`tmp/**`，避免测试/构建输出递归自复制并触发 Windows 超长路径评估失败 |
| `dev-up.py` | 本地开发监督器；Codex MCP 子进程必须通过 5100 TCP readiness 后才启动 Backend，避免 MCP workspace reconciliation 启动竞态 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs · Console/DesktopChild 薄壳) | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线；压缩与冷水合共享 canonical ChatMessages 增量同步门禁 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · 回环鉴权控制面（Core/前端制品加载、重启、诊断）· Core 点火事务部署/程序集哈希验收 · Browser 工作区 · 调试模式与运行中心 · 客户端精灵源素材 | [code_map](Source/PuddingDesktop/code_map.md) |
| `Source/PuddingHost/` | 🔑 组合根 · 全网卡 HTTP/本机控制地址 · Browser Bridge · 飞书连接器 | [code_map](Source/PuddingHost/code_map.md) |
| `Source/PuddingCore/` | 🔑 抽象与契约 · 接口 · 模型 | [code_map](Source/PuddingCore/code_map.md) |
| `Source/PuddingPlatform/` | 🔑 Session · API（含认证/当前用户投影）· EF Core · 消息网关 | [code_map](Source/PuddingPlatform/code_map.md) |
| `Source/PuddingMemoryEngine/` | 🔑 Library/Book/Chapter · FTS5 · 潜意识 | [code_map](Source/PuddingMemoryEngine/code_map.md) |
| `Source/PuddingGateway/` | LLM 网关适配 | [code_map](Source/PuddingGateway/code_map.md) |
| `Source/PuddingController/` | 代理控制层 | [code_map](Source/PuddingController/code_map.md) |
| `Source/PuddingCodexService/` | Codex MCP Sidecar | [code_map](Source/PuddingCodexService/code_map.md) |
| `Source/PuddingBrowser.AgentTools/` | 七项 Browser Agent Tools | [code_map](Source/PuddingBrowser.AgentTools/code_map.md) |
| `Source/PuddingBrowser.Abstractions/` | Browser 契约 | [code_map](Source/PuddingBrowser.Abstractions/code_map.md) |
| `Source/PuddingBrowser.WebView2/` | WebView2 Driver | [code_map](Source/PuddingBrowser.WebView2/code_map.md) |
| `Source/PuddingBrowser.Protocol/` | Bridge 线协议（8 .cs） | [code_map](Source/PuddingBrowser.Protocol/code_map.md) |
| `Source/PuddingCodeIntelligence/` | 代码索引/分析 | [code_map](Source/PuddingCodeIntelligence/code_map.md) |
| `Source/PuddingCodeIndexer.Cli/` | 代码索引 CLI | [code_map](Source/PuddingCodeIndexer.Cli/code_map.md) |
| `Source/PuddingFullTextIndex/` | 全文索引引擎 | [code_map](Source/PuddingFullTextIndex/code_map.md) |
| `Source/PuddingGit.Tools/` | Git 20 工具（实现在 Runtime） | [code_map](Source/PuddingGit.Tools/code_map.md) |
| `Source/PuddingPlatformAdmin/` | React 管理前端 · Chat 虚拟视口/渐进消息/状态缓存 · Agent 编排布局编辑器 · 管理壳异步隔离 · 主代理服务商余额徽标（DeepSeek 首个，多服务商计费展示适配器） · 已移除 Phaser/2D Studio · 生产 dist 经 PuddingHostContent.props 部署到 Core `wwwroot/admin`（dev 输出分流 dist-dev，防 MSBuild 增量清理破坏部署，见 How-Debuge §6.12） | [code_map](Source/PuddingPlatformAdmin/code_map.md) |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → dispatch 冻结授权 catalog/schema；search_tools 结果只在下一 LLM round 单调增加 visible definitions
  → 连续 8 次 discovery-only（查询换词也同族）→ tool_discovery_stalled，禁止高缓存命中零 Goodput 空转
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)

Agent Loop → LlmInvocationService → DirectLlmClient
  → model.protocol=openai → OpenAiLlmGateway (/chat/completions)
  → model.protocol=responses → ResponsesLlmGateway (/responses；DeepSeek reasoning_text + incomplete/length 终态兼容)
  → model.protocol=anthropic → AnthropicMessagesLlmGateway (/messages)
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议
  → Provider usage → ILlmGatewayUsageRecorder → llm_gateway_usage_events
    → StatsApiController（月度/趋势本地计费口径）
    → TokenUsageDailyAggregateService / ContextLayerDailyRollupService
      （闭日 UTC 聚合缓存 llm_usage_daily_aggregates / context_layer_daily_rollups +
        stats_daily_cache_days 完成标记；当天实时计算，Rebuild 后按月失效）
    → TokenUsageEvents 继续只承担会话/角色/上下文归因

TaskAutoDispatchWorker（5min bounded authoritative；MaxStartsPerScan=2）
  → AgentAvailabilityProjectionStore（每轮先刷新全部 Agent；即使候选为 0 也输出 idle/busy/unknown）
  → BacklogRefinementEvaluator/Store（显式 autoDispatchEnabled；结构化准入后 CAS Backlog→Ready）
  → TaskAutoDispatchEvaluator（Ready/Deferred；结构化类型/能力/模型路由 + Availability/Window）
  → ProviderModelExecutionWindowResolver（llm.providers.json 版本化价格窗口；未知 fail closed）
  → TaskExecutionPlanCompiler（结构化 Task → 版本化 WorkUnit DAG + budget/scope/dependency SHA-256）
  → TaskExecutionTracker（active Binding；Task→Plan/WorkUnit→Assignment→Reservation→Goal→Iteration→Execution→Outbox 五态跟踪）
  → GoalContinuationWorker / ConversationAcceptanceStore（canonical plan/node/fingerprint + reservation 二次围栏；首个 WorkUnit 原子 Running）
  → ExecutionCommandReader / ExecutionRunCoordinator（执行前重读 Command→Goal→Binding→Plan/Node；Agent 与 WorkUnit rounds/tools/duration 取更严格值）
  → 每 Agent 每轮最多一个、全局最多两个；任何 promotion/start/repair 都必须经唯一 CAS/fencing 写入者

Admin ChatMain 余额徽标 → useProviderBalance (5min 轮询/手动刷新)
  → GET /api/llm/providers/{id}/balance → LlmProviderApiController.GetBalance
    → LlmProviderFileService.GetBalanceAsync（解析 apiKey：ApiKey/${ENV}/{{vault}}/ApiKeyRef）
      → ILlmBalanceProvider 注册表 CanHandle 分发（DeepSeek: {baseUrl 去 /v1}/user/balance）
      → 未注册适配器 → IsAvailable=false「暂不支持」DTO（前端隐藏/显示 —）

ContextPipeline → Tool layer mandatory delegation policy
  → 首次工具调用前必须判定 Direct / Delegated
  → 复杂任务前三次工具调用内必须进入匹配 smart_* 或 spawn_sub_agent
  → SmartWorkflowToolBase 将历史 question/what/query 仅在执行边界归一为 task
  → smart_explore 统一替代已退役的 smart_search / smart_query_session_log

Terminal 长命令能耗协议（2026-08-22）
  → terminal_wait 阻塞语义：等到任务退出或输出超过预览上限才返回，wait_seconds 0-600 默认 60
  → 工具描述/NextAction/ToolLoopInstruction/Smart 提示词统一引导"一次阻塞等待"，禁止 1-2 秒式轮询
  → 动机：旧"出现新输出即返回"语义在全库产生 6,040 个纯轮询轮 ≈ 8.26 亿 tokens（16.3%）

上下文注入冗余治理（2026-08-22，指令层曾占每次调用 67%）
  → 工具描述单语化：41 文件去除英文复述（-13K 字符）；使用教学入 skill 文档，schema 只留必要说明
  → search_tools 装载收紧：默认 3/上限 8（原 8/20），阻止长会话工具集棘轮到 50+（主会话曾 34.6K schema tokens/轮）
  → L1-TOOLS 索引补延迟工具名清单（仅 id 无 schema），Agent 不再盲搜 search_tools
  → L2-SKILLS 索引行压缩：skillId + 首句摘要(≤100字) + tags≤4/keywords≤6，去掉 Name/版本/path；
    主 Agent 57 技能的索引从 29K 字符/轮显著缩减，全文仍由 agent_skill 渐进加载
  → 前缀稳定性结论：分层排序已正确（稳→动）；L9-INBOUND/L6-AGENT-LOG-RECALL 变化属尾部动态层，
    缓存损伤被限制在其自身与 <1K 尾巴，无需整改

缓存命中率冲刺（2026-08-25，基线 8/22-24 DeepSeek 96.783% → 目标 >99%，设计方案 §1.2）
  → 有界冷启动重组：ContextCompactionOptions.MaxHydrationTokenBudget(49152,0=禁用) 钳制重水合
    预算（DB/JSONL 双路径）；摘要链优先占预算，JSONL 胜出时补拼（原会静默丢摘要）
  → 滚动摘要链：压缩候选纳入旧代 compact_summary，新摘要统一标记 CompactedBy；
    CompactionCoverageFilter 改全部 manifest 并集（修多代 JSONL 复活缺口）
  → 转录连续性：每次压缩和 memory DB 冷水合前从 platform ChatMessages 按稳定 MessageId + durable platform Id
    高水位 after-Id 升序分页（256 条）镜像当前 session 到 memory Messages，不全扫会话/既有 ID；同步失败时水合 fail-closed，
    当前 turn/message 从历史水合排除，pre-projection live 历史不被 DB 覆盖；压缩候选包含当前围栏 Turn 或最后一个未围栏 user 时，
    在摘要和 DB 写入前以 current_turn_in_compaction_scope fail-closed；自动压缩后只合并完整 hash 围栏的当前 live Turn
    （禁止 active.Count==0 门禁）；summary-only（含超大旧摘要）/无可压缩原文直接 no-op，
    result/diagnostics compacted count 均为 0；补读排除 CompactedBy!=null 原文（阻止失忆/套娃）
  → 绝对窗口 proactive 压缩：MaxActiveRawTokenBudget(131072) 与 0.65 比例 OR 触发
    （大窗口模型旧阈值 16 万 token 才压缩）；Streaming 路径补齐轮内软压缩
  → 前缀字节稳定：ToolLoopInstruction 可见集清单（原全注册表）；ContextAssemblyService
    首组装透传 LoadedToolIds/Capability（灭 turn1→turn2 必变）；L0-AGENTS-ROSTER session 冻结
  → 归因卫生：vision-helper:/subconscious: sessionId 命名空间；image_reader 委派稳定 system 前缀
    + (artifact,prompt) 观察缓存；ConversationProjector usage 指纹查重（灭双计 NULL 桶）；
    session_rehydrated 显式归因；会话默认驻留 1h→4h
  → 验收：TestScripts/deepseek-cache-e2e.py 保持任务/模型/工具不变执行双轮真实 DeepSeek 探针，TestScripts/deepseek-cache-hitrate.py 生成日报；连续 7 完整自然日 >99%（§15.3）

自动权限审查（唯一任务 e187a8bbd2d640bb87b96fd3cf548966；ce63f8c0 已合并）
  → ToolApprovalCommandFirewall：引号/括号感知解析 PowerShell/POSIX pipeline、正则 pipe、2>&1、变量赋值；
    已知只读/构建/测试命令秒放，危险命令秒拒，绝对输出/调用运算符/子表达式等未知形态继续 LLM 审批；
    provider usage 以 purpose=approval 独立计费归因
  → feature/auto-approval-v2 的 e716829 已实现 Gate1 静态分级 / Gate2 事实自检 / Gate3 单次 LLM，62/62 测试通过，但尚未合入/部署且 AgentFirewall 仍传 Evidence=null
  → 参数级风险：save_memory get=L0、upsert/set_important=L1、delete=L2；风险事实由 descriptor+实际参数+系统证据派生，Agent 不能用 may_damage_or_delete_data=false 降级
  → 用户审批降为最后手段：L0/L1 无感放行，StaticDeny 不可覆盖，Challenge 只反馈 Agent，仅 HumanRequired 弹一次审批；相同 args/evidence 重复拒绝触发 approval_loop_detected
  → 配置由程序默认 + <DataRoot>/config/system.json ToolReview 覆盖，review profile 走 llm_resource_pool；分阶段部署/启用/下线旧 audit-agent 与默认工单入口
  → 权威设计：Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md

工具模型倾向适配（2026-08-22，实测子代理调用链驱动）
  → shell 输出去 ANSI：pwsh 注入 $PSStyle.OutputRendering='PlainText' + NO_COLOR=1 + 输出侧正则剥离兜底
  → 探查命令返回值教育：Get-ChildItem/Select-String/Get-Content 等成功输出尾附专用工具提示（94.8% shell 曾是探查类）
  → Codex 补丁格式自动转译：UnifiedDiffParser 识别 *** Begin Patch 并转 unified diff（内容匹配定位，行号占位安全）
  → file_read 护栏窗口 120→400 行（小文件与大文件双路径），减少同文件翻页重读（实测同文件重读 8 次）

ContextPipeline → stable system prefix + volatile User tail
  → 当前消息、日期、召回与 inbound context 不再插入 system prompt
  → AgentExecutionService 用 `[CURRENT USER TURN input_sha256=…]` 围住本轮文本与 typed ContentParts；若预算裁剪/投影后围栏缺失，Buffered/Streaming 在 provider 调用前 fail-closed
  → AgentExecutionService → ToolResultContextPolicy（模型历史最多 8 KiB；原始完整结果写入工作区 `.pudding/context-tool-results`，不做模型输入脱敏）
  → search_tools 已发现 schema 在 live session 内保持加载，避免跨 dispatch 重复收缩/扩张

用户 Turn → TurnExecutorAdapter → AgentExecutionAdmissionCoordinator（foreground）
  → 抢占同 workspace/agent 的 Message Fabric 后台执行（含 subagent_result）
  → MessageDeliveryDispatcher 取消旧执行并把 exact delivery 立即 defer 回队列
  → foreground demand 存续期间 recovery/idle drain 不领取后台 delivery
  → MessageFabricStore 依据 wake event deliveryId 精确 claim，避免旧队首抢在用户事件前执行

Agent `send_message` → MessageDeliveryPolicy → Message Fabric 反风暴消费
  → 默认 `inform/report_result` 为 `notify`：不创建 Turn、不调用模型；`ask/request_review/delegate` 才为 `execute`
  → `agent_reply` 永远被动，ConversationReplyProjectionWorker 最多投影一次，切断 A→B→A 自动回声
  → MessageDeliveryDispatcher 按 workspace/Agent 跨 room 原子领取最多 20 条 notify，ConversationNotificationStore 逐条原子写 ChatMessage + message.created 后 ACK
  → 合并 claim 不合并内容/因果链/UI 卡片；execute 仍一条 delivery 对应一个 canonical Turn
  → `message_deliveries.handling_mode` + bootstrap/migration 回填历史普通 inform/report_result/agent_reply

P1-2 召回同源去重（压缩摘要/原文/recall 片段 ≤1 次注入）
  → SessionChunkIndexer（写侧）回查 Messages 补齐 CanonicalContentHash/ContextGeneration 冗余列
  → MemoryLibrary 第 5 路 LEFT JOIN Messages 取 hash/generation/CompactedBy，默认过滤 covered chunk
  → RecalledMemory/SearchHit 透传 SourceMessageId + CanonicalContentHash
  → SubconsciousRecallPipeline 注入前经 CompactionCoverageFilter 过滤 covered + 同轮 hash 去重
  → ContextPipeline assembler 兜底去重（双保险）

P1-3 Reasoning 紧凑归档（v2 sidecar + ThinkingJson 不回流）
  → ReasoningCompactCodec（PuddingCore）：{v:2,text,chunks:[{o,t}],hash} UTF-8 字节偏移 + delta 时间戳 + SHA-256，旧格式兼容、hash fail-open
  → MessageDeliveryDispatcher 写侧：thinking 帧累积 → ReasoningCompactCodec.Encode 落 v2（T2）
  → MessageApiController / AgentConversationProjectionService 读侧：codec 双格式解码（T3）
  → JSONL/Compaction 路径断言：ThinkingJson 不进模型 prompt / compact 输入（T5）
  → E2E：写侧 v2 → 读侧解码 → UI DTO 逐字节还原 + hash 校验（T6）

Plugin configuration → Plugin Resolver → PluginActivation
  → capability registry（Tool/LLM/Prompt/Context/Connector/Job/Presentation）
  → Typed Hook（Guard/Transform/Around，同步有界干预）
  → state commit + transactional outbox → durable DomainEventLog
  → per-consumer checkpoint/retry/dead-letter → UI projection / Heartbeat / Subconscious / Self-learning
  → Session/Run/Turn/LLM/Tool/SubAgent/Message/Compaction/Heartbeat/Job/Learning 使用统一状态机与提交后事件

spawn_sub_agent → SubAgentInvocationService → SubAgentManager
  → model 参数必须是 providerId/modelId 完整路由；裸 modelId 多 provider 注册时 FileLlmResolver 报
    "exists under multiple providers"（2026-08-24 起 list_llm_providers 内置工具输出实时路由表与
    ambiguous_model_ids 歧义清单，不含 apiKey/baseUrl，已入 CoreToolIds 常驻可见；应急快照
    memory/llm-providers-cheatsheet.md 转兜底）
    → `runtime.execution.json` 提供系统 profile（默认 600/2400/24h，非强制统一值）
  → 内部契约 SubAgentSpawnRequest 已含 `int? MaxRounds` 等请求级预算字段（N00 已实施）
  → 父代理工具 schema 面向可选 `max_rounds` 的暴露随 ADR-087 后续批次（SA-MSG/SA-ASK）落地
  → AgentExecutionService 在启动、剩余 80%/50% 与预算耗尽时注入预算通知
  → 正常轮次/时间耗尽后提供 20 轮、最多 30 分钟的收尾宽限，终态为可续跑 `budget_exhausted`
  → `resume_sub_agent_id` 复用 SubSessionId/上下文、创建新 runId 并重置系统计数器
  → run archive 固化实际预算与 `subagent.budget.notice`
  → 子代理轮内 warm-prefix checkpoint（2026-08-28）：估算达 0.65×有效输入上限时以原样
    system/tools/history + 固定尾部指令生成摘要，只有有效且缩小的 checkpoint 才原子替换旧区间；
    失败保留完整 history、每 dispatch 最多尝试一次；写 subagent.context.compacted 事件，
    LlmRequestBudgetGuard 硬悬崖保留为最后防线
  → FileSubAgentRunStore 归档并发协议（ADR-060 §3.11）：读写同一 per-run gate、读方 FileShare.ReadWrite、
    JSONL 追加 sharing violation 退避重试；重试耗尽丢弃事件写 archive-degraded.json 降级，不杀死运行
  → FirewallContext.WorkingDirectory 从 ToolExecutionContext 冻结；防火墙 WorkspaceGate、审批目标解析
    与文件工具统一委派执行根（worktree），不回退进程级静态 workspace root
  → ContextPipeline 以 ConfigurationAgentInstanceId 读取持久 Skill/人格/记忆，缺失 Skill 索引不写盘
  → SubAgentTransientDirectoryGcService 只隔离终态/孤儿的精确空脚手架，运行归档与有状态目录不进入 GC

Runtime 跨层服务 → Core contracts → Platform implementations
  → SubAgentTool → ISubAgentPool → SubAgentPool
  → AgentDiagnosticsTool → ITokenUsageEventRepository → TokenUsageEventRepository
  → FileReadTool/FilePatchTool → Runtime-owned FileChunkService

PuddingHost 产品组合根 → Runtime tool assembly scan
  → 每个自动发现的 IPuddingTool 都参与 ValidateOnBuild
  → 新工具的构造依赖必须同步注册到 PuddingHost 的 Runtime 扩展
  → AgentExecutionAdmissionCoordinator 必须在 Runtime 与 PuddingHost 两个组合根都注册为 Singleton，供前台 Turn 与 MessageDeliveryDispatcher 共享准入状态
  → PuddingApplicationHostCompositionTests 用 DesktopChild 入口防止“构建成功、Core 启动即退出”

Desktop → Core Ready 契约（2026-08-28 增加冷升级启动租约）
  → Core 初始化期间每 5s 发 PUDDING_DESKTOP_STARTING（协议/PID/单调序号）；Desktop 以 startupTimeoutSeconds 作为静默超时，合法租约允许 10 倍且最高 10 分钟的有界冷升级窗口
  → Core 在全部 hosted service StartAsync 返回后才发 PUDDING_DESKTOP_READY；租约不能替代 Ready、PID 校验或 /health/ready
  → ConnectorHostLifecycleService 本地注册保持同步，StartAllAsync 后台执行（ApplicationStopping 绑定）
  → FeishuWebSocket 端点发现/WS 握手各 15s 上限；飞书不可达只 Faulted 单个连接器，不阻塞 Ready

当前视觉链路（2026-09-12复核：ADR-077 V0–V3 已有实现；ADR-088收敛待实施）：typed image content part（`ContentPart{type=image, artifactId, detail}`）
  → ConversationAcceptanceStore 同事务写 `ChatMessages.ContentPartsJson`（v1 信封，Content 为文本拼接投影）
  → ExecutionRunCoordinator 读 canonical parts + 冻结 AgentExecutionSnapshot（CapabilityTags/Protocol/VisionPolicy/VisionHelperRoute）
  → 主模型带 vision：ChatMessage.ContentParts 原生进入请求；文本模型只收 `artifact://` 占位并显式调用 image_reader
  → 已删除 VisualArtifactObservationService 自动预观察旁路（服务+注册+旧测试）
  → LlmVisualInputPlanner fail-closed；已有inline/Files两种路径，旧产品策略单图2MB转Files、inline聚合40MiB、默认8张和384估计由V5/V7纠偏，不作为当前各Provider通用限制
  → Responses：user `input_image`（detail original→high）；`function_call_output.output` 支持 [input_text, input_image] 数组
  → ChatCompletions/Anthropic 遇图片工具结果抛 vision_tool_output_not_supported
  → Image Reader（image_reader）：path 唯一必填（http(s) URL / 宿主绝对路径 / artifact://），Low 权限 ReadOnly|RequiresNetwork（2026-08-28 裁定：纯只读无写/删路径，免审直通）
    → auto 优先 native（ToolExecutionResult.ToolContentParts 图片部件回交调用模型，零辅助 invocation）
    → 文本调用模型或显式 mode=delegate 时用 manifest `visionHelperModel`（原 imageReaderModel 已改名）单次可归因 invocation
  → image_reader source resolver：URL 有界下载（每跳 SSRF/DNS 重校验、禁内网）、本地只读、内容哈希稳定 vision-* Artifact
  → DB 水合经 MessageEntity.AttachmentsJson 恢复图片 part；Snapshot 工厂冻结能力，单一判定来源
  → V3 Files已实现上传、持久remote ref与过期恢复；V4当前真实模型smoke与进程外验收待做。V7进一步收敛流式读取、多Provider传输、全请求预算和引用生命周期

Desktop Storage → CoreStorageManagementClient
  → GET/POST /api/admin/storage/databases（Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceService
    → 平台库页面/行/重复索引 + 代码索引作用域明细
    → PreviewId（10 分钟）→ 白名单批量删除 → checkpoint/VACUUM → 重扫
    → session_event_log / conversation_events / ChatMessages / memory 永不进入清理目标

PuddingHost → RetentionPruningService（platform.db 自动保留调度壳，ADR-076 收编）
  → 策略读 <DataRoot>/config/system.json storageManagement（StorageRetentionPolicyService，CAS + fail closed）
  → 执行全部委托 StorageMaintenanceCoordinator（唯一在线维护 writer：双优先级队列 + DataRoot maintenance.lock）
    → StorageCleanupExecutor 小批执行器（100 行/批、250ms 让步、busy 退避、rowid cursor 续行）
    → conversation_events 证据先 RetentionArchiveWriter 归档再删；在线 VACUUM 已全线移除
  → 遥测/上下文原始行自动清理默认关闭（聚合未实现 fail-safe）；Debug 字段/运行活动/日志默认开启
  → 旧 /api/admin/storage/databases 三端点保留双通道鉴权，Execute 内部经协调器，Desktop 旧页面无感

ADR-076 存储管理（Core + Web /storage，2026-08-24 首轮实现 Phase 0–3）
  → StorageDataClassCatalog 语义目录（9 类型 + evidence.conversation-events，物理白名单 + 保护清单）
  → StorageInventorySampler 有界采样（50–100ms slice、LIMIT 300 样本、索引探测 min/max、目录分片）
    → StorageInventorySnapshotStore 原子合并快照 + history.jsonl（每小时一点、90 天趋势）
    → POST inventory/refresh 立即 202、重复请求合并；GET overview 只读缓存
  → StorageMaintenanceJobStore durable 作业（maintenance/storage/jobs/<id>/job.json + events.jsonl，90 天轮转）
  → StorageAdminController 语义 API（overview/data-classes/refresh/history/policy(CAS)/preview/job/confirm/cancel，Admin JWT）
  → Admin /storage 页面：总览（文件+可复用页+分类估算）、SVG 占比圆环+趋势堆叠图、分类报表、
    可清理选择器（Evidence 只读展示）、受保护区、策略 Drawer、Preview 确认 Modal、作业列表（轮询+取消+确认）
  → 消费循环修复：writer Complete 后 WaitToReadAsync 同步 false 自旋会挂死宿主 StopAsync（测试抓出）

DesignRequest + ExpertGroupDefinition → DesignCouncilPlanCompiler
  → 上下文审计 → 调研 → 独立提案 → 交叉批判 → 主席综合 → 独立终审
  → 输出 Draft + RequiresExplicitActivation
  ├→ 当前 MOA 运行：DesignCouncilRunStateMachine → ISubAgentOrchestrationRunStore
  │  → DesignCouncilRuntimeService（精确 provider/model，无 fallback）
  │  → ISubAgentInvocationService（复用 sub-session/run archive/deadline）
  └→ 通用化迁移：DesignCouncilOrchestrationGraphAdapter
     → pudding.agent-orchestration/v2（component/trigger + typed multimodal port + control/data edge）
     → AgentOrchestrationGraphCompiler（组件冻结、端口/schema/route/reference/DAG 校验，不执行）
     → SqliteAgentOrchestrationStore
       （revision CAS → run/node projection → atomic claim/fence → append-only event replay）
     → AgentOrchestrationApiController
       （graph/run discovery + catalog/revision/run/events → AgentOrchestrationEventFollower → replay-to-live SSE）
     → AgentOrchestrationLayoutApiController
       （GraphLayout read + Admin CAS write；不可变 Revision/Node 先只读校验，与 executable revision/run facts 隔离）
     → AgentOrchestrationManagementApiController
       （Admin Graph create + Head-CAS delete；任意 Run 历史都会阻止删除）
     → AgentOrchestrationHttpHookApiController
       （Admin debug POST + 显式 immutable revision；从不解析 Graph Head）
       → AgentOrchestrationHttpHookService
         （sourceEventId 幂等 + payload binding → durable Run Inputs → Create/Activate）
     → AgentOrchestrationRunCommandApiController
       （Admin 顶部“运行” + 显式 immutable revision + typed inputs → ManualRunService → Create/Activate）
     → AgentOrchestrationWorkerService
       （SubAgent → SubAgent → image-generate → image-preview；按端口 outputs_json 传递文本/Artifact、lease 续租、后继 Ready/Skipped 与 Run 终态同事务推进）
     → Admin /orchestration
       （紧凑 Graph/Run 控制条 + 顶部运行 → 全宽画布 → 悬浮工作台 → SubAgent 模型/模板/角色设置与文本输出 → 图片生成/展示组件自有预览 → Revision/Layout CAS）

Chat 插嘴模式（当前 Turn steering）
  → useMessageInteractionQueue：busy 时 Enter 仍立即提交 canonical Turn API，受理后由 chat_execution_commands + ChatExecutionWorker 持久排队；不创建 React local_pending
  → Composer 独立 ⚡ 设计增量：active Turn + 非空纯文本时直达同一 Steering admission，不写 pendingSendQueue、不创建普通 delivery/第二 Turn；202 后 compare-and-clear，409/失败保留草稿且不自动排队
  → 第一批交互一致性落地（2026-09-11，Docs/Reports/前端交互体验优化建议-2026-09-11.md §0）：
    鼠标发送按钮不再挪用作停止——运行中有草稿=「加入队列」（与 Enter 同链）、⚡菜单=「补充给当前任务」（与 Ctrl/Cmd+Enter 同链）、
    独立「停止当前执行」按钮=本地 abort + requestActiveTurnCancel（新增 cancelConversationTurn 封装，接线既有 ADR-059
    POST .../turns/{turnId}/cancel；已结束/未受理按竞态静默）；useMessageSend 失败恢复草稿（restoreDraft 端口，空输入框才回填）、
    busy 提交 202 受理后「已加入队列」回执；状态胶囊/余额徽标补键盘激活
  → MessageQueueProjectionService：默认只读投影 queued/retrying deliveries + pending commands；claimed/running 只在诊断查询出现
  → MessageQueueDropdown：内容宽度胶囊摘要；详情向上悬浮限高，明确“认领后转入会话轨迹”
  → POST /api/v1/conversations/{conversationId}/turns/{turnId}/steering + X-Workspace-Id
  → ConversationTurnsController → CreateSteeringHandler（Running + Workspace/Agent 围栏）
  → SessionSteeringService → session_steering_messages durable queue（不可变 target_turn_id；source_queue_item_id 重试幂等；旧库由 bootstrapper 原地升级）
  → AgentExecutionService：每次 LLM 前只 drain 当前 Turn；最终回复后的 late safe boundary 命中则继续同一个 Turn
  → steering.injected + agent.steering.inject 形成消费证据；不取消正在运行的工具/模型请求
  → 独立按钮方案：Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md（Proposed；P1 Task ed88185f1d3b4e16a70e9b9ea0f0e040）

Chat first paint → AgentConversationProjectionService
  → 过滤 transport duplicate 占位、按 pudding-message envelope message_id 折叠历史重复入站
  → system/heartbeat envelope 正文投影为 context.text，不把协议 JSON 显示在聊天气泡
  → 最近 20 条可见消息 + active run 最近 64 条过程明细/全量摘要
  → TurnSurfaceStore（2026-08-24 行为链重构）：canonical turnId + 别名归并
    （messageId/runId/commandClientId），完成 turn 经 per-message 明细接口懒水合
    （text/thinking/tool/delegation 同一事件流，eventId 幂等去重），
    终态/刷新后轨迹从投影重建；AgentConversationProjectionService 明细端点
    补 text 事件 + delegation 三重排除修复（canonical 事件名/kind 映射/run 过滤）
  → 验收二轮修复（2026-08-24）：委派节点按 subAgentId upsert（重复 spawn 不再
    留下永久 running）；DTO 透传 canonical sequence/turnId/runId + activeRun 快照
    补正文事件（运行态即可交错文本段）；懒水合有界化（MessageRow 通过消息滚动
    容器 IntersectionObserver 在 600px 预取区注册可见 turn、并发窗口 ≤2 且槽位
    完成后持续排空可见队列；组件挂载不再批量水合全部历史）；正常流保留真实行高，
    删除会在动态 Agent 行上积累过期 remembered size 的 content-visibility 占位；
    虚拟化按消息内容 + 已水合 canonical 行为链 render weight 即时开启；
    AgentTurnCard 大卡片外壳（暖色表面/1px 边界/14px 圆角，终态不重挂载）；
    工具行 aria-label 带状态 + 成功终态卡「已完成」标记
  → canonical event → ExecutionFlowProjectionIndex（2026-08-27）
    （eventId 先去重；同帧按 Turn 合并；只重投影 dirty Turn；快照结构共享；
      终态释放原始事件/eventId；session switch 硬 reset；collector 保留 typed payload）
  → MessageList → messageProjection（保持已组装消息顺序，未匹配 active run 留在当前流末端）→ MessageViewportRuntime（虚拟化、锚点、贴底）
  → ChatMessageStyleProvider（消息树共享一次聚合样式注册）
  → MessageRow（稳定块直接渲染 + 语义 memo；接收本 Turn 具体 Projection，其他 Turn revision 不失效；不再经过单条 MessageStream 兼容重建）
  → AgentMessageBubble → TurnContentStream（AgentTurnCard 内容块流，2026-08-25）
     （per-turn canonical 投影 nodes 按 sequence 形成 TextBlock ⇄ ActivityGroup 交错流；
       正文永久可见且只渲染一次，answerMarkdown 不再以字符串关系切回第二正文气泡；
       最新尾部组始终展开、历史组折叠并卸载成员 DOM，组内长详情默认单行折叠；
       单 Turn 默认只挂载最新 40 个内容块、单 ActivityGroup 最新 24 个行为节点，
       较早轨迹按 40/24 项渐进揭示，避免单个长 Turn 绕过消息级虚拟化撑爆 DOM；
       路径 A processItems adapter 只作无 canonical 正文节点时的旧记录回退；
       ProcessSummaryItem 必须透传服务端 sequence，缺失即 fail closed，不用下标伪造；
       已封闭 TextBlock/ActivityGroup 语义 memo，append 只更新尾段/状态变化组；
       TurnOutputChunker 非 delta 事件先 flush 正文/思考缓冲，轮次边界进 canonical sequence；
       终态 reply 分叉以服务端为准不再拼接（重复输出修复）；
       流式中同回复单卡：activeRun↔本地 turn 合并移除「本地正文为空」门槛
       （commandClientId 已是同一发送的强约束），hasProjectedUserTurn 增加
       turnId 锚点、合并保留本地 clientMessageId（2026-08-24，生成中多卡修复）；
       ReasoningDisclosureRow 多段 + 段时长 chip、ToolCallRow 耗时/exit 折叠行、
       TurnStatsLine 终态计量、PresentationRegistry 五类 renderer：terminal/diff/read/search/web）
  → 主消息运行监视区（首 Token 前也保留主代理“查看过程”：当前阶段 + 推理摘要 + 工具操作 + 有界子代理委派状态；不展开子代理内部过程）
  → subAgentReducer（事件/快照统一投影；状态接口携带 canonical runId 并可重建漏收 created/started 的运行；budget_exhausted 终态单调；原样展示有界的实际 reasoning_preview）
  → SubAgentActivityDock（实时 reducer + 终态 run 归档一次性回放；活动 run 零归档轮询=ADR-060 §3.11；归档降级时展示 archive-degraded 提示；刷新后按 canonical runId 恢复子代理任务/推理/工具/轮次/耗时/输出；Agent-first 路由回退 mainSessionId 保证图标可见）
  → 展开过程摘要时才构建 rounds / trace chips
  → MessageItem 先渲染纯文本，异步加载 Markdown/KaTeX 增强块
  → 任务看板、Checkpoint、历史搜索、开发面板、子代理检查器、右键菜单、
    会话诊断 Drawer 与摄像头输入仅在首次使用时加载；工具栏 hover/focus 可预取
  → 余额、Goal、会话推断等辅助请求在首帧后的 idle window 启动，
    不阻塞消息投影、当前 Agent 与实时事件；旧 WebView2 以短 timer 有界退化
  → 常驻埋点使用 perfEventRuntime；完整诊断模块仅在 perf/debug 模式加载
  → npm run build 强制 Chat bundle budget：同步脚本 ≤1536 KiB、Chat 路由 ≤480 KiB，
    并阻断任务看板/Checkpoint/ContextMenu 回流首载共同块
```

Task auto execution settlement (2026-08-29 hardening)
  → GoalSettlementStore: terminal completeness scans the full Turn window; EvidenceRefs retain the newest bounded 128 events
  → settlement atomically projects RunId/elapsed/LLM rounds/tool calls/input-output tokens into GoalIteration + Goal totals
    (root Turn canonical usage + recursive descendant TokenUsageEvents inside the exact Turn window)
  → blocked Task-bound attempt atomically releases Binding/Assignment/Reservation, keeps Task Blocked/NeedsReview and terminals the attempt Goal as Failed audit history
  → dispatch atomically retires legacy detached Blocked Task Goals for the same Agent/conversation (including a prior Task) before creating a fresh fenced Goal, preventing UX_goal_runs_active from masquerading as task_goal_lost_race
  → five-minute tracker/repair heals historical blocked_binding_still_active ownership before the next dispatch
  → legacy delivered-without-execution assignments are fail-closed to Blocked and released after the stall threshold
  → scan order is tracking/repair → availability rebuild → candidate evaluation → dispatch in the same interval
  → task dispatch outbox revalidates Task/Assignment before send and again at atomic bind; stale/terminal conflicts dead-letter, transient send/bind failures stop at MaxAttempts, shutdown cancellation stays lease-recoverable
  → WorkUnit remaining input/output/cost budgets propagate through tool and sub-agent boundaries; synchronous delegated usage returns to the parent ledger
  → AgentOutputTruncationPolicy: one action-forcing recovery for length/incomplete, then explicit failure
  → ConversationProjector: direct usage dedup uses SQL-stable fingerprint candidates + bounded in-memory time fence

Task scheduler + Goal user control plane (2026-08-31 source-ready)
  → Admin Task board exposes explicit auto-dispatch opt-in and structured task routing facts
  → SchedulerDrawer consumes server status/policy/actions/evaluate: pause/resume, immediate scan/repair, revision-CAS hot policy
  → TaskAutoDispatchScanRunner is the shared periodic/manual reconciliation order; dynamic policy reaches worker, event bridge, coordinator and starter
  → GoalBanner covers start/pause/resume/stop/new without deleting durable Goal history
  → Goal continuation transport keeps new Unicode readable while escaping envelope delimiters; Admin projections decode legacy JSON escapes only for server-marked goal_continuation messages
  → authoritative design: Docs/Features/任务调度器与Goal用户控制面设计.md; external Desktop/Core deploy and product smoke remain separate gates

Task scheduler effective-dispatch closure (2026-09-01 proposed)
  → effective dispatch requires Intent → durable task-scoped Decision/Outcome → fenced Assignment/Reservation → TaskGoalBinding/GoalRun → canonical ExecutionRun → verified Task settlement
  → legacy execution claims must join execution_runs terminal state; terminal/orphaned claims release ownership through Serializable deterministic repair instead of remaining Healthy forever
  → event Coordinator completes each Intent only after its triggering Task has a durable stable outcome; single/bounded modes share the same Bridge/Coordinator/Starter gates
  → durable scan-run summaries preserve empty/failed scans across restart; Blocked recovery UI uses diagnostics + preview + per-task ETag commands
  → code-level implementation and task slicing: Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md

## 测试项目

| 项目 | 覆盖 |
|------|------|
| `Tests/PuddingCoreTests/` | 工具契约、LLM 网关、MessageFabric |
| `Tests/PuddingRuntimeTests/` | Agent Loop、上下文管线、语音/图片 |
| `Tests/PuddingPlatformTests/` | 渠道配置、Artifact 存储、图片生成 |
| `Tests/PuddingMemoryEngineTests/` | Library/Book/Chapter、FTS5、Skill 去重 |
| `Tests/PuddingMemoryEngineBenchmarks/` | BenchmarkDotNet |
| `Tests/PuddingCodeIntelligenceTests/` | 代码索引 |
| `Tests/PuddingCodexServiceTests/` | Codex MCP Service |
| `Tests/PuddingFullTextIndexTests/` | 全文索引 |
| `Tests/PuddingWebApiTests/` | Web API |
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client、Debug 调试模式（路由/反向代理集成/SSE/WS 中继/前端监督器/源码构建器/前端构建部署） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |

## 2026-09-05 效率与代码审计入口

持续恢复入口：`Docs/Reports/PuddingAgent持续优化执行台账-2026-09-05.md`（既有 30 分钟 heartbeat）。第四轮：`StorageInventorySampler` 的两个索引端点/2000-entry 惰性目录预算；`TokenUsageRecorder` 每当前层只读上一条 hash，`PlatformDbContext` / `TokenUsageSchemaBootstrapper` 同步覆盖索引。定向19/19、扩展96/96；第四轮已部署，同任务56.898s/预热13.253s。`Docs/Reports/PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md` 区分模型流与本地记账、GC heap与Private；后续Private仍回升，不能宣布整体内存优化通过。

第三轮部署与产品证据：`Docs/Reports/PuddingAgent第三轮部署与产品验收-2026-09-05.md`。Desktop 主管不变，新 Core/前端已部署，hash/Ready/单次只读功能通过；161.749 秒流空档、预热内存与夜间吞吐仍待收口。`TestScripts/invoke-pudding-desktop-deployment.ps1` 提供停机备份/预构建部署/全量前端核验；`test-pudding-deployment-gates.ps1` 覆盖历史 PID 停机判定（7/7）；`measure-pudding-process-baseline.ps1` 记录进程树 CPU/Private/WS，前后负载不一致不得当作 A/B 收益。

第二轮前端与发布：`Docs/Reports/PuddingAgent第二轮前端修复与发布验证-2026-09-05.md`、`Docs/Reports/pudding-agent-round2-build-2026-09-05.json`。`PuddingAdminShell/EntityCard/PageHeader/StatusBadge/Toolbar` 使用 createStyles；`PerfTab` 使用实际诊断合同；SSE reconnectCount 状态穿透 ChatLayout，移除 ChatMain 500ms 轮询；`PuddingHostContent.props` 的 `PuddingAdminDistPath` 与前端 `PUDDING_ADMIN_OUTPUT_PATH` 支持隔离打包。源码检查和发布核验通过，未部署。

首轮实现与验证：`Docs/Reports/PuddingAgent首轮修复与验证-2026-09-05.md`。新增 `Source/PuddingPlatform/Services/Scheduling/LegacyTaskExecutionProbe.cs`，由 Tracker/Repair 与完成结算共用精确 Command→latest Run 解析；`ExecutionRunCoordinatorMonitorTests.cs` 覆盖监视异常取消；`TestScripts/test_deepseek_cache_hitrate.py` 覆盖完整北京时间日→UTC 边界。产品部署与性能对照仍待验收。

`Docs/Reports/PuddingAgent效率与代码审计-2026-09-05.md`：TaskExecutionTracker legacy claim → canonical terminal、FilePatchTool schema/缺字段语义、useSessionEventConnection 鉴权重试、SubAgentConversationProjectionWorker/FileSubAgentRunStore 增量回放边界，以及 ExecutionRunCoordinator monitor fault。报告附七日指标与验证结果；这些是诊断发现，尚未标记为修复或产品验收完成。
