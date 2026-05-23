# ADR-042 上下文自动压缩与主动 Compact 命令

## 状态

Proposed

## 背景

PuddingAgent 的长会话需要维持“可持续对话”的体验。随着用户消息、Agent 回复、工具调用结果和检索内容不断进入上下文，LLM prompt 会逐步逼近模型上下文窗口。如果没有主动治理，会出现：

- prompt 超长导致 LLM 调用失败；
- 远期历史被临时裁剪，Agent 丢失早期决策；
- 用户无法主动释放上下文；
- 自动恢复只能依赖新建会话或人工重述；
- token 成本和延迟持续升高。

当前系统已有部分基础：

- `ContextPipeline` 已将上下文拆成静态上下文、工具、技能、用户偏好、近期历史、召回记忆、当前消息等层。
- `ContextPipeline` 已有 `None / Gentle / Aggressive` 的预算级别。
- `MessageEntity` 已有 `CompactedBy` 字段。
- `ContextWindowManager.BuildContextFromDbAsync` 已跳过 `CompactedBy != null` 的消息。
- Admin Chat 已有 Slash 命令菜单。
- Session SSE 与 `SessionStateManager` 已能推送运行时事件。

但这些能力尚未形成压缩闭环：系统不会生成持久化压缩摘要，不会标记旧消息，也没有 `/compact` 系统命令。

## 决策

采用“健康状态评估 + 三层压缩 + 用户主动命令 + 持久化摘要”的上下文压缩机制。

### ADR-042-A：上下文健康状态成为 Runtime 一等状态

新增会话级 context health 状态：

| 状态 | 语义 |
| --- | --- |
| `Healthy` | 上下文充足，无需提示 |
| `Warning` | 接近高水位，可建议用户压缩 |
| `Unhealthy` | 上下文不健康，应准备压缩 |
| `Critical` | 必须自动压缩，否则下一轮可能失败 |
| `Blocking` | 阻止普通发送，必须先压缩或新建会话 |

健康状态使用有效上下文窗口，而不是模型声明的最大窗口：

```text
effectiveWindowTokens =
  modelMaxContextTokens
  - min(modelMaxOutputTokens, 20000)
  - safetyBufferTokens
```

默认阈值：

| 状态 | ratio |
| --- | --- |
| Healthy | `< 60%` |
| Warning | `60%-75%` |
| Unhealthy | `75%-85%` |
| Critical | `85%-92%` |
| Blocking | `> 92%` |

### ADR-042-B：采用三层压缩策略

压缩分三层：

1. **MicroCompact**：清理旧工具结果，低成本，每轮后可执行。
2. **SessionMemoryCompact**：用已有会话记忆替换远期历史，中低成本。
3. **FullCompact**：调用 LLM 生成结构化完整摘要，高成本，用于 critical、blocking 和用户主动 `/compact`。

第一阶段只实现 FullCompact 闭环。MicroCompact 和 SessionMemoryCompact 保留接口和状态枚举，后续渐进启用。

### ADR-042-C：压缩结果必须持久化为 summary message

FullCompact 成功后新增一条消息：

```text
Role = "system"
ContentType = "compact_summary"
Source = "context_compaction"
Content = structured markdown summary
```

被覆盖的旧消息设置：

```text
CompactedBy = summaryMessageId
```

上下文重建必须：

- 纳入 `compact_summary`；
- 排除 `CompactedBy != null` 的旧消息；
- 保留最近若干轮原文；
- 不拆开 tool call 与 tool result；
- 不压缩当前未完成执行轮。

### ADR-042-D：`/compact` 是系统命令，不是普通用户消息

Admin Chat 的 Slash 命令面板新增：

```text
/compact - 压缩上下文
```

当用户选择或输入 `/compact`：

- 前端拦截该命令；
- 调用 `POST /api/sessions/{sessionId}/compact`；
- 不向普通 chat message endpoint 发送 `/compact`；
- 不创建普通用户气泡；
- 使用系统时间线节点展示结果。

