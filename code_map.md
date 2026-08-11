# PuddingAgent CodeMAP

> 顶层快速索引 | 2026-08-10 | 29 项目 | .NET 10 / WPF / React / SQLite / WebView2

## 项目定位

Pudding — Windows 桌面智能助手。ASP.NET Core 是 Desktop 子进程，Console 仅开发入口。详见 `Agents.md`。

## 架构文档

| 文档 | 主题 |
|------|------|
| `Docs/07架构/67ADR-066*.md` | Browser 能力与 Douyin 分层决策 |
| `Docs/07架构/68*.md` | WebView2 自动化分阶段实施规格 |
| `Docs/07架构/69*.md` | Desktop 浏览器工作区/运行中心/存储 |
| `Docs/07架构/70–73*.md` | Phase 2A-1 Bridge 与双标签工作区（✅） |
| `Docs/07架构/74*.md` | Phase 2A-2 Remote Browser + Agent Tools（✅） |
| `Docs/07架构/75–76*.md` | Phase 2A-3 Snapshot/Locator/Interact/Wait（✅） |
| `Docs/07架构/77–79*.md` | Phase 2A-3B/C DeepSeek 验收与闭环 |
| `Docs/07架构/80ADR-069*.md` | MOA 子代理设计委员会编排核心；Phase 1–3 计划编译、纯状态机与运行时适配 |
| `Docs/07架构/81ADR-070*.md` | 通用 Agent 编排图；V2 组件/多模态端口、SQLite 事实、Graph/Run 发现、Revision/Layout 双 CAS、replay-to-live SSE，以及 React Flow 节点/端口/Edge/Graph Input 编辑器 |
| `Docs/07架构/82ADR-071*.md` | 通用 Agent 编排平台完整目标设计；JSON 图、Revision/Layout/Deployment/Run 事实边界、组件、多模态、Agent 工具与 MOA 统一 |
| `Docs/07架构/83*.md` | 后端执行内核与 Control Plane 施工图；契约、SQLite、API、状态转换、Executor、Scheduler、Trigger 与权限 |
| `Docs/07架构/84*.md` | Admin 蓝图编辑器和组件系统施工图；Node/Edge/Input/Trigger、Revision/Deployment/Run、多模态 UX 与文件级拆分 |
| `Docs/07架构/85*.md` | 分期交付、测试、安全、性能、Desktop 部署、浏览器 smoke、恢复与验收证据图册 |
| `Docs/07架构/tool-infrastructure-layering.md` | Tool 分层、强制委派合同、Smart 参数与结果合同 |
| `Docs/QA/QA-2026-08-03*.md` | Qwen 输入上限修复验收 |
| `Agents.md` | 仓库级开发约束 |
| `dev-up.py` | 本地开发监督器 |
| `How-Debuge.md` | 诊断路径 |

## 顶层目录

| 项目 | 说明 | 详细索引 |
|------|------|----------|
| `Source/PuddingAgent/` | 🔑 入口 (Program.cs) | — |
| `Source/PuddingRuntime/` | 🔑 Agent Loop · LLM · 工具 · 上下文管线 | [code_map](Source/PuddingRuntime/code_map.md) |
| `Source/PuddingDesktop/` | 🔑 WPF Launcher · 固定端口 Core 子进程 · Browser 工作区 · 客户端精灵源素材 | [code_map](Source/PuddingDesktop/code_map.md) |
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
| `Source/PuddingPlatformAdmin/` | React 管理前端 · Chat 虚拟视口/渐进消息/状态缓存 · Agent 编排布局编辑器 · 管理壳异步隔离 · 已移除 Phaser/2D Studio | [code_map](Source/PuddingPlatformAdmin/code_map.md) |

## 调用链路

