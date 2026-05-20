# Pudding Agent — 任务看板

最后更新：2026-05-20（ADR-026 ADR-025 验收阻塞修复与执行闭环方案）

## 项目定位

Pudding Agent 是一个单进程、零外部依赖、支持 P2P 组网的 AI 代理程序。用户下载一个可执行文件，双击运行，浏览器自动打开即可使用。

## V1 目标

一个可运行的 Pudding Agent：
- 单进程，内嵌 Web UI + Controller + Runtime + SQLite
- 双击启动，浏览器打开即用
- 能进行 LLM 对话（多轮，带工具调用）
- 两个 Agent 进程可通过 P2P 互相发现与通信
- 支持裸进程运行或 Docker 单容器部署

## V1 任务管理

所有 V1 任务通过 Todo API 管理。以下为任务看板概览，详细状态以 API 为准。

```bash
python .github/skills/todo-api/todo_api.py kanban --group-by stage --project Pudding
```

## 架构增强剩余任务（2026-05-18）

行动指南：[19架构基础设施增强下一步ADR](07架构/19架构基础设施增强下一步ADR.md)

### P0：配置与目录基础设施

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-CONFIG-001 | 全仓库移除 `.env` / LLM 环境变量作为配置来源 | 代码扫描清单、替换方案、兼容期策略 |
| ARCH-CONFIG-002 | 统一 `data/config/*.json` 配置加载入口 | `system.json`、`llm.providers.json`、`security.json`、`connectors.json` 的 schema 校验与错误报告 |
| ARCH-CONFIG-003 | 支持多 LLM 服务商、多模型、多 profile | provider/model/profile/role 解析链路，覆盖显意识 LLM 与潜意识 LLM |
| ARCH-CONFIG-004 | Agent 专属文件配置目录落地 | `data/agents/{agentInstanceId}/config/*.json|yaml` 与 `soul.md`、`persona.md`、`tools.md` 文件约定 |
| ARCH-CONFIG-005 | Agent 模板目录落地 | `data/agent-templates/{templateId}/manifest.json` 与 Markdown 行为文件 |
| ARCH-CONFIG-006 | 旧配置迁移工具 | 从 `data/conf/*`、`data/llm/*`、数据库 Agent 配置迁移到新目录 |
| ARCH-CONFIG-007 | 配置管理 API 与 Admin UI | 可查看、校验、编辑系统配置、LLM 服务商、模型、Agent 配置 |

### P0：事件系统

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-EVENT-001 | 事件 envelope 标准化 | 统一 `eventId`、`traceId`、`correlationId`、`causationId`、`source`、`timestamp` |
| ARCH-EVENT-002 | 事件 schema 注册与版本管理 | 事件类型目录、schema 校验、兼容性规则 |
| ARCH-EVENT-003 | 持久化事件队列 | `data/runtime/events` 存储、ack、retry、dead-letter |
| ARCH-EVENT-004 | 事件回放与诊断 | 按 session/agent/trace 回放事件流 |
| ARCH-EVENT-005 | 事件系统可观测性 UI | 事件时间线、失败事件、重试次数、消费者状态 |

### P0：LLM 执行引擎

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-EXEC-001 | 统一 LLM Gateway 协议边界 | Runtime 只依赖统一 gateway，不散落 provider 协议细节 |
| ARCH-EXEC-002 | 显意识/潜意识 LLM profile 路由 | 每个 Agent 可独立选择 conscious/subconscious profile |
| ARCH-EXEC-003 | 执行生命周期状态机 | queued、assembling_context、calling_llm、tool_calling、completed、failed、cancelled |
| ARCH-EXEC-004 | 超时、取消、重试、熔断策略 | provider/model 级别策略配置与执行记录 |
| ARCH-EXEC-005 | Tool call 审计链路 | tool 请求、参数、审批、输出、错误、耗时统一记录 |
| ARCH-EXEC-006 | 本地 Fake LLM 测试基座稳定化 | OpenAI 兼容非流式/流式/工具调用响应，供 E2E 和开发默认使用 |

