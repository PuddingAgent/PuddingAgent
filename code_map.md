# PuddingAgent CodeMAP

> 顶层快速索引 | 2026-08-24 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `README.md` / `README_zh-CN.md` | 中英文产品与目标架构入口；Windows Desktop/Core 产品边界、Plugin/Function/Hook/Event/Projection 五类合同、Agent FSM、函数图编排、前端思想、现状缺口与路线 |
| `Docs/Features/工作区TODO与峰谷节能任务编排设计方案.md` | 工作区 TODO 台账、Agent 认领/拒绝/回报、durable 自动派发与定时消息、可信 idle、心跳 0、峰谷 WorkAdmissionFence，以及 Hook 触发的临时质询子代理、GoalRun 有界循环、manifest/Admin 模型路由、防无限循环熔断和公共 Plugin/Function/Event/Projection 映射 |
| `Docs/Features/Goal持久目标自主续行与自动压缩完整设计方案.md` | `/goal` 完整专项设计；统一 Web/Desktop/Connector 命令、持久 GoalRun、事件驱动 continuation、256 个外层 Goal Iteration、证据 Verifier、用户抢占、重启停用、自动压缩和 Task-bound Goal；明确不依赖 Heartbeat |
| `Docs/Features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md` | 低峰自动执行施工图；冻结 Goal 前置、持久 Agent Availability Sensor、provider/model 价格时段 Resolver、TaskGoalDispatchCoordinator、原子 Task/Goal 绑定、文件矩阵、测试、切换与生产门禁 |
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
| `Docs/07架构/89ADR-074*.md` | Goal 专项架构决策；冻结外层 GoalRun/内层 Agent Loop 双层预算、单 Goal 256 accepted Iteration 硬上限、durable outbox 续行、独立证据验证、重启 disarm、Task-bound Goal、Availability Sensor、低峰派发与不依赖 Heartbeat |
| `Docs/07架构/90ADR-075*.md` / `Docs/Features/第三方任务看板AccessToken与外部API详细设计方案.md` | 第三方任务看板开发合同；冻结 hashed opaque Access Token、ASP.NET Core 独立 scheme + scope/workspace Policy、外部 API v1、ETag/幂等、追加式 TaskEvaluation 与 Admin Access Token 管理器；P1（Token 后端）+ P3（Admin UI）+ P2 基本功能已实现：`pdt_v1_` opaque Token 摘要存储、PuddingExternalAccessToken scheme、Admin 管理 API/UI、last-used 合并写、External Task API v1（list/get/create/patch/comments/evaluations/commands + ETag/428/412 + 简化幂等）共 65 项后端测试；SSE Watch/RateLimiter/OpenAPI 与 P4（部署收口）未实现，External API 默认关闭 |
| `Docs/07架构/91ADR-076*.md` / `Docs/Features/遥测调试数据自动过期与Web存储管理设计方案.md` | 遥测/Debug 存储治理目标设计；Core + Web `/storage`、缓存快照与有界增量估算、分类图表/趋势报表、语义类型目录、自动/人工唯一维护 writer、按类型/时间清理、关键事实保护、长期聚合和禁止在线全库 VACUUM；Desktop 不扩展，当前未实现；2026-08-24 代码级现状核对补充：上下文日聚合复用既有 `context_layer_daily_rollups`、retention 索引收编目录所有权（终止 `ix_prune_*` 运行时补建双轨）、`CompactAfterCleanup` 默认值随 Phase 1 翻转、旧 /databases 端点与 Desktop 旧页面捆绑退役、appsettings `Retention` 节整体迁移 system.json |
| `Docs/07架构/92ADR-077*.md` | 主代理原生视觉目标设计；typed image content、Workspace Artifact、DeepSeek Responses `input_image`/图片型工具结果、Files API、多轮/重启恢复、fail-closed；Image Reader 重定位为 URL/任意绝对路径/Artifact 取用工具，默认当前模型原生读取，文本模型或第二意见才委派 helper；V0–V2 已实现（typed parts、fail-closed Planner、图片工具结果、visionHelperModel、删除自动预观察），V3 Files API 与 V4 真实模型 smoke 待做 |
| `Docs/07架构/tool-infrastructure-layering.md` | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| `Docs/deepseek-harness-message-card-alignment-2026-08-14.md` | 对照 deepseek-harness 的消息、推理和工具调用 UI 目标架构；定义 TurnStatus、Reasoning/Tool/Delegation 行、toolCallId 投影、分期与验收矩阵 |
| `Docs/chat-ui-behavior-chain-quality-upgrade-2026-08-23.md` | 聊天前端「行为链 + 质感」升级：harness 质感纪律与 Hermes/业界 12 原则调研、四档灰阶 token、交错时间线（路径 A/B 统一 ViewModel）、五类 presentation renderer 设计与实施记录 |
| `Docs/deepseek-harness-tool-system-alignment-2026-08-14.md` | 对照 deepseek-harness 的工具定义与执行协议；规划 canonical output、端到端 callId、结构化错误、管线、并发、spill、可回放 presentation 与 DeepSeek Code Mode |
| `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md` | 对照 deepseek-harness 与 pi 的统一目标架构与 2026-08-15 复评；定义 Plugin/Function/Hook/Event/Projection、Agent Transition+Effect FSM、Function Graph、Composition Snapshot、前端解释层、底座缺口与分期路线 |
| `Docs/Features/上下文Token效率缓存命中与分级压缩优化设计方案.md` | 7 日 Token 构成、工具结果重放、搜索失败和 ZIP 稀疏度基线；定义原文不脱敏的 artifact/envelope、T0-T4 分级压缩、Compact 覆盖门禁、冷启动去重、稳定前缀与 DeepSeek 缓存 >99% 验收 |
| `Docs/Features/服务商余额查询与多服务商计费适配器设计方案.md` | 聊天页主代理余额徽标 + 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器（DeepSeek `/user/balance` 首个落地，`/v1` 剥离修复）+ 前端 `providerBilling.ts` 展示适配器；新服务商扩展步骤、5min 低频轮询/手动刷新、apiKey 不进日志约束 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `dev-up.py` | 本地开发监督器 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs · Console/DesktopChild 薄壳) | [code_map](Source/PuddingAgent/code_map.md) |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · Browser 工作区 · 调试模式（源码前后端 + 80 端口反向代理）· 运行中心前端构建部署按钮 · 客户端精灵源素材 | [code_map](Source/PuddingDesktop/code_map.md) |
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

