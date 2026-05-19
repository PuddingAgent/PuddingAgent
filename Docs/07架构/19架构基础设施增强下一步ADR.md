# 19 架构基础设施增强下一步 ADR

> 状态：**in-progress**（Phase 1-4 基础骨架已完成，Git: `21f6057`）
> 日期：2026-05-18 / 最后更新：2026-05-19
> 范围：配置与目录、事件系统、执行引擎、会话层、子代理、可观测性、E2E 测试
> 关联：[18上下文缓存可观测性ADR](18上下文缓存可观测性ADR.md)、[QA-2026-05-18-Architecture-Audit](../QA/QA-2026-05-18-Architecture-Audit.md)、[Tasks](../Tasks.md)

---

## 1. 背景

当前系统已经具备单进程运行、Runtime、Controller、Platform、SQLite、Agent 执行、记忆、连接器和管理后台能力，但架构基础设施仍存在几个核心问题：

- 配置来源分散，存在 `.env`、数据库、旧 JSON、运行时默认值并存的问题。
- Agent 配置、人格、LLM 绑定、workspace 目录缺少统一文件化规范。
- 事件系统仍偏内存与临时调度，缺少持久化、回放、诊断和 schema 约束。
- LLM 执行引擎、会话层、子代理之间缺少统一状态机和全链路 trace。
- 可观测性目前分散在日志、RuntimeActivity、前端状态和部分诊断接口中，无法稳定回答“组件按什么顺序执行、每一步结果是什么、失败在哪里”。
- E2E 测试基础不完整，问题发现仍依赖人工浏览器操作。

本 ADR 的目标不是提出局部补丁，而是确定下一阶段基础设施增强的推进顺序和架构边界。

---

## 2. 决策

### ADR-019-A：`data` 是运行时状态与用户透明配置的唯一根目录

**决定**：运行时所有可变数据统一位于 `data` 下。Docker 只挂载 `data`。本地开发、容器运行、端到端测试必须共享同一目录契约。

标准目录：

```text
data/
  config/
    system.json
    llm.providers.json
    security.json
    connectors.json
  agent-templates/
    {templateId}/
      manifest.json
      soul.md
      persona.md
      tools.md
  agents/
    {agentInstanceId}/
      manifest.json
      config/
        llm.json
        memory.json
        tools.json
      workspace/
  workspaces/
    {workspaceId}/
      agents/
      sessions/
      artifacts/
  logs/
    system/
    diagnostics/
    sessions/
    agents/
    connectors/
  runtime/
    traces/
    events/
  memory/
    books/
    graphs/
    indexes/
  databases/
  backups/
  tmp/
```

**后果**：

- `.env` 不再作为 LLM 服务商、模型、Agent 配置来源。
- 数据库可以继续作为查询索引和运行时状态存储，但不能成为用户不可见配置的唯一来源。
- 配置文件必须可读、可校验、可备份、可 diff。

### ADR-019-B：LLM 配置采用 provider/model/profile/role 四层模型

**决定**：多 LLM 服务商、多模型、显意识 LLM、潜意识 LLM 必须通过同一配置模型解析。

解析顺序：

1. Agent instance override：`data/agents/{agentInstanceId}/config/llm.json`
2. Agent template default：`data/agent-templates/{templateId}/manifest.json`
3. Global role default：`data/config/llm.providers.json.roles`
4. 启动失败并报告明确错误；不静默回退到隐藏默认模型。

核心概念：

- `provider`：OpenAI、DeepSeek、Anthropic-compatible、本地 fake provider 等服务商。
- `model`：服务商下的具体模型与能力标签。
- `profile`：一次可复用的 LLM 运行配置，绑定 provider/model/reasoning/thinking/limits。
- `role`：系统角色使用哪个 profile，例如 `conscious`、`subconscious`、`embedding`、`tool_planner`。

**后果**：

