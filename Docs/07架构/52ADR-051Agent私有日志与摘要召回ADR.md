# 52 ADR-051 Agent 私有日志与摘要召回

> 状态：**proposed**  
> 日期：2026-06-16  
> 关联：  
> - [15潜意识LLM子代理系统ADR](15潜意识LLM子代理系统ADR.md)  
> - [32ADR-031聊天历史转录持久化与事件日志回放边界](32ADR-031聊天历史转录持久化与事件日志回放边界.md)  
> - [43ADR-042上下文自动压缩与主动Compact命令ADR](43ADR-042上下文自动压缩与主动Compact命令ADR.md)  
> - [Agent私有日志与摘要召回设计方案](../Features/Agent私有日志与摘要召回设计方案.md)

---

## 1. 背景

Pudding 当前存在三类历史数据：

1. 系统运行日志和诊断日志。
2. 面向 UI 的普通聊天转录 `ChatMessages`。
3. 作为证据源的 raw session event 日志 `session_event_log`。

新的记忆召回目标要求：

- 每次新会话启动时，Agent 能读取最近摘要。
- 用户每次发送消息时，Agent 能从历史普通日志和摘要日志中召回相关内容。
- 每日 0 点对上一天普通日志生成精简摘要。
- 当前未结束的一天通过 `content.md` 提供滚动摘要。
- Agent 可以在需要时读取完整普通日志，raw 日志只作为诊断证据读取。

这要求日志必须具备明确的 agent 归属，并且系统日志、Agent 普通消息日志、Agent raw 日志、Agent 摘要日志必须分层存储和授权。

---

## 2. 决策

### ADR-051-A：系统日志与 Agent 日志分离

**决定**：系统日志继续保存在 `data/logs/...`，Agent 可读历史放入 `data/agents/{agentInstanceId}/...`。

系统日志包括：

- backend / frontend / proxy / supervisor。
- runtime activity。
- telemetry metrics。
- diagnostics timeline。
- proxy diagnostics。

Agent 默认上下文不得读取系统日志。只有 diagnostic 工具可以显式读取诊断证据。

### ADR-051-B：Agent 普通消息日志成为 Agent 可读历史

**决定**：普通消息日志只包含 user message、assistant final message 和系统 compact summary，写入 Agent 私有目录：

```text
data/agents/{agentInstanceId}/logs/messages/YYYY-MM-DD/{sessionId}.jsonl
data/agents/{agentInstanceId}/logs/messages/YYYY-MM-DD/{sessionId}.md
```

普通消息日志是：

- 每日摘要输入。
- 普通日志 FTS 召回输入。
- Agent 工具默认可读取的历史转录。

普通消息日志不是：

- raw event。
- tool evidence。
- thinking 或 delta 流。
- 系统诊断日志。

### ADR-051-C：Agent raw 日志保留为诊断证据层

**决定**：raw session event 以 agent 私有副本保存：

```text
data/agents/{agentInstanceId}/logs/raw/YYYY-MM-DD/{sessionId}.jsonl
```

raw 日志仅用于：

- 精确复盘。
- 工具调用证据核查。
- 错误诊断。

raw 日志不进入默认上下文。Agent 读取 raw 日志必须通过明确 raw/diagnostic 动作。

### ADR-051-D：ChatMessages 和 session_event_log 必须关联 agent

**决定**：`ChatMessages` 和 `session_event_log` 都需要携带 agent 归属。

`ChatMessages` 新增：

```text
workspace_id
agent_instance_id
agent_template_id
```

`session_event_log` 新增：

```text
agent_instance_id
agent_template_id
```

理由：

- 没有 agent 归属，就无法按 agent 生成普通日志、raw 日志、每日摘要和召回索引。
- workspace/session 粒度不足以保证多 Agent 隔离。
- 后续工具权限和 data 目录私有化都依赖 agentInstanceId。

### ADR-051-E：摘要日志以文件为 V1 权威产物

**决定**：每日摘要和当天滚动摘要存入 agent 私有文件，而不是先引入新的平台表。

```text
data/agents/{agentInstanceId}/memory/daily/YYYY-MM-DD.md
data/agents/{agentInstanceId}/memory/content.md
data/agents/{agentInstanceId}/memory/index.json
```

理由：

- 与 SKILL 文件系统服务和 Agent 私有 data 目录方向一致。
- 方便 Agent 工具读取。
- 方便人工排查。
- 降低 V1 DB migration 和 UI 依赖。

### ADR-051-F：潜意识 LLM 通过文本处理服务统一调用

**决定**：新增 `SubconsciousTextProcessingService`，封装 `IMemoryLlmClient.ChatWithConfigAsync`。

该服务负责：

- 每日普通日志摘要。
- 当前会话滚动摘要。
- 会话压缩摘要。
- 其他后台文本提炼任务。