审批命令防火墙（任务 ce63f8c0，2026-08-22，确定性 Gate 0）
  → ToolApprovalCommandFirewall（静态字符串判定）接入 CheckAsync：工单匹配后、LLM 隐式审计前
  → 危险命令（rm/del/Remove-Item/format/taskkill/force-push/reset --hard，引号外匹配+危险首词）秒拒并引导 request_tool_approval
  → 安全命令（git 常规动词白名单含 -C 解析、构建/测试、只读探查、cd 前缀）秒放，与既有 builtin allowlist 协同
  → 灰区落回 LLM 隐式审计；v2 三层漏斗（feature/auto-approval-v2，62/62 测试、部署就绪）合入后接管灰区
  → 动机：隐式审计裁决 14-39 秒且同参数先拒后过；防火墙判定 0ms 可复现

工具模型倾向适配（2026-08-22，实测子代理调用链驱动）
  → shell 输出去 ANSI：pwsh 注入 $PSStyle.OutputRendering='PlainText' + NO_COLOR=1 + 输出侧正则剥离兜底
  → 探查命令返回值教育：Get-ChildItem/Select-String/Get-Content 等成功输出尾附专用工具提示（94.8% shell 曾是探查类）
  → Codex 补丁格式自动转译：UnifiedDiffParser 识别 *** Begin Patch 并转 unified diff（内容匹配定位，行号占位安全）
  → file_read 护栏窗口 120→400 行（小文件与大文件双路径），减少同文件翻页重读（实测同文件重读 8 次）

ContextPipeline → stable system prefix + volatile User tail
  → 当前消息、日期、召回与 inbound context 不再插入 system prompt
  → AgentExecutionService → ToolResultContextPolicy（模型历史最多 8 KiB；原始完整结果写入工作区 `.pudding/context-tool-results`，不做模型输入脱敏）
  → search_tools 已发现 schema 在 live session 内保持加载，避免跨 dispatch 重复收缩/扩张

用户 Turn → TurnExecutorAdapter → AgentExecutionAdmissionCoordinator（foreground）
  → 抢占同 workspace/agent 的 Message Fabric 后台执行（含 subagent_result）
  → MessageDeliveryDispatcher 取消旧执行并把 exact delivery 立即 defer 回队列
  → foreground demand 存续期间 recovery/idle drain 不领取后台 delivery
  → MessageFabricStore 依据 wake event deliveryId 精确 claim，避免旧队首抢在用户事件前执行

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
  → `runtime.execution.json` 统一配置 600 轮 / 2400 次工具调用 / 24h
  → 父 Agent 工具 schema 不暴露轮次、工具调用或 timeout 预算字段
  → AgentExecutionService 在启动、剩余 80%/50% 与预算耗尽时注入预算通知
  → 正常轮次/时间耗尽后提供 20 轮、最多 30 分钟的收尾宽限，终态为可续跑 `budget_exhausted`
  → `resume_sub_agent_id` 复用 SubSessionId/上下文、创建新 runId 并重置系统计数器
  → run archive 固化实际预算与 `subagent.budget.notice`
  → 子代理轮内软压缩（ADR-060 §3.12）：估算达 0.65×有效输入上限即驱逐最旧会话单元并回写 history，
    压到 0.5×上限；阈值由 runtime.execution.json subAgents 段配置；写 subagent.context.compacted 事件；
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