### ADR-042-E：自动压缩必须有熔断和可观测性

自动 FullCompact 只在 `Critical` 和 `Blocking` 状态触发。

同一 session 连续自动压缩失败达到 3 次后：

- 停止自动压缩；
- 不再反复调用 LLM；
- 前端提示用户手动压缩、新建会话或减少输入。

每次压缩必须记录：

- `RuntimeActivity`;
- session event;
- before/after token;
- compacted message count;
- mode: `Manual | Auto`;
- level: `Full | SessionMemory | Micro`;
- failure reason。

### ADR-042-F：前端展示轻量但明确

上下文压缩不是普通聊天内容。成功后展示为系统事件：

```text
上下文已压缩 · 覆盖 84 条历史 · 126K → 42K tokens
```

状态行根据 context health 显示：

- `Warning`：轻提示，可点击压缩。
- `Unhealthy`：建议压缩。
- `Critical`：显示自动压缩中。
- `Blocking`：禁用普通发送，提供压缩动作。

## 后果

### 正向影响

- 长会话不再只能依赖人工重开。
- 用户可以主动释放上下文窗口。
- 旧消息不会被删除，只是被 summary message 覆盖引用。
- 压缩结果可回放、可诊断、可测试。
- 后续 MicroCompact 和 SessionMemoryCompact 可在同一框架下增量实现。

### 代价

- FullCompact 增加一次 LLM 调用成本和延迟。
- 摘要质量会影响后续 Agent 恢复能力。
- 压缩摘要需要严格提示词和测试样例。
- 前端需要区分系统命令和普通消息。
- DB 历史重建需要正确处理 `compact_summary`。

### 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| 摘要遗漏关键信息 | 使用结构化摘要模板，覆盖目标、决策、文件、错误、下一步 |
| 压缩失败破坏历史 | 失败时不写 `CompactedBy`，保持原历史不变 |
| 自动压缩反复失败 | session 级连续失败熔断 |
| 用户误触 `/compact` | 第一阶段允许直接执行，但结果以系统事件展示；后续可加确认 |
| summary message 污染 UI | 不作为普通气泡展示，只作为系统时间线节点 |

## API 决策

新增：

```http
GET /api/sessions/{sessionId}/context-health
POST /api/sessions/{sessionId}/compact
```

新增 SSE 事件：

```text
context.health
context.compaction.started
context.compaction.completed
context.compaction.failed
```

## 实施顺序

1. 实现 `ContextHealthEvaluator`。
2. 实现 `IContextCompactionService` 的 FullCompact。
3. 新增 compact API 和 context health API。
4. 调整 DB 上下文重建，纳入 `compact_summary`。
5. 前端新增 `/compact` 命令和 API 调用。
6. 接入 SSE 状态反馈。
7. 启用 Critical/Blocking 自动 FullCompact。
8. 后续启用 SessionMemoryCompact 和 MicroCompact。

## 验收标准

1. 输入 `/compact` 不会生成普通用户消息。
2. 压缩成功后 DB 中出现 `compact_summary` message。
3. 被覆盖旧消息的 `CompactedBy` 指向 summary message。
4. 后续上下文重建不包含被压缩旧消息原文。
5. 最近 3 轮对话保持原文。
6. 压缩失败不会修改任何旧消息。
7. `Critical` 或 `Blocking` 状态下自动压缩或阻止普通发送。
8. 前端能显示压缩开始、成功、失败。
9. RuntimeActivity 和 session events 能追踪压缩全过程。

## 相关文件

- `Docs/Tasks/task40-context-compaction.md`
- `Source/PuddingRuntime/Services/ContextPipeline.cs`
- `Source/PuddingRuntime/Services/ContextWindowManager.cs`
- `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- `Source/PuddingMemoryEngine/Entities/MessageEntity.cs`
- `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- `Source/PuddingPlatformAdmin/src/pages/chat/components/CommandPalette.tsx`
- `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- `Source/PuddingPlatformAdmin/src/services/platform/api.ts`

