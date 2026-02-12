# Agent 私有日志与摘要召回设计方案

> 日期：2026-06-16  
> 范围：Agent 普通消息日志、raw 证据日志、摘要日志、潜意识 LLM 文本处理服务、每日摘要任务、上下文召回注入  
> 关联 ADR：[52ADR-051Agent私有日志与摘要召回ADR](../07架构/52ADR-051Agent私有日志与摘要召回ADR.md)

---

## 1. 背景

当前 Pudding 已具备以下基础：

- `ChatMessages` 作为面向 UI 和查询的聊天转录物化视图。
- `session_event_log` 作为 raw session event 证据源。
- `query_session_logs` 工具可按 session 读取普通消息转录，也可在 diagnostic 模式读取 raw event。
- `IMemoryLlmClient` / `DirectMemoryLlmClient` 已能调用潜意识 LLM。
- `ContextCompactionService` 已有会话压缩骨架，但摘要生成仍是抽取式占位。
- `ContextPipeline` 已有 `L4-PINNED`、`L6-RECALLED` 和记忆图书馆召回。

缺口是：

1. `ChatMessages` 缺少明确的 agent 归属，也没有落到 `data/agents/{agentInstanceId}` 私有目录。
2. raw 日志与系统日志、agent 消息日志的边界不够清晰。
3. 没有每日摘要日志，也没有当天滚动摘要 `content.md`。
4. 普通日志和摘要日志没有专门的 FTS 索引，无法支持“最近 5/30/180 天”召回策略。
5. 潜意识 LLM 还缺少面向文本处理的稳定服务入口。

目标是建立一条可施工的 V1 闭环：Agent 能在每次对话启动或用户新消息时召回自己的历史工作索引，必要时再通过工具读取完整普通日志或 raw 证据。

---

## 2. 日志分层

### 2.1 系统日志

系统日志记录 Pudding 平台运行情况，不属于 Agent 的普通记忆上下文。

内容：

- backend / frontend / proxy / supervisor 日志。
- health check、runtime activity、telemetry metrics。
- diagnostics timeline、proxy diagnostics、LLM gateway diagnostics。

位置：

```text
data/logs/...
```

访问规则：

- 默认不进入 Agent 上下文。
- Agent 只能通过显式 diagnostic 工具读取，且必须保留现有诊断权限边界。

### 2.2 Agent 普通消息日志

Agent 普通消息日志是 Agent 可读取的会话转录，只包含可以进入上下文的普通消息。

内容：

- user message。
- assistant final message。
- 可选系统生成的 compact summary。

不包含：

- thinking。
- tool_call / tool_result。
- delta event。
- usage。
- 内部 event。
- raw diagnostic payload。

位置：

```text
data/agents/{agentInstanceId}/logs/messages/YYYY-MM-DD/{sessionId}.jsonl
data/agents/{agentInstanceId}/logs/messages/YYYY-MM-DD/{sessionId}.md
```

`.jsonl` 是结构化事实源，`.md` 是 Agent 和人工可读副本。两者都可以由同一个写入服务生成。

### 2.3 Agent raw 日志

Agent raw 日志是完整证据层，用于复盘和诊断。

内容：

- `session_event_log` 中与该 Agent 会话相关的事件。
- tool_call、tool_result、thinking、delta、usage、done、error 等。

位置：

```text
data/agents/{agentInstanceId}/logs/raw/YYYY-MM-DD/{sessionId}.jsonl
```

访问规则：

- 不进入默认上下文。
- `query_session_logs` 或后续日志工具读取 raw 日志时，需要明确 raw/diagnostic 动作。

### 2.4 Agent 摘要日志

摘要日志是上下文召回索引（由LLM读取消息日志，根据消息日志总结的），不是完整历史。

位置：

```text
data/agents/{agentInstanceId}/memory/daily/YYYY-MM-DD.md
data/agents/{agentInstanceId}/memory/content.md
data/agents/{agentInstanceId}/memory/index.json
```

用途：

- `daily/YYYY-MM-DD.md`：每日 0 点后对上一天普通消息日志的精简总结。
- `content.md`：当天未结束内容的滚动摘要，每天重置。
- `index.json`：摘要文件索引、hash、生成时间、来源 session 列表、token 估算、FTS 索引状态。

---

## 3. 数据模型与文件结构

### 3.1 ChatMessages 扩展

`ChatMessages` 仍作为 UI 和查询的 DB 物化视图，但需要增加 agent 归属字段：

