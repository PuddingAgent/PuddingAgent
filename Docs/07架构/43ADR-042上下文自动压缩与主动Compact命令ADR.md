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

### Headroom 项目研究结论

2026-06 对 [chopratejas/headroom](https://github.com/chopratejas/headroom) 的研究结论是：Headroom 与 Pudding 的上下文治理目标高度相关，但不应在第一阶段直接替代 Pudding 原生上下文管线。

Headroom 的核心思想包括：

- 在 LLM 调用前压缩工具输出、日志、文件、检索块和历史消息，减少输入 token。
- 通过 `CacheAligner` 稳定 system/tools/早期消息前缀，提高 OpenAI/Anthropic/DeepSeek 这类前缀缓存的可命中性。
- 通过 `ContentRouter` 按内容类型选择 JSON、日志、diff、代码、文本等压缩策略。
- 通过 CCR（Compress-Cache-Retrieve）保存原文，只把压缩片段和取回标记送入 LLM，模型需要细节时再调用取回工具。
- 提供 Library、Proxy、MCP Server、Agent wrapper 等接入形态，许可证为 Apache-2.0，适合作为实现参考或可选外部适配器。

对 Pudding 的适用判断：

| 场景 | 适用性 | 设计判断 |
| --- | --- | --- |
| 大型 JSON 工具输出 | 高 | 可优先实现结构保留、异常保留、样本保留和原文取回 |
| 构建日志、运行日志、测试日志 | 高 | 可压缩重复行、保留 error/fatal/stack trace/时间边界 |
| 文件读取、搜索结果、diff | 中高 | 需要区分“用户正在编辑/审查的活跃代码”和“背景证据” |
| RAG 块 | 中 | Headroom README 将 RAG 纳入范围，但限制页说明部分 RAG 文档上下文可能 passthrough；Pudding 必须以本地 eval 验证召回质量后再启用 |
| system prompt 和用户原始意图 | 低 | 不做语义压缩，只做动态字段后移、顺序稳定和空白归一化 |
| 最近几轮对话和当前执行轮 | 低 | 默认保护，避免压缩破坏任务连续性 |

结论：Pudding 应吸收 Headroom 的“前置输入压缩 + 前缀稳定 + 可逆取回 + 结构化指标”模式，但实现上先建设原生 `ContextInputCompression` 能力；Headroom 可作为可选压缩引擎、开发期代理或对照基准，而不是当前生产默认依赖。

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

### ADR-042-G：LLM 前置输入压缩网关

**决定**：在 `ContextPipeline` 组装完成、调用 LLM 之前增加“输入压缩网关”，专门处理工具输出、日志、文件片段和 RAG 块，不替代 FullCompact 的会话摘要闭环。

该网关的职责：

1. **分类**：按 layer/source/contentType 标记 `tool_output`、`log`、`file_excerpt`、`diff`、`search_result`、`rag_chunk`、`history`。
2. **前缀稳定**：确保静态 system prompt、工具定义、Agent 模板和稳定记忆位于 prompt 前部；时间戳、session id、随机 trace id、当前状态摘要等动态字段后移。
3. **局部压缩**：只压缩大体量、可恢复的证据块；短内容、用户消息、当前编辑代码、最近工具结果默认跳过。
4. **可逆取回**：压缩前原文写入 session/workspace 作用域的本地存储，压缩文本中携带短 hash 和取回提示。
5. **取回工具**：向 LLM 暴露受权限约束的内部工具，例如 `context.retrieve_artifact(hash)`，只能取回当前 workspace/session 可见原文。
6. **可观测指标**：记录 before/after tokens、压缩率、跳过原因、取回次数、取回成功率、缓存命中变化、是否影响回答质量。

与现有三层压缩的关系：

| 能力 | 触发时机 | 是否持久化为 summary | 主要目标 |
| --- | --- | --- | --- |
| InputCompression | 每次 LLM 调用前 | 否，原文存 artifact/diagnostics | 降低本轮输入 token，提高前缀缓存命中 |
| MicroCompact | 每轮后 | 可选 | 清理旧工具结果进入后续上下文的体积 |
| SessionMemoryCompact | warning/unhealthy | 是 | 用会话记忆替换远期历史 |
| FullCompact | critical/blocking/`/compact` | 是 | 持久化长会话摘要，释放上下文窗口 |

### ADR-042-H：Headroom 集成边界

**决定**：不把 Headroom 作为 Pudding V1 的硬依赖。后续可提供三种可选路径：

1. **对照基准**：用 Headroom CLI/Proxy 跑 Pudding 的工具输出、日志、RAG 样本，建立节省率和答案一致性基线。
2. **外部引擎适配器**：在开发环境提供 `HeadroomCompressionProvider`，通过本地 proxy/MCP/library 调用 Headroom；失败时必须 passthrough 原文。
3. **原生实现吸收**：把已验证的 JSON/log/diff 压缩策略移植为 .NET 原生组件，直接接入 Pudding 的 telemetry、SQLite、权限和诊断包。

默认策略是原生实现优先，外部 Headroom 适配器 opt-in。原因：

- Pudding 的数据根目录、SQLite、诊断时间线和权限模型需要统一治理。
- Headroom 高级能力可能引入 Python/Rust/ONNX/HuggingFace 等运行时资产，不适合作为单文件默认用户路径。
- 压缩质量必须接受 Pudding 自己的 benchmark、RAG 命中率和工具任务验收，而不能直接继承外部项目的节省率声明。
- Headroom 默认/可选遥测、CCR TTL、多 worker 存储等运行参数需要明确配置后才可进入受控环境。

## 后果

### 正向影响

- 长会话不再只能依赖人工重开。
- 用户可以主动释放上下文窗口。
- 旧消息不会被删除，只是被 summary message 覆盖引用。
- 压缩结果可回放、可诊断、可测试。
- 后续 MicroCompact 和 SessionMemoryCompact 可在同一框架下增量实现。
- 大型工具输出、日志和 RAG 证据在进入 LLM 前可以被局部压缩，降低单轮 prompt 成本。
- 静态前缀更稳定，能与 ADR-018/ADR-043 的缓存统计闭环形成因果验证。
- 可逆取回避免把“节省 token”变成不可诊断的信息丢失。

### 代价

- FullCompact 增加一次 LLM 调用成本和延迟。
- 摘要质量会影响后续 Agent 恢复能力。
- 压缩摘要需要严格提示词和测试样例。
- 前端需要区分系统命令和普通消息。
- DB 历史重建需要正确处理 `compact_summary`。
- 输入压缩网关会增加一次内容分类、hash、存储和指标记录开销。
- 可逆取回工具需要防止跨 workspace/session 泄露原始工具输出。
- 如果压缩策略误判，LLM 可能少看关键证据；因此必须 fail-open 并保留跳过原因。

### 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| 摘要遗漏关键信息 | 使用结构化摘要模板，覆盖目标、决策、文件、错误、下一步 |
| 压缩失败破坏历史 | 失败时不写 `CompactedBy`，保持原历史不变 |
| 自动压缩反复失败 | session 级连续失败熔断 |
| 用户误触 `/compact` | 第一阶段允许直接执行，但结果以系统事件展示；后续可加确认 |
| summary message 污染 UI | 不作为普通气泡展示，只作为系统时间线节点 |
| 输入压缩删除关键证据 | 默认保护用户消息、最近轮次、当前执行轮和活跃代码；异常/error/fatal/stack trace 永远保留 |
| CCR 取回失败 | 压缩 artifact 使用 session/workspace 作用域本地存储；过期/缺失时向模型返回明确错误并记录指标 |
| 外部 Headroom 依赖不可用 | Headroom 只作为 opt-in provider；任何异常必须 passthrough 原文 |
| RAG 压缩影响召回质量 | 先通过 Pudding eval 验证 answer quality、citation hit 和 retrieval fallback，再按知识库/助手开关启用 |

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
8. 增加 InputCompression 原型：JSON 工具输出、日志、文件/search/diff、RAG 块分类与指标。
9. 用 Headroom 作为对照基准，评估本地样本压缩率、回答一致性和缓存命中率变化。
10. 后续启用 SessionMemoryCompact 和 MicroCompact。

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
10. 大型工具输出/日志进入 LLM 前能记录 before/after tokens、压缩率、跳过原因和 artifact hash。
11. LLM 能通过受限内部工具取回被压缩 artifact；跨 session/workspace 取回必须失败。
12. 对同一测试集，启用输入压缩后缓存命中率不下降，答案质量通过 Pudding benchmark 门禁。

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