该服务不得隐式回退主聊天模型。缺少潜意识 LLM 配置时，应记录明确失败并 fail-open。

### ADR-051-G：每日摘要使用系统维护后台任务

**决定**：每日 0 点摘要任务使用专门 `BackgroundService`，不走 `CronSchedulerService`。

理由：

- 它是系统维护任务，不是用户 Agent 任务。
- 不应创建普通 Agent 会话。
- 不应污染普通消息日志。
- 需要独立的幂等、补偿和失败重试策略。

### ADR-051-H：召回分冷启动、每轮消息、压缩切换三个时机

**决定**：上下文合成管线按三个时机注入不同来源。

新 Session 冷启动：

- 完整注入最近 2 天 daily summary。
- 完整注入当天 `content.md`。

用户每次发送消息：

- 最近 5 天普通消息日志 FTS 召回，最多 20 条。
- 最近 30 天普通消息日志 FTS 召回，最多 10 条。
- 最近 180 天 daily summary FTS 召回，最多 10 条。
- 现有记忆图书馆召回机制。

当前窗口结束或切换会话：

- 触发会话压缩。
- 压缩结果写入 `content.md`。
- 新 session 启动时读取 `content.md`。

### ADR-051-I：FTS 索引以 agent 私有索引为 V1 边界

**决定**：V1 为 agent 私有普通日志和摘要日志建立独立索引：

```text
data/agents/{agentInstanceId}/memory/log-index.db
```

索引对象：

- 普通消息日志。
- 每日摘要日志。

索引必须保存 evidence ref，使 Agent 可以通过工具取回完整日志。

---

## 3. 后果

### 正面影响

- Agent 历史数据与系统日志边界清晰。
- 每个 Agent 的历史、摘要、raw 证据都能物理隔离。
- 上下文召回可以只注入摘要和索引，减少 token 压力。
- raw 证据仍可追溯，但不会污染默认上下文。
- 每日摘要和 `content.md` 能补齐跨 session 冷启动记忆。

### 成本

- 需要 DB migration 给 `ChatMessages` 和 `session_event_log` 补 agent 字段。
- 需要新增文件写入服务和索引服务。
- 需要处理旧数据无法映射 agent 的兼容策略。
- 需要每日任务的幂等、补偿和失败观测。

### 风险

- 如果 session 到 agent 的映射不可靠，摘要可能归错 agent。
- 如果 summary prompt 不严格，摘要可能过长或记录低价值信息。
- 如果普通日志和 raw 日志工具边界不清，可能把诊断数据误塞上下文。
- 如果 FTS 索引更新失败，召回覆盖率下降。

缓解：

- 写入时强制携带 `agentInstanceId`，缺失时不写 agent 私有日志，只记录警告。
- 摘要 prompt 明确“只记录重要工作和决策，保持短小”。
- raw 读取保留 diagnostic 显式开关。
- 索引失败 fail-open，不阻塞主对话，并记录 metric。

---

## 4. 非目标

V1 不实现：

- 面向用户的摘要日志管理 UI。
- 向量召回。
- 跨 agent 共享日志召回。
- 对系统日志的默认 Agent 读取。
- 对 raw 日志的默认上下文注入。
- 对旧历史数据的完整自动归档迁移。

---

## 5. 施工约束

1. 所有 Agent 可读历史必须位于 `data/agents/{agentInstanceId}` 下。
2. 文件路径必须通过 `PuddingDataPaths` helper 推导。
3. 写入服务必须先检查目录是否存在，不存在则初始化。
4. 普通消息日志和 raw 日志必须分开写入、分开授权。
5. 每日摘要必须幂等，源日志 hash 未变时跳过。
6. `content.md` 必须每天重置。
7. 摘要和召回失败不得阻塞主对话。
8. 所有关键路径写入 runtime activity / telemetry metric。
9. 单元测试必须覆盖路径隔离、目录初始化、摘要幂等、工具权限、上下文注入顺序。

---

## 6. 验收

1. 新建 Agent 会话后，`data/agents/{agentInstanceId}/logs/messages/...` 出现普通消息日志。
2. raw event 写入 `data/agents/{agentInstanceId}/logs/raw/...`，且不出现在普通消息日志。
3. `ChatMessages` 和 `session_event_log` 新记录包含 agent 归属。
4. 每日 0 点任务生成昨天 `daily/YYYY-MM-DD.md`。
5. 当天压缩摘要写入 `content.md`，跨日自动重置。
6. ContextPipeline 冷启动注入最近 2 天 daily summary 和 `content.md`。
7. 用户每轮消息触发普通日志和摘要日志召回。
8. Agent 可通过工具按日期/session/ref 读取普通日志。
9. raw 日志读取必须显式 diagnostic/raw。
10. 系统日志不会进入 Agent 默认上下文。

