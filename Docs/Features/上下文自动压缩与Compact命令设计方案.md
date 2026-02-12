# 上下文自动压缩与 Compact 命令设计方案

> 日期：2026-05-23  
> ADR：[43ADR-042上下文自动压缩与主动Compact命令ADR](../07架构/43ADR-042上下文自动压缩与主动Compact命令ADR.md)  
> 范围：Runtime 上下文窗口、会话持久化、Admin Chat `/compact` 命令、SSE 状态反馈  
> 目标：当上下文窗口接近不健康状态时自动压缩历史，并允许用户通过 `/compact` 主动压缩当前会话上下文。

---

## 1. 背景

PuddingAgent 已经具备以下基础：

- `ContextPipeline` 可以按层组装上下文，并有 `None / Gentle / Aggressive` 的预算级别。
- `ContextWindowManager.BuildContextFromDbAsync` 会跳过 `CompactedBy != null` 的消息。
- `MessageEntity` 已有 `CompactedBy` 字段，可表达旧消息被某条压缩摘要覆盖。
- Admin Chat 已有 Slash 命令面板，用户输入 `/` 时会展示命令菜单。
- Chat SSE 事件和 `SessionStateManager` 已承载运行时状态、usage、done、工具事件。

当前缺口是：

- 没有真正的压缩执行器，压缩不会持久化。
- 没有会话级 context health 状态机。
- 没有手动压缩 API。
- `/compact` 只能作为普通文本进入对话，不能触发系统动作。
- 自动压缩没有失败熔断，可能在临界窗口反复重试。
- 工具输出、日志、文件片段和 RAG 块在进入 LLM 前缺少统一压缩/取回/观测层，容易拉低前缀缓存命中率并放大输入 token。

### 1.1 Headroom 研究摘要

