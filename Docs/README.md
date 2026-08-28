# Pudding Agent Network 文档索引

最后更新：2026-08-26（新增 Chat 独立“⚡ 插嘴”按钮设计；自动权限审查与危险操作防火墙已统一设计并合并任务；新增子代理活动轨迹实时回放与运行检查器修复方案；Agent Harness H0 已实现；以上均待部署或产品验收）

## 文档定位

这里是 Pudding Agent 的设计入口。当前产品主线是 Windows First 的 `PuddingDesktop.exe`：WPF 负责 Windows 11 Shell、WebView2 和进程监督，独立的 ASP.NET Core 子进程继续承载 API、Controller、Runtime、Connector 和 SQLite。现有 Web Workbench 通过内置静态资源复用，产品运行不依赖命令行、Python 或 Node。

## 建议阅读顺序

1. `Docs/架构.md`
	 - 架构总览、分层边界与阅读地图。
2. `Docs/07架构/README.md`
	 - 模块级架构分册入口，包含 Runtime、Controller、Platform、治理、数据模型与 V1 落地说明。
	 - 其中 `10事件系统与事件总线.md` 负责解释统一事件模型、订阅、唤醒、重放与死信策略。
	 - `11工作流与任务图.md` 负责解释工作流节点类型、触发方式、任务图表达与 Agent 生命周期。

3. `Docs/Tasks.md`
	 - 全局任务入口与 V1 目标，任务状态通过 Todo API 实时查询，不依赖硬编码表格。

## 当前主线文档

- `Docs/Features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md` / `Docs/superpowers/specs/2026-06-06-runtime-steering-queue-design.md`
	- 复用既有 current-Turn durable Steering，为运行中 Composer 增加独立 `⚡` 直达入口；明确不进入普通待发队列、不创建第二个 Turn、202 后 compare-and-clear、409/失败保留草稿、图片 fail closed、单飞幂等和明确部署 smoke。当前仅设计，关联 P1 任务 `ed88185f1d3b4e16a70e9b9ea0f0e040`，尚未实施/部署。
- `Docs/superpowers/specs/2026-06-03-auto-tool-approval-design.md`
	- 自动权限审查唯一设计入口：合并确定性危险命令防火墙与三层漏斗，冻结用户审批最后手段、参数级风险分类、系统派生风险事实、重复审批熔断和 `save_memory upsert` 零审批；关联唯一 P1 任务 `e187a8bbd2d640bb87b96fd3cf548966`，尚未部署验收。
- `Docs/Features/AgentHarness兼容与工具调用效率修复设计方案.md` / `Docs/07架构/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md`
	- 冻结“canonical 工具唯一、统一执行边界前适配、短稳定提示、no-match 结构化、round-boundary 动态工具激活、discovery-only 熔断、可选而非默认 bundled rg”的 Harness 兼容边界；2026-08-28 已修复 216 次 `search_tools` 高命中零 Goodput 事故，进程外部署与真实模型验收未完成。
- `Docs/Features/Agent消息交错内容流与最新行为组披露完整实施方案.md`
	- Flash 可直接施工的代码级合同：canonical sequence、TextBlock ⇄ ActivityGroup、会话级唯一最新披露 owner、完整 reasoning 换行、工具详情懒加载、柔和收起/卸载、性能、逐文件任务卡、测试命令和双阶段真实验收。
- `Docs/07架构/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md`
	- 冻结一个 AgentTurnCard 内真实交错、唯一正文源，以及“当前最新 Agent 回合最后行为组持续展开；最终正文不关闭；新行为/新回合才转移并收起旧组”的架构决策。Accepted 只表示设计决策冻结，不表示实现完成。
- `Docs/07架构/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md`
	- 冻结主视觉模型直接消费 typed image content、Workspace Artifact、DeepSeek Responses `input_image`/图片型工具结果、Files API、多轮重启恢复、fail-closed 与视觉用量；Image Reader 改为默认只传路径的按需取图工具，支持 URL/任意绝对路径，并保留显式 helper 委派能力。当前为 Proposed。
- `Docs/Features/Chat图片消息回放与前端旧Bundle缓存修复方案.md`
	- 记录 2026-08-26 图片占位故障的消息/Artifact/DOM/Bundle 证据链；修复 Agent-first `contentParts` 投影断点、localhost 旧 Service Worker 清理、静态资源缓存合同、build identity 与两段式产品验收。当前为 Proposed，关联任务 `ceba781342aa4353901654d1897092cb`。
- `Docs/Features/子代理活动轨迹实时回放与运行检查器修复方案.md`
	- 记录 Run `run_20260826_111621_3a711865a9f8` 的 archive/cursor/canonical event/UI/Bundle 证据链；修复活动子代理未纳入 gap replay、空卡片无同步降级、聚合计数受有界详情截断与 build identity 不可见问题，坚持活动 Run 零归档轮询。当前为 Proposed，关联 P1 任务 `791d062fa6ea44f18bfe5027a37696d0`。