```text
WorkspaceId
AgentInstanceId
AgentTemplateId
SessionId
Role
Content
CreatedAt
```

短期可通过迁移增加 nullable 字段，并在写入链路补齐新数据。旧数据缺 agent 归属时只能参与 workspace/session 级查询，不进入 agent 私有日志生成。

### 3.2 session_event_log 扩展

`session_event_log` 需要补充：

```text
agent_instance_id
agent_template_id
```

这样 raw 日志可以按 agent 分区落盘，也能在诊断时做 agent 级隔离。

### 3.3 AgentLog 文件结构

建议新增 `PuddingDataPaths` helper：

```csharp
AgentInstanceMessageLogsRoot(agentInstanceId)
AgentInstanceRawLogsRoot(agentInstanceId)
AgentInstanceMemoryRoot(agentInstanceId)
AgentInstanceDailySummaryRoot(agentInstanceId)
AgentInstanceContentSummaryFile(agentInstanceId)
AgentInstanceMemoryIndexFile(agentInstanceId)
```

目录初始化遵循现有 `data` 冷启动原则：检查目录是否存在，不存在则创建。

---

## 4. 核心服务

### 4.1 SubconsciousTextProcessingService

职责：

- 封装潜意识 LLM 文本处理能力（传入提示词和上下文文本，输出文本）。
- 为每日摘要、会话压缩摘要、当天滚动摘要提供统一入口。
- 只使用 memory/subconscious LLM 配置，不隐式回退主聊天模型。

建议接口：

```csharp
public interface ISubconsciousTextProcessingService
{
    Task<string> SummarizeDailyLogAsync(DailyLogSummaryRequest request, CancellationToken ct);
    Task<string> SummarizeCurrentSessionAsync(CurrentSessionSummaryRequest request, CancellationToken ct);
    Task<string> CompressConversationAsync(ConversationCompressionRequest request, CancellationToken ct);
}
```

输出约束：

- Markdown。
- 精简、短小、索引化。
- 记录重要工作、决策、文件、问题、后续事项。
- 不记录闲聊、重复状态、无价值工具输出。
- 不输出思维链。

### 4.2 AgentConversationLogService

职责：

- 接收普通 user/assistant message。
- 写入 `ChatMessages`。
- 追加写入 agent 私有普通消息日志 `.jsonl` / `.md`。
- 更新普通日志 FTS 索引。

现有 `ChatTranscriptWriter` 应改为调用该服务，或由该服务替代其职责。

### 4.3 AgentRawLogMirrorService

职责：

- 从 session event 写入链路镜像 raw 事件到 agent 私有 raw 日志。
- 保留 event sequence、event type、recorded_at、trace/correlation、component/operation。
- 不做摘要、不做清洗，必要时只做字段脱敏。

### 4.4 AgentDailySummaryService

职责：

- 对某个 agent 某一天的普通消息日志生成 daily summary。
- 保证幂等：同一天重复执行时，若源日志 hash 未变则跳过。
- 写入 `daily/YYYY-MM-DD.md`。
- 更新 `memory/index.json`。
- 更新 summary FTS 索引。

### 4.5 AgentCurrentSummaryService

职责：

- 管理当天 `content.md`。
- 每次自动会话压缩或用户发起 `/compact` 成功后，追加或重写当天滚动摘要。
- 每天第一次访问时检查日期，跨日则重置。

---

## 5. 定时任务

新增系统维护型 `BackgroundService`，不走 Agent `CronSchedulerService`。

原因：

- 每日摘要是系统维护任务，不应创建普通 Agent 会话。
- 失败重试、幂等和扫描范围由系统控制。
- 避免定时任务本身污染用户会话日志。

触发：

```text
每日 00:00 本地时区
```

处理：

1. 枚举 `data/agents/*/logs/messages/{yesterday}`。
2. 读取普通消息日志。
3. 调用 `SubconsciousTextProcessingService.SummarizeDailyLogAsync`。
4. 写入 `data/agents/{agentInstanceId}/memory/daily/YYYY-MM-DD.md`。
5. 更新 `index.json` 和 summary FTS。
6. 写 telemetry metric 和 runtime activity。

补偿：

- 服务启动时检查昨天是否已摘要。
- 如果昨天 summary 缺失或源 hash 变化，补跑一次。

---

## 6. FTS 召回

V1 建议为 agent 私有日志建立独立索引：