- 每个 Agent 可以独立配置显意识 LLM 和潜意识 LLM。
- 后续新增模型或服务商不需要改 Runtime 核心执行代码。
- UI 需要展示 profile 解析结果，而不是只展示单个 model 字段。

### ADR-019-C：事件系统升级为持久化事件骨架

**决定**：事件系统是 Runtime、Session、SubAgent、Memory、Connector 的共同骨架。事件必须具备 envelope、schema、持久化、状态和诊断能力。

事件 envelope 必须包含：

```json
{
  "eventId": "evt_...",
  "eventType": "runtime.llm.started",
  "schemaVersion": 1,
  "traceId": "trace_...",
  "correlationId": "corr_...",
  "causationId": "evt_parent",
  "source": "runtime",
  "workspaceId": "default",
  "sessionId": "ses_...",
  "agentInstanceId": "agent_...",
  "timestamp": "2026-05-18T00:00:00Z",
  "payload": {}
}
```

事件状态：

- `pending`
- `leased`
- `completed`
- `retrying`
- `dead_letter`

**后果**：

- 所有关键执行步骤都能按 trace/session/agent 回放。
- Debug UI 可以直接从事件流构建时间线。
- 失败定位从“看日志猜测”升级为“按事件链定位”。

### ADR-019-D：执行引擎必须显式建模生命周期

**决定**：LLM 执行、工具调用、上下文合成、子代理调用不再只依赖日志描述状态，必须进入统一执行生命周期。

标准状态：

```text
queued
assembling_context
calling_llm
streaming
tool_calling
waiting_subagent
writing_memory
completed
failed
cancelled
```

每次执行必须输出：

- execution id
- trace id
- 输入摘要
- 选用 agent/profile/model
- 上下文合成结果摘要
- LLM 请求开始/结束
- tool call 请求/结果
- 子代理调用树
- token usage
- 错误和 retry 信息

**后果**：

- RuntimeActivity 与事件系统需要统一字段。
- 前端可观测页面可以展示执行顺序和耗时。
- 执行引擎内部状态转换必须可测试。

### ADR-019-E：会话层成为用户交互状态机

**决定**：会话层负责用户可见交互状态，不直接承担 Runtime 内部调度职责。它应聚合消息、流式帧、执行 trace、工具调用、子代理结果和错误。

会话日志采用 SQLite + JSONL 双写：

- SQLite：查询、分页、索引、UI 快速加载。
- JSONL：回放、调试、灾备、人工审查。

**后果**：

- `data/logs/sessions/{sessionId}.jsonl` 是可观测性和恢复的重要来源。
- 前端状态可以从会话事件恢复，而不是依赖临时内存状态。

### ADR-019-F：子代理必须文件化、隔离、可观测

**决定**：子代理不是普通工具调用的附属品，而是独立 Agent 实例。每个子代理必须拥有独立 workspace、配置目录、执行日志和结果归档。

子代理目录：

```text
data/workspaces/{workspaceId}/agents/{agentInstanceId}/
  config/
  runs/
    {runId}/
      input.json
      output.md
      events.jsonl
      files.json
      trace.json
  workspace/
```

**后果**：

- 主代理可以审计子代理做了什么。
- 子代理失败可以单独重跑或回放。
- 多 Agent 平台能力不再被数据库内部字段遮蔽。

### ADR-019-G：E2E 测试采用外部自动化 + 前端调试模式双轨

**决定**：E2E 不只依赖浏览器手工测试，也不把自动化逻辑塞进业务 UI。采用双轨：

1. 外部 E2E：Playwright 或 Python 浏览器自动化，模拟真实用户。
2. 前端调试模式：通过 URL flag 或配置开关开放 debug panel、trace overlay、状态快照和测试钩子。

前端调试模式只在开发/测试环境启用，不进入普通用户路径。

**后果**：

- 外部 E2E 验证真实浏览器行为。
- 调试模式降低失败定位成本。
- 测试数据依赖 Fake LLM、本地 SQLite 和 deterministic seed。

---

## 3. 不做的事

下一阶段不做以下事项：

