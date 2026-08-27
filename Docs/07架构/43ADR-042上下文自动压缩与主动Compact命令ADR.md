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

### ADR-042-I：压缩锁顺序约束（防死锁）

压缩由 `CompactionCoordinator` 提供 per-session 单飞锁（`SemaphoreSlim`），统一拦截工具触发、自动触发、API 触发三个来源，保证同一 session 的压缩互斥执行。为避免与执行路径构成锁环，必须遵守以下硬约束：

1. **压缩锁内禁止获取执行锁**：`ChatExecutionWorker._sessionLocks` 是执行路径的会话锁，既有执行路径按「执行锁 → 压缩」方向加锁；若压缩持锁期间反向获取执行锁，将形成 AB-BA 死锁。因此压缩锁内不得再获取任何执行锁。
2. **压缩锁内禁止等待消息 dispatch**：压缩只读写 DB 与内存历史，不参与消息投递（`SendMessageToSession` / dispatch 回执）；若持锁期间等待投递，而投递链路又反等待压缩锁，会死锁。

对应代码约束见 `Source/PuddingRuntime/Services/CompactionCoordinator.cs` 类注释与 `ContextCompactionService.CompactAsync` 入口。

### ADR-042-J：压缩与冷水合前增量对齐 canonical 转录，空窗口不得生成摘要

CoordinatorCanonical 轮次的用户与 assistant 转录由平台投影器写入 platform DB `ChatMessages`；Runtime 不再保证同步写入 memory DB `Messages` 或 JSONL。因此压缩与任何 memory DB 历史水合都必须先通过同一个 `CanonicalChatTranscriptSynchronizer`，把当前 session 的 `ChatMessages` 增量镜像到 `Messages`，并以平台行派生的稳定 `MessageId` 幂等去重。导入高水位写入 memory session metadata，并以已镜像 `chat_transcript` 消息的稳定 `MessageId` 作为恢复兜底；从最大 platform row Id 之后按 Id 升序有界分页（当前每页 256）续读。高水位必须越过完全无正文/parts 的非语义行，避免每次重扫尾部空行；首次导入或旧数据无法解析高水位时也必须分页追平，禁止每次物化整个会话转录或全部既有 `MessageId`。禁止仅在 `activeMessages.Count == 0` 时导入：只要 memory DB 留有旧 active/summary，该门禁就会永久跳过后续转录，使压缩输入、健康度和冷启动水合停留在旧代。

冷水合必须与压缩共享 session 级 `CompactionCoordinator`，先完成 canonical 同步，再读取 memory DB 快照，避免同步与 `CompactedBy`/摘要提交交错。若 canonical 仓储不可用或同步失败，本次 DB 水合必须 fail-closed：保留当前进程已有历史并继续携带当前用户输入，禁止回退读取已知可能陈旧的 memory DB。UI 本地状态始终不是模型历史源。

受理当前 Turn 时，platform `ChatMessages` 已包含本轮 user 行，但 provider 输入仍必须只有一个当前轮入口。同步器应把该行连同稳定 `turnId/messageId` 镜像进 memory DB 以保证持久性；历史水合则按当前 `turnId`（缺失时按 `messageId`）排除整轮，随后只由 `BuildCurrentUserChatMessage` 追加带 hash 围栏和 typed parts 的当前输入。禁止同时把无围栏 DB 行和围栏当前输入交给模型。

运行中内存历史承载尚未由 Platform 投影完成的 assistant/tool 轮次，因此不能因为 `preferDbContextWindow=true` 或 DB 消息数不同，就在流式执行结束时用 DB 快照覆盖活跃历史。仅冷启动空历史可直接采用 DB 快照；自动压缩完成后的 DB 刷新必须把水合出的摘要/历史与当前 live Turn 尾部合并。live 尾部的起点必须是带 64 位输入 hash 且 opening/closing 配对的当前轮围栏；围栏缺失或畸形时不得把“最后一条历史 user”提升为当前轮。否则 assistant 终态提交前的时间窗会把刚完成的回答再次回退为“只有旧历史或当前 user”，异常投影还可能重新激活旧指令。纯图片/typed-parts 消息也属于 canonical 转录：增量扫描不得因文本为空而越过，内容 hash 同时覆盖文本与 `ContentPartsJson`。