[Headroom](https://github.com/chopratejas/headroom) 是一个面向 AI Agent 的本地上下文优化层，提供 Python/TypeScript library、OpenAI/Anthropic 兼容 proxy、MCP server 和 agent wrapper。它的公开设计包含四个值得 Pudding 借鉴的点：

1. **前缀稳定**：把日期、session id、trace id 等易变内容移出 prompt 前部，减少服务商前缀缓存失效。
2. **内容路由压缩**：按 JSON、日志、diff、文件、代码、文本等类型选择不同压缩策略。
3. **CCR 可逆取回**：本地保存原文，prompt 只携带压缩结果和 hash，需要细节时通过工具取回。
4. **压缩统计**：输出 before/after tokens、压缩率、跳过原因和节省估算。

对 Pudding 的启发是：`/compact` 解决“长会话历史摘要”的问题，但无法单独解决“本轮工具输出或 RAG 证据过大”的问题。因此需要在 FullCompact 之外增加 LLM 前置输入压缩层。

需要保留的边界：

- 不压缩用户原始消息和 system prompt 语义，只做动态字段后移和顺序稳定。
- 最近几轮、当前执行轮、用户正在编辑或审查的代码默认保护。
- RAG 块不能直接按节省率启用，必须先验证答案质量、引用命中和取回兜底。
- Headroom 可作为开发期对照基准或可选 provider，不作为 V1 默认硬依赖。

---

## 2. 目标

### 必须支持

1. 系统能根据上下文窗口健康度自动触发压缩。
2. 用户能通过 `/compact` 主动压缩当前会话。
3. 压缩结果持久化为一条 summary message。
4. 被摘要覆盖的旧消息通过 `CompactedBy` 指向 summary message。
5. 后续上下文重建时保留 summary message，排除被压缩的旧消息。
6. 前端能显示压缩开始、成功、失败状态。
7. 压缩动作必须可观测、可诊断、可测试。

### 暂不支持

1. 精确 tokenizer 依赖。第一阶段使用保守估算。
2. provider 级 cache edit 优化。
3. 多模型压缩路由。
4. 用户编辑压缩摘要。
5. 压缩结果跨会话复用。

---

## 3. 核心设计

采用四层压缩：

| 层级 | 触发 | 成本 | 结果 | 第一阶段 |
| --- | --- | --- | --- | --- |
| InputCompression | 每次 LLM 调用前 | 低 | 压缩工具输出、日志、文件、RAG 块并保留可取回原文 | 原型验证 |
| MicroCompact | 每轮后 | 极低 | 清理旧工具结果内容 | 规划接口，延后启用 |
| SessionMemoryCompact | warning / unhealthy | 低 | 用会话记忆替换远期历史 | 第二阶段 |
| FullCompact | critical / blocking / `/compact` | 高 | LLM 生成完整摘要并持久化 | 第一阶段实现 |

第一阶段优先实现 FullCompact，因为它能闭环持久化、API、UI、后续上下文重建和验收。InputCompression 可以并行做原型，但必须以 fail-open 方式运行：压缩失败、取回不可用或质量门禁失败时直接使用原文。

### 3.1 LLM 前置输入压缩层

`ContextPipeline` 组装出候选消息后、`DirectLlmClient` 发起请求前，增加 `IContextInputCompressionService`：

```csharp
public interface IContextInputCompressionService
{
    Task<ContextInputCompressionResult> CompressAsync(
        ContextInputCompressionRequest request,
        CancellationToken ct);
}
```

输入包含 session/workspace、provider/model、上下文层列表、token 预算和安全策略。输出包含：

- 压缩后的 message/layer 列表；
- 每个 artifact 的 `hash`、`sourceType`、`tokensBefore`、`tokensAfter`、`compressionStrategy`；
- 跳过原因，例如 `too_small`、`user_message`、`recent_turn`、`active_code`、`rag_quality_gate_disabled`；
- 可写入 `telemetry_metric_events` / `context_layer_metric_events` 的指标。

压缩策略按内容类型分层：

| 内容 | 默认策略 | 必须保留 |
| --- | --- | --- |
| JSON 工具输出 | 结构保留、字段归纳、样本/异常保留 | key、错误项、ID/hash、首尾样本、数值异常 |
| 日志 | 模板聚类、重复行折叠、优先级过滤 | error/fatal/warn、stack trace、首尾时间边界 |
| 文件/search/diff | 降噪、片段摘要、重复命中折叠 | 文件路径、行号、hunk header、变更行 |
| RAG 块 | 默认只做去重/截断预览，语义压缩需开关 | 来源、引用 ID、标题、关键命中句、可取回 hash |
| 历史消息 | 交给 FullCompact/SessionMemoryCompact | 最近轮次和 compact_summary |

### 3.2 Artifact 与取回

压缩前原文不得长期混入 prompt；应写入本地 artifact store：

```text
ContextCompressionArtifact
  Hash
  WorkspaceId
  SessionId
  SourceType
  SourceId
  ContentHash
  TokensBefore
  TokensAfter
  CompressionStrategy
  ExpiresAtUtc
  RedactedPreview
```

第一阶段可以复用 SQLite + diagnostics bundle，后续再抽专表。LLM 侧只看到短 hash 和内部工具说明：

```text
原始内容已压缩，可在需要完整证据时调用 context.retrieve_artifact(hash)。
```

取回工具必须受 workspace/session/agent 权限约束，不能跨会话读取。取回事件要记录到 RuntimeActivity、session timeline 和 metrics，便于判断压缩是否过度。

### 3.3 Headroom 适配路线

Headroom 不进入默认依赖链，但可提供三个工作模式：

1. **Benchmark 模式**：用 Headroom 压缩同一批 Pudding 工具输出/日志/RAG 样本，对比 tokens saved、latency、answer quality。
2. **Dev Proxy 模式**：开发者显式配置本地 Headroom proxy，Pudding 将 LLM 请求指向 proxy，观察前缀缓存与压缩指标。
3. **Provider 模式**：新增可选 `HeadroomCompressionProvider`，在 `IContextInputCompressionService` 内调用本地 Headroom library/MCP/proxy；失败时 passthrough。

默认验收标准不是外部项目宣称的节省率，而是 Pudding 自己的指标：输入 token 降低、cache hit rate 不下降、benchmark 质量不回退、取回成功率可解释。

---

## 4. 上下文健康状态

### 4.1 有效窗口

不能直接使用模型最大上下文窗口，应扣除输出保留区和安全缓冲。

```text
effectiveWindowTokens =
  modelMaxContextTokens
  - min(modelMaxOutputTokens, 20000)
  - safetyBufferTokens
```

第一阶段默认：

```text
safetyBufferTokens = 3000
reservedOutputTokens = min(modelMaxOutputTokens, 20000)
```

### 4.2 状态分层

| 状态 | 条件 | 行为 |
| --- | --- | --- |
| healthy | `< 60%` | 不提示 |
| warning | `60%-75%` | 显示轻提示，可手动 compact |
| unhealthy | `75%-85%` | 自动尝试 SessionMemoryCompact；第一阶段仅提示 |
| critical | `85%-92%` | 自动触发 FullCompact |
| blocking | `> 92%` 或剩余 token 小于最小输出保留区 | 阻止普通发送，先压缩 |

### 4.3 估算来源优先级

1. 最近一次 LLM usage：`promptTokens / totalTokens / contextWindowTokens`。
2. `ContextAssemblyStore` 的 assembled snapshot。
3. `ContextWindowManager` 对内存历史和 DB 历史的估算。

第一阶段以 usage 和 assembled snapshot 为主，DB 估算兜底。

---

## 5. 后端组件

### 5.1 ContextHealthEvaluator

职责：

- 输入模型上下文、已用 token、输出保留区。
- 输出 `ContextHealthSnapshot`。
- 决定是否建议压缩、是否必须压缩。

建议类型：

```csharp
public enum ContextHealthState
{
    Healthy,
    Warning,
    Unhealthy,
    Critical,
    Blocking,
}

public sealed record ContextHealthSnapshot(
    string SessionId,
    int UsedTokens,
    int ContextWindowTokens,
    int EffectiveWindowTokens,
    int RemainingTokens,
    double UsageRatio,
    ContextHealthState State,
    bool ShouldSuggestCompact,
    bool ShouldAutoCompact,
    bool ShouldBlockSend
);
```

### 5.2 ContextCompactionService

职责：

- 执行手动和自动压缩。
- 选择压缩等级。
- 生成压缩摘要。
- 写入 summary message。
- 更新旧消息 `CompactedBy`。
- 发出 SSE 事件。
- 记录 RuntimeActivity。

建议接口：

```csharp
public interface IContextCompactionService
{
    Task<ContextHealthSnapshot> GetHealthAsync(
        string sessionId,
        CancellationToken ct);

    Task<ContextCompactionResult> CompactAsync(
        ContextCompactionRequest request,
        CancellationToken ct);
}

public sealed record ContextCompactionRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    ContextCompactionMode Mode,
    ContextCompactionLevel Level,
    string Reason
);

public enum ContextCompactionMode
{
    Manual,
    Auto,
}

public enum ContextCompactionLevel
{
    Micro,
    SessionMemory,
    Full,
}
```

### 5.3 FullCompact 摘要格式

压缩摘要必须面向后续 Agent 恢复上下文，而不是面向用户阅读。

建议格式：

```markdown
<compact_summary>
## 用户目标

## 已完成事项

## 关键决策

## 涉及文件和代码位置

## 工具调用与重要输出

## 错误、阻塞与修复

## 当前工作状态

## 明确的下一步

## 保留的用户偏好和约束
</compact_summary>
```

摘要生成失败时，不能删除旧消息；应返回失败事件并保持会话可继续。

### 5.4 ContextInputCompressionService

职责：

- 在 LLM 请求前扫描上下文层并识别可压缩块。
- 对工具输出、日志、文件/search/diff、RAG 块执行局部压缩。
- 写入 artifact store 并在 prompt 中放置可取回 hash。
- 记录压缩指标和跳过原因。
- 在任何异常下返回原始上下文，不阻断聊天主流程。

建议接口：

```csharp
public sealed record ContextInputCompressionRequest(
    string WorkspaceId,
    string SessionId,
    string? AgentId,
    string? ProviderId,
    string? ModelId,
    IReadOnlyList<ContextLayerPayload> Layers,
    ContextInputCompressionPolicy Policy);

public sealed record ContextInputCompressionPolicy(
    bool Enabled,
    bool CompressToolOutputs,
    bool CompressLogs,
    bool CompressFiles,
    bool CompressRagChunks,
    int MinTokensToCompress,
    int RecentTurnsToProtect,
    bool EnableExternalHeadroomProvider);
```

---

## 6. 持久化策略

### 6.1 Summary Message

压缩成功后新增一条 `MessageEntity`：

```text
Role = "system"
ContentType = "compact_summary"
Content = summary markdown
Source = "context_compaction"
Metadata = { mode, level, beforeTokens, afterTokens, compactedMessageCount }
```

如果现有 UI 历史只展示 `user / agent`，该 message 不直接进入聊天气泡，但可作为系统事件或时间线节点展示。

### 6.2 旧消息标记

被压缩覆盖的旧消息：

```text
CompactedBy = summaryMessageId
```

保留策略：

- 不压缩系统初始 prompt。
- 不压缩最近 3 轮用户和 agent 对话。
- 不拆开 tool call 与 tool result 组。
- 不压缩尚未完成的当前执行轮。
- 不压缩已经被其他 summary 覆盖的消息。

### 6.3 上下文重建

`BuildContextFromDbAsync` 已跳过 `CompactedBy != null`，还需要确保：

- `ContentType = "compact_summary"` 的 summary message 会被纳入上下文。
- summary message 放在近期历史之前。
- 如果存在多个 summary，按 sequence 保留最近一个或保留最近 N 个，第一阶段建议保留最近 1 个。

---

## 7. API 设计

### 7.1 获取健康状态

```http
GET /api/sessions/{sessionId}/context-health
```

返回：

```json
{
  "sessionId": "session-1",
  "usedTokens": 126000,
  "contextWindowTokens": 200000,
  "effectiveWindowTokens": 177000,
  "remainingTokens": 51000,
  "usageRatio": 0.71,
  "state": "Warning",
  "shouldSuggestCompact": true,
  "shouldAutoCompact": false,
  "shouldBlockSend": false
}
```

### 7.2 主动压缩

```http
POST /api/sessions/{sessionId}/compact
```

请求：

```json
{
  "workspaceId": "default",
  "agentId": "agent-1",
  "level": "Full",
  "reason": "manual slash command"
}
```

返回：

```json
{
  "sessionId": "session-1",
  "summaryMessageId": "compact-1",
  "level": "Full",
  "mode": "Manual",
  "beforeTokens": 126000,
  "afterTokens": 42000,
  "compactedMessageCount": 84,
  "summaryPreview": "## 用户目标..."
}
```

---

## 8. SSE 事件

新增事件类型：

```text
context.health
context.compaction.started
context.compaction.completed
context.compaction.failed
```

示例：

```json
{
  "sessionId": "session-1",
  "state": "Critical",
  "usedTokens": 160000,
  "effectiveWindowTokens": 177000,
  "usageRatio": 0.90
}
```

```json
{
  "sessionId": "session-1",
  "mode": "Manual",
  "level": "Full",
  "summaryMessageId": "compact-1",
  "compactedMessageCount": 84,
  "beforeTokens": 126000,
  "afterTokens": 42000
}
```

---

## 9. 前端设计

### 9.1 Slash 命令

在 `CommandPalette` 增加：

```text
id: compact
label: 压缩上下文
shortcut: /compact
description: 总结早期对话，释放上下文窗口
```

用户选择 `/compact` 后：

- 不把 `/compact` 作为普通消息发送。
- 调用 `compactSession(sessionId, request)`。
- Composer 状态显示“正在压缩上下文…”。
- 成功后显示“上下文已压缩，释放 N 条历史消息”。
- 失败后显示错误，并允许用户继续发送普通消息。

### 9.2 自动提示

当收到 `context.health`：

- `warning`：状态条显示可点击提示。
- `unhealthy`：状态详情中展示建议压缩。
- `critical`：显示正在自动压缩，短暂禁用发送。
- `blocking`：发送按钮禁用，并提供压缩动作。

### 9.3 UI 展示边界

压缩摘要不作为普通 assistant 消息展示，避免污染对话流。建议作为系统时间线节点：

```text
上下文已压缩 · 覆盖 84 条历史 · 126K → 42K tokens
```

---

## 10. 自动触发流程

```text
用户发送消息
  -> Runtime 组装上下文
  -> 评估 context health
  -> healthy/warning: 正常执行
  -> unhealthy: 第一阶段仅提示，第二阶段 SessionMemoryCompact
  -> critical: 执行 FullCompact 后再继续
  -> blocking: 阻止普通 LLM 调用，先执行 FullCompact
```

自动压缩必须有熔断：

```text
同一 session 连续自动压缩失败 >= 3 次
  -> 停止自动压缩
  -> 前端提示用户手动处理或新建会话
```

手动 `/compact` 不受自动熔断限制，但同一时刻同一 session 只能有一个 compaction task。

---

## 11. 测试计划

### 后端单元测试

1. `ContextHealthEvaluator` 阈值边界测试。
2. `ContextCompactionService` 成功写入 summary message。
3. 旧消息 `CompactedBy` 正确指向 summary message。
4. 最近 3 轮不会被压缩。
5. 工具调用组不会被拆开。
6. 摘要失败时不修改旧消息。
7. `ContextInputCompressionService` 对小内容、用户消息、最近轮次返回 skip。
8. JSON 工具输出压缩保留 key、错误项、ID/hash 和首尾样本。
9. 日志压缩保留 error/fatal/stack trace，并折叠重复低价值行。
10. artifact hash 只能在同一 workspace/session 取回。

### API 测试

1. `GET /context-health` 返回状态。
2. `POST /compact` 能完成手动压缩。
3. 不存在 session 返回 404。
4. 正在压缩时重复请求返回 409 或复用当前任务状态。

### 前端测试

1. `/` 菜单出现 `/compact`。
2. 选择 `/compact` 后不会发送普通 chat message。
3. 压缩中 composer 显示状态。
4. 成功后出现系统时间线节点。
5. 失败后恢复输入状态。

### Benchmark / 质量测试

1. 对工具输出、日志、文件/search/diff、RAG 样本记录 tokens before/after。
2. 对比启用/关闭 InputCompression 的 cache hit rate。
3. 对比启用/关闭 InputCompression 的任务答案质量、引用命中率和取回次数。
4. 用 Headroom 跑同一批样本作为参考基线，但不把外部节省率作为上线门槛。

---

## 12. 分阶段交付

### Phase 1：手动 FullCompact 闭环

- 新增后端 compaction service。
- 新增 compact API。
- 写入 summary message 和 `CompactedBy`。
- 前端 `/compact` 命令调用 API。
- 增加基础测试。

### Phase 2：自动 critical compact

- 接入 context health 状态机。
- 在 critical/blocking 前自动 FullCompact。
- 增加 SSE 状态反馈和熔断。

### Phase 3：InputCompression 原型

- JSON 工具输出压缩和 artifact 取回。
- 日志压缩和 error/stack trace 保留。
- 文件/search/diff 降噪。
- RAG 块去重和质量门禁。
- Headroom benchmark 对照脚本。
- 接入 `telemetry_metric_events`、`context_layer_metric_events` 和诊断包。

### Phase 4：SessionMemoryCompact 和 MicroCompact

- 使用后台会话记忆替换远期历史。
- 每轮后清理旧工具结果。
- 优化成本和响应速度。

---

## 13. 验收标准

1. 用户输入 `/compact` 后，当前会话历史被压缩，后续回答仍能引用早期关键决策。
2. DB 中生成 `compact_summary` message，旧消息 `CompactedBy` 被正确标记。
3. 后续上下文重建不会重新塞入被压缩旧消息。
4. 压缩失败不会破坏原会话历史。
5. 前端不会把 `/compact` 作为普通聊天消息展示。
6. critical 状态下系统能自动压缩或阻止超限调用。
7. 压缩过程在 SSE、日志和 RuntimeActivity 中可追踪。
8. 工具输出、日志、文件/search/diff 和 RAG 块在进入 LLM 前有可配置压缩策略和跳过原因。
9. 压缩 artifact 可在同一 workspace/session 内通过内部工具取回，跨边界取回失败。
10. 启用 InputCompression 后，`context_layer_metric_events` 能看到 before/after token、压缩率、缓存命中率变化和取回次数。
11. Headroom 对照 benchmark 只能作为参考，正式启用以 Pudding 本地质量门禁为准。