```text
data/agents/{agentInstanceId}/memory/log-index.db
```

表：

```text
message_logs_fts
  agent_instance_id
  workspace_id
  session_id
  day
  role
  content
  evidence_ref

daily_summaries_fts
  agent_instance_id
  day
  title
  summary
  tags
  source_path
```

召回策略：

- 用户每次消息发送：
  - 最近 5 天普通日志 FTS 召回，最多 20 条。
  - 最近 30 天普通日志 FTS 召回，最多 10 条。
  - 最近 180 天摘要日志 FTS 召回，最多 10 条。
  - 记忆图书馆召回，沿用现有 `L6-RECALLED`。
- 新 Session 冷启动：
  - 完整塞入最近 2 天 daily summary。
  - 完整塞入当天 `content.md`。
- 会话压缩/切换：
  - 生成 compact summary。
  - 写入 `content.md`。
  - 后续新 session 冷启动读取。

---

## 7. ContextPipeline 注入

建议新增 `AgentRecallContextService`，由 `ContextPipeline` 调用。

新增上下文层：

```text
L4A-AGENT-DAILY-SUMMARY
L6A-AGENT-LOG-RECALL
```

放置建议：

- 冷启动摘要：放在 `L4-PINNED` 后，`L5-RECENT` 前。
- 每轮日志召回：放在 `L6-RECALLED` 附近，和记忆图书馆结果分开标注来源。

注入内容必须包含 evidence ref：

```text
- [daily:2026-06-15] 完成 SKILL 文件系统服务和上下文索引注入。path=...
- [message:2026-06-15/session-xxx#42] 用户要求区分系统日志和 agent 消息日志。ref=...
```

完整原文不默认注入。Agent 需要细节时，通过日志工具按日期/session/ref 读取。

---

## 8. 工具能力

扩展 `query_session_logs`：

```text
messages_by_day
messages_by_ref
agent_daily_summary
agent_content_summary
```

读取规则：

- 普通日志读取默认允许低风险，只限当前 agent 自己的目录。
- raw 日志读取仍需 diagnostic/raw 明确动作。
- 系统日志不通过该工具默认开放。

---

## 9. 施工顺序

### 阶段 1：日志归属和文件落盘

1. `ChatMessages` 增加 agent/workspace/template 字段。
2. `session_event_log` 增加 agent/template 字段。
3. 增加 `PuddingDataPaths` agent log helper。
4. 实现 `AgentConversationLogService`。
5. 改造 `ChatTranscriptWriter` 写 DB + agent 私有普通日志。
6. 实现 raw log mirror。
7. 补单元测试和集成测试。

### 阶段 2：潜意识文本处理和每日摘要

1. 新增 `SubconsciousTextProcessingService`。
2. 实现 `AgentDailySummaryService`。
3. 实现每日 0 点 `BackgroundService`。
4. 写 `daily/YYYY-MM-DD.md`、`index.json`。
5. 增加失败重试和幂等测试。

### 阶段 3：当天 content.md 和会话压缩

1. 替换 `ExtractiveContextCompactionSummaryGenerator` 为潜意识 LLM 生成器。
2. 压缩成功后写 `content.md`。
3. `/compact` 接入真实压缩。
4. 跨日重置 `content.md`。

### 阶段 4：FTS 和上下文注入

1. 建立 agent 私有日志索引。
2. 实现 `AgentRecallContextService`。
3. ContextPipeline 接入最近 2 天 summary、5/30 天普通日志召回、180 天 summary 召回。
4. 扩展 `query_session_logs` 按日期/ref 读取。
5. 加上下文层 metrics，观察 token 占比和缓存命中。

---

## 10. 验收标准

1. 新会话启动时，能看到最近 2 天 daily summary 和当天 `content.md`。
2. 用户每次发送消息时，能按策略召回 agent 私有普通日志和 daily summary。
3. 普通日志只包含 user/assistant final，不包含 thinking/tool/raw event。
4. raw 日志可以按 agent/session/day 精确读取，但不进入默认上下文。
5. 系统日志不被 Agent 默认读取。
6. 每日 0 点任务可幂等生成昨天 summary。
7. `content.md` 每天重置，压缩后更新。
8. 所有新增文件都在 `data/agents/{agentInstanceId}` 下，遵循 data root helper。
9. 召回条目带 evidence ref，可通过工具取回完整普通日志。
10. 失败时 fail-open：摘要/索引失败不阻塞主对话，只记录诊断和 metrics。