- `Docs/07架构/89ADR-074Goal持久目标自主续行与自动压缩ADR.md`
	- 冻结 GoalRun 持久续行、证据验证、Task-bound Goal、Agent 可用性感知与低峰自动派发；明确 Auto Task 以 Goal 为前置且不依赖 Heartbeat。
- `Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md`
	- 2026-08-28 已部署的是五分钟 Shadow；后续源码已补结构化路由、auto-dispatch opt-in、Backlog refinement、版本化 WorkUnit、Acceptance/执行前双重 Plan/Node 围栏、顺序推进、round/tool/time/input/output/cost 硬预算和五分钟确定性 repair。新源码尚未进程外部署；AwaitHandle/checkpoint、blocked 重预约、动态模型反馈、authoritative 与七夜验收仍未完成。
	- 按 Core/Platform/Runtime/Host/Admin 列出类、表、事务、事件、文件、施工卡、测试、切换和生产验收门禁；2026-08-28 已进入五分钟 shadow 对账，修复终态 Assignment false-busy 与通用 PATCH 伪完成，authoritative 仍未开放。
- `Docs/07架构/91ADR-076遥测与调试数据保留及Core存储管理ADR.md`
	- 冻结 Core + Web Admin `/storage` 边界、语义数据类型目录、唯一在线维护 writer、关键事实保护和禁止在线全库 VACUUM；当前为 Proposed。
- `Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md`
	- 自动过期、缓存快照与后台增量估算、分类占比图/趋势报表、按类型/时间近似 Preview、异步清理作业、策略配置、文件级施工与验收方案。
- `Docs/07架构/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md`
	- 当前产品施工入口；列出 30 项产品任务和 17 项 T00–T16 平台底座任务的目标、优先级、工作量、难度、依赖和设计位置，并把各专项 Phase 去重到唯一 Canonical Owner。产品顺序为任务看板 → Auto/Cron → 完整轨迹 → 实时指标 → 插件化收口。
- `Docs/07架构/86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md`
	- WorkspaceTask、五列 Board、Failed/Reopen、手工/Auto 派发、受限 Cron、Task executionWindow、provider/model 价格时段 Resolver、Task Tools、Admin 和恢复的任务领域合同；不新增 `work-policy.json`。
- `Docs/deepseek-reference-architecture-master-plan-2026-08-14.md`
	- 本次会话设计总入口；以“模型、工具、技能、会话、Agent Loop、沙箱、存储、调度和 UI 均为插件”为第一原则，汇总组件级映射、文件级修改矩阵、任务图、T00-T16 施工卡、验收和风险边界。
- `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`
	- 插件、Typed Hook、durable event 与统一生命周期的上位架构；覆盖心跳自主推进和事件驱动自学习闭环。
- `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md`
	- 工具 canonical output、callId、结构化错误、执行 Hook、并发、spill 与 presentation 方案。
- `Docs/deepseek-harness-message-card-alignment-2026-08-14.md`
	- 消息、推理、工具调用与子代理过程的前端投影方案。
- `Docs/架构.md`
	- Pudding Agent Network 的架构总览与阅读入口。
- `Docs/07架构/README.md`
	- 按模块拆分后的架构分册目录。
- `Docs/Tasks.md`
	- 全局任务入口，任务状态通过 Todo API 管理。
- `Docs/07架构/18上下文缓存可观测性ADR.md`
	- LLM prompt cache hit/miss 解析、统计和前端可观测基线。
- `Docs/07架构/43ADR-042上下文自动压缩与主动Compact命令ADR.md`
	- 长会话 Compact、LLM 前置输入压缩、Headroom 研究结论和可逆取回边界。
- `Docs/Features/上下文自动压缩与Compact命令设计方案.md`
	- Compact API、上下文健康状态、InputCompression 原型和验收计划。
- `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md`
	- 7 日 Token/工具重放/搜索失败/ZIP 基线，以及无损 artifact、分级压缩、Compact 覆盖门禁和 DeepSeek 缓存 `>99%` 的施工与验收合同；2026-08-28 追加 95.92% 事故基线、Harness warm-prefix checkpoint、prefix-v2 与审批控制面降耗实施记录。
- `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md`
	- 聊天页主代理余额徽标（DeepSeek 首个落地）+ 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器、前端 `providerBilling.ts` 展示适配器；含新服务商扩展步骤、刷新策略与 apiKey 安全约束。已实施（2026-08-24）。

## 主题文档分组

### 1. 渠道、网关与接入