### P0：会话层

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-SESSION-001 | Session State Machine 明确化 | 会话、消息、流式帧、工具调用、子代理状态转换表 |
| ARCH-SESSION-002 | SQLite + JSONL 双写会话日志 | `data/logs/sessions/{sessionId}.jsonl` 与数据库一致性策略 |
| ARCH-SESSION-003 | 会话恢复与重放 | 从 JSONL 恢复 UI 状态、执行状态、调试上下文 |
| ARCH-SESSION-004 | 会话级 trace 聚合 | 同一 session 下 LLM、工具、事件、子代理、记忆调用统一关联 |
| ARCH-SESSION-005 | 会话诊断页面 | 状态机视图、事件视图、执行耗时、错误链路 |

### P0：子代理系统

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-SUBAGENT-001 | 子代理 workspace 隔离规范 | `data/workspaces/{workspaceId}/agents/{agentInstanceId}` 目录与权限边界 |
| ARCH-SUBAGENT-002 | 子代理实例配置文件化 | 不再只依赖数据库，配置可读、可 diff、可备份 |
| ARCH-SUBAGENT-003 | 子代理生命周期管理 | spawn、running、waiting、completed、failed、cancelled 状态与事件 |
| ARCH-SUBAGENT-004 | 子代理结果归档 | 输入、输出、工具调用、文件变更、LLM profile 记录 |
| ARCH-SUBAGENT-005 | 子代理可观测性 UI | 主代理与子代理调用树、时间线、结果摘要 |

### P1：记忆图书馆与潜意识 LLM

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-MEM-001 | 记忆图书馆目录规范 | `data/memory/books`、`data/memory/graphs`、索引与备份目录 |
| ARCH-MEM-002 | 潜意识 LLM 执行日志 | 抽取、检索、总结、写入的输入输出与耗时 |
| ARCH-MEM-003 | 记忆写入事件化 | 记忆抽取、候选、确认、落库全部进入事件系统 |
| ARCH-MEM-004 | 记忆诊断 UI | 命中来源、相似度、上下文注入位置、失败原因 |

### P1：网关与连接器

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-GATEWAY-001 | 连接器配置文件化 | HTTP/WebSocket/MQTT/Webhook 连接器配置进入 `data/config/connectors.json` |
| ARCH-GATEWAY-002 | 连接器消息事件化 | 入站、出站、失败、重试消息统一进入事件系统 |
| ARCH-GATEWAY-003 | 连接器诊断增强 | 当前连接数、吞吐、错误、最后消息、重放入口 |
| ARCH-GATEWAY-004 | 网关安全边界 | token、签名、来源限制、速率限制配置化 |

### P1：可观测性

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-OBS-001 | 全局 trace/correlation 贯穿 | HTTP、Session、Runtime、Event、Tool、SubAgent、Memory 全链路 |
| ARCH-OBS-002 | 结构化日志目录规划 | `data/logs/system`、`diagnostics`、`sessions`、`agents`、`connectors` |
| ARCH-OBS-003 | Runtime activity 持久化 | 活动记录可查询、可过滤、可关联 session/agent |
| ARCH-OBS-004 | Admin 诊断驾驶舱 | 组件状态、执行顺序、耗时、失败点、最近事件 |
| ARCH-OBS-005 | 日志脱敏与密钥保护 | API Key、token、用户敏感内容脱敏策略 |

### P1：端到端测试与调试模式

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-E2E-001 | 修复/稳定 Web API 测试发现与运行 | `PuddingWebApiTests` 可稳定 list/run tests |
| ARCH-E2E-002 | 外部 E2E 测试框架 | Playwright 或 Python 浏览器自动化脚本，覆盖登录、建会话、发消息、流式响应 |
| ARCH-E2E-003 | Docker E2E 测试 | `build-and-up.ps1` 后自动健康检查与核心路径验证 |
| ARCH-E2E-004 | 前端调试模式 | URL flag 或配置开关开启 debug panel、mock action、trace overlay |
| ARCH-E2E-005 | 前端自动化测试钩子 | 稳定 test id、debug API、状态快照导出 |
| ARCH-E2E-006 | E2E 测试数据种子 | 默认 workspace、agent、LLM fake provider、session seed |
| ARCH-E2E-007 | CI 测试分层 | unit、integration、web api、browser e2e 分层执行 |

### ADR-023 可观测性闭环与E2E基线（2026-05-20）