Desktop → Core Ready 契约（2026-08-24 修复连接器阻塞启动）
  → Core 在全部 hosted service StartAsync 返回后才发 PUDDING_DESKTOP_READY，Desktop 固定 60s 超时
  → ConnectorHostLifecycleService 本地注册保持同步，StartAllAsync 后台执行（ApplicationStopping 绑定）
  → FeishuWebSocket 端点发现/WS 握手各 15s 上限；飞书不可达只 Faulted 单个连接器，不阻塞 Ready

当前视觉链路（ADR-077 V0–V2 已实现，2026-08-23）：typed image content part（`ContentPart{type=image, artifactId, detail}`）
  → ConversationAcceptanceStore 同事务写 `ChatMessages.ContentPartsJson`（v1 信封，Content 为文本拼接投影）
  → ExecutionRunCoordinator 读 canonical parts + 冻结 AgentExecutionSnapshot（CapabilityTags/Protocol/VisionPolicy/VisionHelperRoute）
  → 主模型带 vision：ChatMessage.ContentParts 原生进入请求；文本模型只收 `artifact://` 占位并显式调用 image_reader
  → 已删除 VisualArtifactObservationService 自动预观察旁路（服务+注册+旧测试）
  → LlmVisualInputPlanner fail-closed（缺图/超限抛稳定错误码，不再静默丢图）；inline-only：单图 2MB、聚合 40MiB、8 张上限
  → Responses：user `input_image`（detail original→high）；`function_call_output.output` 支持 [input_text, input_image] 数组
  → ChatCompletions/Anthropic 遇图片工具结果抛 vision_tool_output_not_supported
  → Image Reader（image_reader）：path 唯一必填（http(s) URL / 宿主绝对路径 / artifact://），High 权限 ReadOnly|RequiresNetwork
    → auto 优先 native（ToolExecutionResult.ToolContentParts 图片部件回交调用模型，零辅助 invocation）
    → 文本调用模型或显式 mode=delegate 时用 manifest `visionHelperModel`（原 imageReaderModel 已改名）单次可归因 invocation
  → image_reader source resolver：URL 有界下载（每跳 SSRF/DNS 重校验、禁内网）、本地只读、内容哈希稳定 vision-* Artifact
  → DB 水合经 MessageEntity.AttachmentsJson 恢复图片 part；Snapshot 工厂冻结能力，单一判定来源
  → 待办（V3/V4）：DeepSeek Files API（>2MB 图暂 fail closed vision_request_limit_exceeded）、真实模型 smoke 与进程外验收

Desktop Storage → CoreStorageManagementClient
  → GET/POST /api/admin/storage/databases（Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceService
    → 平台库页面/行/重复索引 + 代码索引作用域明细
    → PreviewId（10 分钟）→ 白名单批量删除 → checkpoint/VACUUM → 重扫
    → session_event_log / conversation_events / ChatMessages / memory 永不进入清理目标

PuddingHost → RetentionPruningService（platform.db 唯一在线保留期任务）
  → `Retention` 顶层配置 → 小批 DELETE + 批间让步 + 单表单轮批数上限
  → conversation_events 先写 `retention-archive/<day>/conversation_events.jsonl` 再删
  → VACUUM 默认关闭；不与第二套诊断裁剪服务并行争用 SQLite writer

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
    补正文事件（运行态即可交错文本段）；懒水合有界化（MessageRow 挂载注册可见
    turn、单批并发 ≤2，替代首屏全量 8 回合 5288 事件）；content-visibility 占位
    改 auto 120px + 虚拟化按内容权重即时开启（不再要求 ≥24 条）；
    AgentTurnCard 大卡片外壳（暖色表面/1px 边界/14px 圆角，终态不重挂载）；
    工具行 aria-label 带状态 + 成功终态卡「已完成」标记
  → MessageList → messageProjection（保持已组装消息顺序，未匹配 active run 留在当前流末端）→ MessageViewportRuntime（虚拟化、锚点、贴底）
  → ChatMessageStyleProvider（消息树共享一次聚合样式注册）
  → MessageRow（稳定块直接渲染 + 语义 memo；不再经过单条 MessageStream 兼容重建）
  → AgentMessageBubble → ExecutionFlowTimeline（行为链交错，2026-08-23；正文分段交错 2026-08-24）
     （per-turn canonical 投影 nodes 按 sequence 交错 文本段/reasoning 段/工具树/委派；
       路径 A processItems adapter 同构回退，featureFlag 默认开、'0' 逃生门；
       正文分段：中间文本段（MessageNode 段）时间线内联、尾段独占正文气泡（段切换 remount 打字机），
       分段并集必须覆盖 answerMarkdown 否则回退整块（防同段双渲染）；
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
  → 子代理检查器、会话诊断 Drawer、摄像头输入仅在首次打开时加载
  → 常驻埋点使用 perfEventRuntime；完整诊断模块仅在 perf/debug 模式加载
```

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