- `Docs/06智能体网关/`
- `Docs/Config/hooks.md`
- `Docs/Config/pudding-yaml.md`
- `Docs/07架构/63ADR-063飞书Agent绑定与可靠消息网关ADR.md`
- `Docs/07架构/67ADR-066抖音个人开发者评论接入与浏览器自动化ADR.md`
- `Docs/07架构/68抖音接入与通用WebView2自动化开发实施规格.md`
- `Docs/07架构/69PuddingDesktop浏览器工作区运行中心与存储管理实施规格.md`
- `Docs/07架构/70Phase2A-1通用BrowserBridge与双标签工作区开发工作指令.md`
- `Docs/07架构/71Phase2A-1验收补丁真实BrowserWorkspace与Bridge可靠性工作指令.md`
- `Docs/07架构/72Phase2A-1最终验收修复Bridge握手Surface切换与UISmoke工作指令.md`
- `Docs/07架构/73Phase2A-1验收证据收口与Phase2A-2准入工作指令.md`
- `Docs/07架构/74Phase2A-2最小RemoteBrowser与AgentTools实施验收报告.md`
- `Docs/07架构/75Phase2A-3SnapshotLocatorInteractWait开发工作指令.md`
- `Docs/07架构/76Phase2A-3通用WebView2页面操作实施验收报告.md`
- `Docs/07架构/77Phase2A-3B真实DeepSeekAgent浏览器工具选择验收工作指令.md`
- `Docs/07架构/78Phase2A-3B外部验收控制器与脱敏BrowserActivity证据开发工作指令.md`
- `Docs/07架构/79Phase2A-3C真实Agent会话WebView2控制闭环开发工作指令.md`

### 2. 智能体、运行时与协作

- `Docs/02智能体与智能体运行时/`
- `Docs/03多智能体/`
- `Docs/04工具与技能/`
- `Docs/07架构/`

### 2.1 上下文、缓存与输入压缩

- `Docs/07架构/18上下文缓存可观测性ADR.md`
- `Docs/07架构/43ADR-042上下文自动压缩与主动Compact命令ADR.md`
- `Docs/07架构/44ADR-043缓存统计闭环ADR.md`
- `Docs/Features/上下文自动压缩与Compact命令设计方案.md`
- `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md`

该主题用于跟踪 token 成本治理、服务商前缀缓存命中、工具输出/日志/文件/RAG 块进入 LLM 前的压缩策略，以及 Headroom 作为参考项目或可选适配器的评估结果。

### 3. 历史任务与设计演进记录

- `Docs/Tasks/task04-swarm.md` 到 `Docs/Tasks/task18-positioning.md`
- `Docs/Tasks/task19-coding-agent-blueprint.md`
- `Docs/Tasks/task20-cli-ui-ux.md`
- `Docs/Tasks/task21-subconscious-dual-llm.md`
- `Docs/Tasks/task22-agent-roles-orchestration.md`
- `Docs/Tasks/task23-central-lock-coordination.md`

这些文档仍然有价值，但需要放在新的 Platform / Runtime / Workspace 治理主线下理解，不能再单独代表产品总方向。

## 当前架构基线

- V1 目标：`PuddingDesktop.exe` 双击启动并监督独立 ASP.NET Core 子进程
- WPF 提供 Windows 11 Shell、Workbench WebView2、Agent Browser、运行中心和存储管理
- Core 继续承载 Web UI 静态资源、Controller、Runtime、Connector 与 SQLite
- Desktop 托管的 Core 使用 `system.json` 可配置固定端口（默认 8080）绑定 `0.0.0.0`；Desktop 控制流固定走同端口 `127.0.0.1`
- 支持 LLM 多轮对话（带工具调用）
- 支持 P2P 节点发现与直连通信（mDNS + HTTP/gRPC）
- Console Host 只作为开发和诊断入口
- 任务管理已迁移至 Todo API（`python .github/skills/todo-api/todo_api.py`）

## 当前实现状态说明

- ADR-077 当前是基于现有多模态代码骨架形成的目标设计，不表示 `deepseek-v4-flash-vision-exp` 已通过当前轮、多轮、重启和 Files API 的真实模型验收。
- ADR-074 及 Task-bound Goal 代码级施工计划当前只是设计定稿，不是实现或生产验收证据；现有 `goal_queue.json`/`GoalModeService` 不等价于持久 GoalRun。
- Phase 1A Desktop Launcher、Phase 1B-R Runtime Center、Phase 1B-S Storage、Phase 2A-1/2 和 Phase 2A-3 确定性实现已于 2026-08-02 验收。Phase 2A-3 已交付 Snapshot、Locator、八项 Interact、Wait、版本化 ref、四项新 Agent Tools、真实 WebView2 TestSite、Release publish 与可见 Desktop 退出 smoke；结果见 76。真实 DeepSeek Agent 的工具选择 smoke 仍需用户明确选择测试 Agent/DataRoot，完成前不进入 Douyin Adapter。`dev-up.py` 保留为源码开发脚本，不进入最终产品。
- Phase 2A-3B 的真实 DeepSeek 工具选择验收按 77 执行；通过前不得开始 Douyin Adapter 实现。
- 当前源码中仍保留旧架构和开发脚本入口，阅读 Desktop 主线时以 68、69 实施规格为准。
- 任务状态通过 Todo API 管理，不在文档或代码中硬编码。