| 任务 ID | 标题 | 状态 |
|--------|------|------|
| ARCH-OBS-001 | 诊断 DTO + Timeline 聚合后端 | ✅ done |
| ARCH-OBS-002 | Admin Diagnostics 第一版 UI | ✅ done |
| ARCH-OBS-003 | 前端 Debug Mode | ✅ done |
| ARCH-OBS-004 | Playwright E2E 基线 | ✅ done |
| ARCH-OBS-005 | Docker Smoke + QA 收口 | ✅ done |

### ADR-024 核心架构组件边界与执行引擎拆分（2026-05-20）

行动指南：[24核心架构组件边界与执行引擎拆分ADR](07架构/24核心架构组件边界与执行引擎拆分ADR.md)
实施计划：[2026-05-20-core-architecture-boundaries-refactor-plan](superpowers/plans/2026-05-20-core-architecture-boundaries-refactor-plan.md)
QA：[QA-2026-05-20-Core-Architecture-Boundaries](../QA/QA-2026-05-20-Core-Architecture-Boundaries.md)

| 任务 ID | 标题 | 状态 |
|--------|------|------|
| ARCH-CORE-001 | Core Runtime Contracts (6 接口 + DTO) | ✅ done |
| ARCH-CORE-002 | Lifecycle Recorder 适配 RuntimeActivity | ✅ done |
| ARCH-CORE-003 | Context Assembly Facade | ✅ done |
| ARCH-CORE-004 | LLM Invocation Facade (非流式路径迁移) | ✅ done |
| ARCH-CORE-005 | Tool Invocation Facade | ✅ done |
| ARCH-CORE-006 | Sub-Agent Invocation Facade | ✅ done |
| ARCH-CORE-007 | Session Output Writer | ✅ done |
| ARCH-CORE-008 | AgentExecutionService 瘦身 | ✅ done |
| ARCH-CORE-009 | ADR-023 Timeline Metadata 兼容 | ✅ done |
| ARCH-CORE-010 | QA 与文档收口 | ✅ done |

### ADR-026 ADR-025 验收阻塞修复与执行闭环（2026-05-20）

行动指南：[26ADR-025验收阻塞修复与执行闭环方案](07架构/26ADR-025验收阻塞修复与执行闭环方案.md)
QA：[QA-2026-05-20-ADR-026-Closure](../QA/QA-2026-05-20-ADR-026-Closure.md)

| 优先级 | 任务 ID | 标题 | 状态 |
|--------|---------|------|------|
| P0 | ADR26-001 | 修复 diagnostics 前端 TS 契约错误 | ✅ done |
| P0 | ADR26-002 | 增加诊断 DTO 序列化 contract tests | ✅ done |
| P0 | ADR26-003 | Streaming LLM 经 `ILlmInvocationService.InvokeStreamAsync` | ✅ done |
| P0 | ADR26-004 | Tool 调用经 `IToolInvocationService.InvokeAsync` | ✅ done |
| P0 | ADR26-005 | Sub-Agent 调用经 `ISubAgentInvocationService.InvokeAsync` | ✅ done (DI registered; direct calls not in AgentExecutionService) |
| P0 | ADR26-006 | Session output 经 `ISessionOutputWriter.WriteFrameAsync` | ✅ done |
| P0 | ADR26-007 | LLM provider/profile/model/role 真实解析 | ✅ done (`ILlmProfileResolver` implemented) |
| P1 | ADR26-008 | Debug API 写入 sessionId/traceId/messageId | ✅ done |
| P1 | ADR26-009 | Playwright evidence test 断言后端证据链 | ✅ done |
| P1 | ADR26-010 | Docker smoke 增加 evidence API 检查 | ⏳ deferred (需运行中服务) |
| P1 | ADR26-011 | QA 报告与 ADR 状态收口 | ✅ done |

### P2：运维与文档

| 任务 ID | 标题 | 交付物 |
|--------|------|--------|
| ARCH-OPS-001 | `data` 目录备份/还原工具 | 一键备份、恢复、清理临时数据 |
| ARCH-OPS-002 | 配置示例与注释文档 | 多 provider、多模型、多 Agent 示例 |
| ARCH-OPS-003 | 本地开发 Runbook | 裸进程、Docker、Fake LLM、真实 provider 切换说明 |
| ARCH-OPS-004 | 架构决策索引 | ADR、QA、spec、plan 与任务 ID 的映射 |