滚动摘要必须由尚未覆盖的新原文或尺寸驱逐原文驱动。若没有可压缩候选，或待压缩集合与尺寸驱逐克隆都只含上一代 `compact_summary`（包括超过逐字保留字节阈值的旧摘要），本次压缩必须 no-op：不调用摘要生成器，不新增 summary/coverage manifest，不更新 `CompactedBy`，诊断中的 compacted count 固定为 0，也不触发 history invalidation。为满足最小摘要输入而补读时，只能读取 `CompactedBy == null` 的 active 原文；已覆盖原文的语义已经属于对应摘要，禁止再次回流形成重复证据和套娃压缩。

共享同步器消除了“上次压缩后产生新轮次、Core 在下次压缩前重启”造成的冷启动缺口。持续的 Agent History 投影仍可作为降低水合延迟的后续优化，但不能替代水合入口的同步正确性门禁。

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
| memory DB 留有旧消息导致新转录不再导入 | 每次压缩和 DB 历史水合前按稳定 MessageId 与 durable platform Id 高水位分页镜像当前 session 的 platform `ChatMessages`；两条入口共享 session 锁和同步器，回归覆盖非空 DB、冷启动与 after-Id 续读 |
| canonical 同步失败后读取陈旧 memory DB | DB 历史水合 fail-closed，保留进程内历史和当前用户输入并记录失败；禁止继续读取陈旧快照 |
| 当前 user 同时从 DB 与当前轮入口注入 | 镜像时保存稳定 `turnId/messageId`；水合排除当前 Turn，provider 前只由当前轮围栏路径追加一次 |
| assistant 尚未投影时 DB 快照覆盖 live Turn | 非空活跃历史不接受 pre-projection DB 覆盖；自动压缩刷新时仅合并通过完整 hash 围栏验证的 live 当前轮尾部，缺失围栏时禁止提升最后一条历史 user |
| 纯图片消息因空文本被增量扫描跳过 | after-Id 扫描包含所有行，仅完全无文本且无 typed parts 的行不生成上下文消息；hash 同时覆盖正文与 parts |
| 无新原文时重复压缩旧摘要 | summary-only/无可压缩窗口直接 no-op，不写摘要、coverage 或 `CompactedBy` |

## API 决策

新增：

```http
GET /api/sessions/{sessionId}/context-health
POST /api/sessions/{sessionId}/compact
```

新增 SSE 事件：

```text
context.health
context.compaction.requested
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
13. memory DB 已有旧 active/summary 时，platform `ChatMessages` 的新增轮次仍会从 durable platform Id 高水位之后按 256 条有界分页幂等导入，且不全量扫描会话转录或既有 MessageId；压缩和冷启动水合都能看到最新完整 user/assistant/typed-parts 转录，重复水合不产生重复消息。
14. 仅剩旧摘要（包括超过逐字保留阈值的旧摘要）或没有可覆盖原文时，压缩结果及 diagnostics 均返回 `CompactedMessageCount=0`，且不调用摘要生成器、不新增摘要或 coverage manifest。
15. 当前 user 已存在于 platform DB 时，水合结果按稳定 `turnId/messageId` 排除当前 Turn，最终 provider 历史中只有一个带当前轮 hash 围栏的 user 输入。
16. Streaming 完成但 assistant 尚未投影时，后处理裁剪保留 live assistant/tool 尾部；自动压缩发生时，DB 摘要只与通过完整 hash 围栏验证的 live 当前 Turn 合并，不能回退到 pre-projection 快照，也不能把无围栏的最后一条历史 user 提升为当前 Turn。

## 相关文件

- `Docs/Tasks/task40-context-compaction.md`
- `Source/PuddingRuntime/Services/ContextPipeline.cs`
- `Source/PuddingRuntime/Services/CanonicalChatTranscriptSynchronizer.cs`
- `Source/PuddingRuntime/Services/ContextWindowManager.cs`
- `Source/PuddingRuntime/Services/ContextAssemblyService.cs`
- `Source/PuddingMemoryEngine/Entities/MessageEntity.cs`
- `Source/PuddingPlatform/Controllers/Api/SessionEventsController.cs`
- `Source/PuddingPlatform/Controllers/Api/ChatApiController.cs`
- `Source/PuddingPlatformAdmin/src/pages/chat/components/CommandPalette.tsx`
- `Source/PuddingPlatformAdmin/src/pages/chat/hooks/useChatState.ts`
- `Source/PuddingPlatformAdmin/src/services/platform/api.ts`