- 不引入 RabbitMQ、Kafka、Redis 等外部基础设施作为默认依赖。
- 不把配置重新隐藏进数据库。
- 不为每个模块各自设计一套 trace/log/event 字段。
- 不在前端业务代码中硬编码测试流程。
- 不把所有任务一次性重构到完美状态；按架构骨架优先、业务补齐后续推进。

---

## 4. 行动指南

### 4.1 执行原则

1. 先定边界，再改实现。
2. 先打通骨架，再迁移业务。
3. 每个阶段必须可运行、可验证、可回滚。
4. 所有新基础设施必须带最小测试。
5. 每次改动只推进一个架构层，不混入无关 UI 美化或重构。

### 4.2 推荐实施顺序

#### Phase 1：配置与目录收口

目标：确保系统从 `data/config` 和 `data/agents` 启动，Docker 与本地行为一致。

任务：

- 完成 `.env` 和旧 LLM 环境变量来源移除。
- 完成 `PUDDING_DATA_ROOT` 在 Program、Docker、build 脚本中的统一。
- 建立 `agent-templates` 与 `agents` 默认模板。
- 建立配置 schema 校验和启动错误报告。
- 增加配置迁移工具。

验收：

- 删除 `.env` 后系统仍可用 Fake LLM 启动。
- `data/config/llm.providers.json` 可配置多个 provider、多个 model、多个 profile。
- 每个 Agent 的 conscious/subconscious profile 解析结果可查询。

#### Phase 2：事件系统持久化

目标：事件从内存队列升级为可持久化、可重试、可诊断的系统骨架。

任务：

- 标准化 `InternalEvent` envelope。
- 增加 SQLite 事件队列表。
- 实现 lease、retry、dead-letter。
- EventDispatcher 改为更新事件状态，不再 fire-and-forget 重入队。
- 增加事件查询 API。

验收：

- 进程重启后未完成事件仍可继续处理。
- 失败事件进入 dead-letter 并可查询。
- 事件按 trace/session/agent 可以还原执行链。

#### Phase 3：执行引擎状态机

目标：LLM、工具、上下文合成、子代理调用进入统一生命周期。

任务：

- 定义 execution record。
- 统一 RuntimeActivity 与事件字段。
- DirectLlmClient、AgentExecutionService、ContextPipeline、ToolExecutor 输出生命周期事件。
- 引入超时、取消、重试、熔断配置。
- Fake LLM 增强为支持流式、非流式、工具调用和错误注入。

验收：

- 单次用户消息可以展示完整执行 timeline。
- LLM 失败能定位到 provider/model/profile/request。
- Tool call 输入输出可审计。

#### Phase 4：会话层双写与回放

目标：会话状态可以恢复、回放、诊断。

任务：

- 增加 session JSONL writer。
- 消息、流式帧、工具调用、错误、usage 双写。
- 增加 session replay API。
- 前端可从 replay API 恢复会话视图。

验收：

- SQLite 删除前可通过 JSONL 做基本恢复。
- 单个 session 的执行顺序可以完整回放。
- 前端刷新后状态一致。

#### Phase 5：子代理隔离与观测

目标：子代理成为可审计的独立执行单元。

任务：

- 子代理 instance 配置文件化。
- 子代理 workspace 隔离。
- 子代理 run 目录归档输入输出。
- 主代理与子代理 trace 关联。
- Admin UI 展示子代理调用树。

验收：

- 每个子代理 run 都有独立归档目录。
- 主会话能看到子代理状态、耗时、结果、错误。
- 子代理可以在测试中独立重放。

#### Phase 6：E2E 与前端调试模式

目标：把人工浏览器验证升级为可重复测试。

任务：

- 修复 `PuddingWebApiTests` 测试发现与运行稳定性。
- 增加 Playwright 或 Python 浏览器测试。
- 增加前端 debug mode。
- 增加 trace overlay 和 state snapshot。
- 将 Docker 启动、健康检查、核心流程测试串起来。