### P0/P1：架构基础设施硬化（ADR-022）

行动指南：[22架构基础设施硬化与行动路线ADR](07架构/22架构基础设施硬化与行动路线ADR.md)
实施计划：[2026-05-19-architecture-foundation-hardening-roadmap](superpowers/plans/2026-05-19-architecture-foundation-hardening-roadmap.md)

| 优先级 | 任务 ID | 标题 | 交付物 |
|--------|---------|------|--------|
| P0 | ARCH-HARDEN-001 | JSON/JSONL 序列化契约硬化 | `PuddingJsonContracts`、JSONL 单行测试、run archive 往返测试 |
| P0 | ARCH-HARDEN-002 | 子代理 run terminal 单写入者 | `CompleteRunAsync` 幂等结果、terminal 不覆盖、Manager/Execution 职责分离 |
| P0 | ARCH-HARDEN-003 | EventSchema scope 化 | `EventSchemaScope`、Internal/SessionFrame 分层、重复 key 测试 |
| P1 | ARCH-HARDEN-004 | 诊断 API DTO 稳定化 | SubAgentRun/Event diagnostics DTO、分页校验、脱敏摘要 |
| P1 | ARCH-HARDEN-005 | Workspace Guard 权限执行接入 | `IAgentWorkspaceGuard`、FileTool/ShellTool/SubAgentTool 权限拦截 |
| P1 | ARCH-HARDEN-006 | Trace report token usage 兼容解析 | 多命名风格 usage parser、trace-report token 聚合测试 |
| P1 | ARCH-HARDEN-007 | E2E 基线准备 | WebApiTests 输出隔离、Docker healthcheck、浏览器 smoke |
| P2 | ARCH-HARDEN-008 | QA 与文档收口 | QA 报告、Tasks 状态更新、残余风险清单 |

### V1 任务 一览（已全部完成）

| 优先级 | 层级 | 任务卡 ID | 标题 | 状态 |
|--------|------|-----------|------|------|
| P0 | L0 基础 | task-20260502-008 | 实现多轮会话与工具调用框架 | ✅ done |
| P0 | L0 基础 | task-20260502-007 | 实现 SQLite 数据持久化层 | ✅ done |
| P0 | L0 基础 | task-20260502-012 | AI 服务商管理功能迁移 | ✅ done |
| P0 | L1 记忆 | task-20260502-023 | 实现长期记忆引擎 | ✅ done |
| P0 | L2 工具 | task-20260502-024 | 实现工具与Skill系统 | ✅ done |
| P0 | 安全 | task-20260502-018~021 | KeyVault 密钥保管箱 | ✅ done |
| P1 | — | task-20260502-005 | 实现 P2P 节点发现与直连通信 | ✅ done |
| P1 | L3 子代理 | task-20260502-025 | 实现子代理与Orchestrator-Workers | ✅ done |
| P1 | L4 连接器 | task-20260502-026/017 | 连接器框架 + Webhook | ✅ done |
| P2 | L5 自动化 | task-20260502-027 | Cron 定时任务调度 | ✅ done |
| P2 | L5 工作流 | task-20260502-028 | 工作流管道 CRUD | ✅ done |
| P3 | 远期 | task-20260502-029 | 浏览器自动化 | 📋 远期 |

### 下一批任务（2026-05-03 新建）

| 优先级 | 任务卡 ID | 标题 | 阶段 | 依赖 |
|--------|-----------|------|------|------|
| P0 | task-20260503-002 | QA 独立审阅 — 新增代码 | ready | — |
| P0 | task-20260503-010 | 端到端部署验证 | verifying | — |
| P1 | task-20260503-003 | Hook 系统实现 — PreToolUse/PostToolUse | ready | task-002 |
| P1 | task-20260503-005 | 会话持久化 — SQLite+JSONL双写 | ready | — |
| P1 | task-20260503-009 | MicroCompact 上下文压缩 | ready | — |
| P1 | task-20260503-013 | Cron/Webhook API 端点补充 | ready | — |
| P1 | task-20260503-011 | Cron 管理页面 | ready | task-013 |
| P1 | task-20260503-012 | Webhook 管理页面 | ready | task-013 |