```
Agent Loop → search_tools → Browser Tools (PuddingBrowser.AgentTools)
  → IBrowserRuntime → RemoteBrowserRuntime (Host/BrowserBridge/)
    → WebSocket → DesktopBrowserBridgeClient (Desktop/Browser/)
      → WebView2 (PuddingBrowser.WebView2)

Agent Loop → LlmInvocationService → DirectLlmClient
  → model.protocol=openai → OpenAiLlmGateway (/chat/completions)
  → model.protocol=responses → ResponsesLlmGateway (/responses)
  → model.protocol=anthropic → AnthropicMessagesLlmGateway (/messages)
  → Provider 不保存协议；同一 Provider 的模型可分别选择三种协议
  → Provider usage → ILlmGatewayUsageRecorder → llm_gateway_usage_events
    → StatsApiController（月度/趋势本地计费口径）
    → TokenUsageEvents 继续只承担会话/角色/上下文归因

ContextPipeline → Tool layer mandatory delegation policy
  → 首次工具调用前必须判定 Direct / Delegated
  → 复杂任务前三次工具调用内必须进入匹配 smart_* 或 spawn_sub_agent
  → SmartWorkflowToolBase 将历史 question/what/query 仅在执行边界归一为 task
  → smart_explore 统一替代已退役的 smart_search / smart_query_session_log

spawn_sub_agent → SubAgentInvocationService → SubAgentManager
  → `runtime.execution.json` 统一配置 600 轮 / 2400 次工具调用 / 24h
  → 父 Agent 工具 schema 不暴露轮次、工具调用或 timeout 预算字段
  → AgentExecutionService 在启动、剩余 80%/50% 与预算耗尽时注入预算通知
  → 正常轮次/时间耗尽后提供 20 轮、最多 30 分钟的收尾宽限，终态为可续跑 `budget_exhausted`
  → `resume_sub_agent_id` 复用 SubSessionId/上下文、创建新 runId 并重置系统计数器
  → run archive 固化实际预算与 `subagent.budget.notice`

Runtime 跨层服务 → Core contracts → Platform implementations
  → SubAgentTool → ISubAgentPool → SubAgentPool
  → AgentDiagnosticsTool → ITokenUsageEventRepository → TokenUsageEventRepository
  → FileReadTool/FilePatchTool → Runtime-owned FileChunkService

PuddingHost 产品组合根 → Runtime tool assembly scan
  → 每个自动发现的 IPuddingTool 都参与 ValidateOnBuild
  → 新工具的构造依赖必须同步注册到 PuddingHost 的 Runtime 扩展
  → PuddingApplicationHostCompositionTests 用 DesktopChild 入口防止“构建成功、Core 启动即退出”

Agent 实例 `manifest.json.imageReaderModel` → ImageReaderTool
  → ILlmResolver 显式 `provider/model` + `vision` 能力校验
  → 专用模型失败时仅尝试同一 manifest 的 Agent 主模型（也必须具备 `vision`）
  → 配置缺失或两条显式路由均失败时返回错误，不按全局 vision 模型排序兜底
  → 文本主模型附件的 VisualArtifactObservationService 也使用同一显式字段

Desktop Storage → CoreStorageManagementClient
  → GET/POST /api/admin/storage/databases（Admin JWT 或 Loopback ControlToken）
  → StorageMaintenanceService
    → 平台库页面/行/重复索引 + 代码索引作用域明细
    → PreviewId（10 分钟）→ 白名单批量删除 → checkpoint/VACUUM → 重扫
    → session_event_log / conversation_events / ChatMessages / memory 永不进入清理目标

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
  → MessageList → messageProjection（保持已组装消息顺序，未匹配 active run 留在当前流末端）→ MessageViewportRuntime（虚拟化、锚点、贴底）
  → ChatMessageStyleProvider（消息树共享一次聚合样式注册）
  → MessageRow（稳定块直接渲染 + 语义 memo；不再经过单条 MessageStream 兼容重建）
  → 主消息运行监视区（主代理当前阶段 + 最近推理摘要 + 有界子代理委派状态；不展开子代理内部过程）
  → subAgentReducer（事件/快照统一投影；budget_exhausted 终态单调；原样展示有界的实际 reasoning_preview）
  → SubAgentActivityDock（子代理任务/工具/轮次/输出详情；Agent-first 路由回退 mainSessionId 保证图标可见；预算耗尽显示为可恢复终态）
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
| `Tests/PuddingDesktop.Tests/` | Desktop 进程/配置、Browser Controller/Client（135/135 ✅，Release 2026-08-09） |
| `Tests/PuddingHost.Tests/` | Bridge Endpoint/Remote proxy（56/56 ✅） |
| `Tests/PuddingBrowser.AgentTools.Tests/` | 七项 Agent Tools（10/10 ✅） |