验收：

- 一条命令可启动 Docker 并验证核心聊天链路。
- E2E 失败时能输出截图、trace、浏览器日志、后端 trace id。
- 前端 debug mode 不影响生产用户路径。

---

## 5. 工程约束

### 5.1 文件格式约束

- 系统配置：JSON。
- Agent 行为文本：Markdown。
- Agent 结构化配置：JSON 优先；YAML 暂不作为默认格式。
- 运行时日志：JSONL。
- 大型二进制或 artifact：按 workspace/run 目录保存，不进数据库。

### 5.2 兼容约束

- 已有数据库字段短期保留，但新写入路径以文件配置为源。
- 旧 API 保持可用，但内部解析应走新配置服务。
- 迁移工具必须非破坏性，默认只复制或生成新文件，不删除旧数据。

### 5.3 安全约束

- API Key 不进入普通日志。
- 配置 UI 展示密钥时默认脱敏。
- trace/event payload 必须支持字段级脱敏。
- `data/backups` 默认不包含明文密钥，除非用户显式选择。

---

## 6. 优先级矩阵

| 优先级 | 模块 | 原因 |
|--------|------|------|
| P0 | 配置与目录 | 没有统一根目录，后续 Agent、多 provider、Docker、E2E 都会反复分叉 |
| P0 | 事件系统 | 执行引擎、会话层、子代理、连接器都依赖事件骨架 |
| P0 | 执行引擎 | 这是 LLM 调用、工具调用、上下文合成的核心链路 |
| P0 | 会话层 | 用户可见状态和诊断入口依赖会话聚合 |
| P0 | 子代理 | 多 Agent 平台能力的关键抽象 |
| P1 | 可观测性 UI | 后端 trace/event 稳定后再做 UI，可避免 UI 反复重写 |
| P1 | E2E | 与 Fake LLM 和调试模式并行推进，逐步替代人工验证 |

---

## 7. 下一步执行清单

1. 接受或修改本 ADR。
2. 将 [Tasks](../Tasks.md) 中 `ARCH-CONFIG-*`、`ARCH-EVENT-*`、`ARCH-EXEC-*` 拆成可执行开发计划。
3. 先执行 `ARCH-CONFIG-001` 到 `ARCH-CONFIG-005`。
4. 配置目录稳定后，执行 `ARCH-EVENT-001` 到 `ARCH-EVENT-004`。
5. 事件骨架稳定后，执行 `ARCH-EXEC-001` 到 `ARCH-EXEC-006`。
6. 每完成一个阶段，新增一份 QA 报告到 `Docs/QA`。

---

## 8. 实施进度追踪

> 最后更新：2026-05-18

### Phase 1：配置与目录收口

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-CONFIG-001 | 全仓库移除 `.env` / LLM 环境变量 | ✅ done | PASS_WITH_NOTES | `PuddingFileConfigLoader` 统一加载，`LlmProfileResolver` 支持多 profile |
| ARCH-CONFIG-002 | 统一 `data/config/*.json` 加载入口 | ✅ done | PASS_WITH_NOTES | `ConfigLoadResult<T>` 错误收集，schema 校验 |
| ARCH-CONFIG-003 | 多 LLM provider/model/profile 支持 | ✅ done | PASS_WITH_NOTES | provider→model→profile→role 四层解析链 |
| ARCH-CONFIG-004 | Agent 专属配置目录落地 | ✅ done | — | `data/agents/general-assistant/manifest.json` + `config/llm.json` + `config/memory.json` |
| ARCH-CONFIG-005 | Agent 模板目录落地 | ✅ done | — | `data/agent-templates/general-assistant/` 含 SOUL/AGENTS/TOOLS/BOOTSTRAP/MEMORY.md |
| ARCH-CONFIG-006 | 旧配置迁移工具 | 🔲 pending | — | 从 `data/conf/*`、`data/llm/*` 迁移 |
| ARCH-CONFIG-007 | 配置管理 API 与 Admin UI | 🔲 pending | — | — |