### UI 任务（PuddingPlatformAdmin）

| 优先级 | 任务卡 ID | 标题 | 状态 | QA |
|--------|-----------|------|------|-----|
| **P1** | task-ui-01-chat-core | Chat 核心体验 — 时间戳/重试/骨架屏/欢迎引导 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-ChatCoreUpgrade.md) |
| **P1** | task-ui-02-chat-interaction | Chat 交互增强 — 全栈 SSE 流式改造 + 消息操作 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-ChatStreamingUpgrade.md) |
| **P2** | task-ui-03-visual-system | 视觉系统 — 主题/响应式/动效/无障碍 | ✅ done | [PASS_WITH_NOTES](QA/QA-2026-05-03-VisualSystemUpgrade.md) |
| **P2** | task-ui-04-management-details | 管理详情页 — 仪表盘/场景/Agent 详情 | 🔲 ready | — |

## 技术选型

| 组件 | 技术 |
|---|---|
| 运行时 | .NET (ASP.NET Core, 单文件发布) |
| 数据库 | SQLite (EF Core) |
| 前端 | React + TypeScript, 内嵌 |
| LLM | 直接调用 OpenAI 兼容 API |
| P2P | mDNS + HTTP/gRPC |

## 不再需要的旧任务

以下旧任务因架构简化为单进程模型而废弃：

- ~~task24-platform-v1-first-slice~~ — 多服务分布式架构设计
- ~~task26-runtime-foundation~~ — 独立 Runtime 进程
- ~~task27-controller-routing-session~~ — 独立 Controller 进程
- ~~task28-platform-workspace-governance~~ — Platform 治理层
- ~~task29-agent-template-and-audit~~ — 审计 Agent 模板
- ~~task30-knowledge-infrastructure~~ — 知识库/图谱/存储
- ~~task31-client-surfaces~~ — 多客户端（CLI/Avalonia）
- ~~task32-observability-integration~~ — 多服务可观测性
- ~~task33-embedded-runtime-host~~ — 嵌入式 Runtime
- ~~task34-event-bus-and-subscription~~ — RabbitMQ 事件总线
- ~~task35-workspace-cockpit~~ — 协作驾驶舱

## Phase 5：上下文缓存可观测性（ADR-018，2026-05-18 归档）

| 优先级 | 任务卡 ID | 标题 | 状态 | 关联 ADR |
|--------|-----------|------|------|----------|
| P0 | T-CACHE-001 | TokenUsageDto 新增 PromptCacheHitTokens / PromptCacheMissTokens | ✅ done | ADR-018-B |
| P0 | T-CACHE-002 | DirectLlmClient.ChatAsync() 解析 cache tokens | ✅ done | ADR-018-A |
| P0 | T-CACHE-003 | LlmModelEntity 新增 CacheHitPricePer1MTokens + EF 迁移 | ✅ done | ADR-018-C |
| P0 | T-CACHE-004 | 新增 TokenUsageStatsEntity + EF 迁移 | ✅ done | ADR-018-F |
| P0 | T-CACHE-005 | ChatApiController fire-and-forget 增量更新统计表 | ✅ done | ADR-018-A |
| P0 | T-CACHE-006 | 管理后台模型表单新增缓存价格字段 | ✅ done | ADR-018-C |
| P1 | T-CACHE-007 | StatusBarTokenIndicator 双层圆环改造 | ✅ done | ADR-018-D |
| P1 | T-CACHE-008 | useChatState 新增 mainSessionId + 缓存统计累加 | ✅ done | ADR-018-E |
| P1 | T-CACHE-009 | TokenStatsIndicator 数据接入 | ✅ done | ADR-018-D |
| P1 | T-CACHE-010 | ChatPage 集成主会话逻辑 | ✅ done | ADR-018-E |
| P1 | T-CACHE-011 | GET /api/sessions/{id}/token-stats API | ✅ done | ADR-018-D |
| P2 | T-CACHE-012 | StatsApiController 新增 GET /api/stats/tokens/monthly | ✅ done | ADR-018-F |
| P2 | T-CACHE-013 | Admin Token 统计页面 (pages/stats/tokens/) | ✅ done | ADR-018-F |
| P2 | T-CACHE-014 | 路由配置 /stats/tokens | ✅ done | ADR-018-F |