### Phase 2：事件系统持久化

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-EVENT-001 | 事件 envelope 标准化 | ✅ done | — | `InternalEvent` 添加 SchemaVersion/CausationId/TraceId/CorrelationId；Timestamp→TimestampUtc |
| ARCH-EVENT-002 | 事件 schema 注册与版本管理 | ✅ done | — | `EventSchemaRegistry`：42 种事件类型，6 类别，兼容性检查 |
| ARCH-EVENT-003 | 持久化事件队列 | ✅ done | PASS_WITH_NOTES | SQLite 队列 + lease + retry + dead-letter |
| ARCH-EVENT-004 | 事件回放与诊断 | ✅ done | — | `EventDiagnosticsController`：按 session/trace/agent 查询 + causation 回溯 + 统计 |
| ARCH-EVENT-005 | 事件系统可观测性 UI | 🔲 pending | — | — |

### Phase 3：LLM 执行引擎

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-EXEC-001 | 统一 LLM Gateway 协议边界 | ✅ done | PASS_WITH_NOTES | DirectLlmClient 消除手工 JSON 构造 |
| ARCH-EXEC-002 | 显意识/潜意识 LLM profile 路由 | ✅ done | — | `LlmProfileResolver` 支持 instance→template→global 链 |
| ARCH-EXEC-003 | 执行生命周期状态机 | ✅ done | — | queued→assembling_context→calling_llm→tool_calling→completed/failed/cancelled |
| ARCH-EXEC-004 | 超时/取消/重试/熔断策略 | ✅ done | — | LlmProviderStrategy + DirectLlmClient 超时+重试+熔断器 |
| ARCH-EXEC-005 | Tool call 审计链路 | ✅ done | — | 工具调用增强 metadata（SHA256 args hash、耗时、输出长度）；审批记录 |
| ARCH-EXEC-006 | Fake LLM 测试基座稳定化 | ✅ done | — | 非流式+流式+工具调用响应；5个测试通过 |

### Phase 4：会话层

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-SESSION-001 | Session State Machine 明确化 | ✅ done | — | [ADR-020](20会话状态机与事件规范ADR.md)：3 态 + 17 事件帧序列 + Channel 生命周期 |
| ARCH-SESSION-002 | SQLite + JSONL 双写一致性 | ✅ done | — | 写入顺序 SQLite→JSONL；`CheckConsistencyAsync`；`/consistency` API |
| ARCH-SESSION-003 | 会话恢复与重放 | ✅ done | — | `ReplaySessionAsync` + `GET /replay` API |
| ARCH-SESSION-004 | 会话级 trace 聚合 | ✅ done | — | `GetTraceReportAsync` + `GET /trace-report` API |
| ARCH-SESSION-005 | 会话诊断页面 | 🔲 pending | — | 前端任务，待 UI 阶段 |

### Phase 5：子代理系统

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-SUBAGENT-001~005 | 子代理（5 项） | 🔲 pending | — | — |

### Phase 6：E2E 测试与调试

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-E2E-001~007 | E2E（7 项） | 🔲 pending | — | — |

### P1：记忆/网关/可观测性

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-MEM-001~004 | 记忆图书馆 | 🔲 pending | — | — |
| ARCH-GATEWAY-001~004 | 网关与连接器 | 🔲 pending | — | — |
| ARCH-OBS-001~005 | 可观测性 | 🔲 pending | — | — |

### P2：运维与文档

| 任务 ID | 标题 | 状态 | QA | 备注 |
|--------|------|------|-----|------|
| ARCH-OPS-001~004 | 运维文档 | 🔲 pending | — | — |

### 统计

- ✅ done: 19/44
- 🔲 pending: 25/44

### Git 里程碑

| Tag/Commit | 日期 | 说明 |
|-----------|------|------|
| `21f6057` | 2026-05-19 | feat: strengthen runtime config events and session observability（19 项 P0 任务完成） |

