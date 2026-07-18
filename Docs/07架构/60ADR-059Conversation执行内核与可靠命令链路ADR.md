# ADR-059：Conversation 执行内核与可靠命令链路

状态：已实施（2026-07-18）

关联：ADR-056、ADR-057

## 1. 目标

为 Harness Agent 建立一条可诊断、可恢复、不会静默丢失终态的前后端主链路：

```text
Frontend command
  -> POST /api/v1/conversations/{conversationId}/turns
  -> atomic acceptance
  -> leased execution
  -> fenced journal
  -> durable Conversation Event
  -> resumable SSE
```

浏览器是否在线不影响 Agent 执行与事件持久化。浏览器恢复后以 Conversation
sequence 为游标补读事件，而不是依赖 Controller 内存 Channel。

## 2. 问题根因

旧链路的问题不是单个 500 或单个“发送中”状态，而是写入权威重叠：

- Controller 同时承担协议转换、配置解析、执行调度和响应聚合。
- 旧 Command Store 同时公开受理、租约、终态等操作，与新的
  AcceptanceStore、LeaseStore、Journal 职责重叠。
- Worker 子任务异常可能脱离观察，留下永久 Running。
- 输出批次写入失败后没有被带入终态事务，造成已生成内容丢失。
- Control Inbox 在控制实际生效前确认，Steering 还没有 Runtime 消费者却对外表现为已受理。
- 前端发送路由和 SSE 订阅可能使用不同 Conversation ID。
- DI 生命周期错误直到首个用户请求才暴露为 500。

## 3. 决策

### 3.1 唯一外部消息入口

唯一 Chat 命令入口：

```http
POST /api/v1/conversations/{conversationId}/turns
X-Workspace-Id: {workspaceId}
```

请求只包含用户意图与稳定引用：

```json
{
  "clientRequestId": "uuid",
  "clientMessageId": "uuid",
  "recipients": {
    "type": "agent",
    "agentIds": ["agent-id"]
  },
  "content": [
    { "type": "text", "text": "hello" }
  ]
}
```

命令载荷不得包含 `llmConfig`、Provider、Profile、Model、Tool、Skill、密钥、
SSE Channel 或 Trace 配置。Worker 领取命令后，只调用
`IAgentRuntimeProfileResolver` 组装 Agent、模板、LLM Provider、Tool 与 Skill；
`IAgentExecutionSnapshotFactory` 只消费已经解析的 Profile 并冻结不可变快照，
不得再次读取配置文件、数据库或 Skill 存储。

旧 `/api/workspaces/{workspaceId}/chat/message` 和 `ChatApiController` 删除，
不保留兼容翻译层。

### 3.2 写入权威

| 事实或状态 | 唯一写入权威 | 其他组件 |
|---|---|---|
| Message + Batch + Turn + Command + `turn.accepted` | `IConversationAcceptanceStore` | 只读 |
| Run 领取、续租、释放、fencing token | `IExecutionLeaseStore` | 不直接改租约 |
| 输出事件与 Turn/Run/Command 终态 | `IExecutionJournal` | 不直接提交终态 |
| Cancel/Control + 对应事件 | `IExecutionControlService` | Inbox 只读/确认 |
| Command 稳定执行引用 | `IExecutionCommandReader` | 不提供写方法 |
| Agent/Template/LLM/Tool/Skill 配置解析 | `IAgentRuntimeProfileResolver` | Controller、Worker、Runtime 不自行读取 |
| 不含秘密的执行快照 | `IAgentExecutionSnapshotFactory` | 只消费已解析 Profile |

`IChatCommandStore` 已删除。它原有的保存、领取、续租和终态方法均与上述
权威冲突，不能继续作为“备用路径”保留。

### 3.3 HTTP 边界

`ConversationTurnsController` 只负责：

1. 认证并读取 route/header/body。
2. 校验 ID 长度、显式 Agent 收件人和非空文本。
3. 映射 `SubmitTurnCommand`。
4. 返回 `202 Accepted`。

当前只支持显式 Agent ID 和文本内容。`@all`、图片、文件等未完成能力必须
返回明确错误，不能静默降级或丢弃。

### 3.4 Worker 与 Journal

- `ChatExecutionWorker` 必须显式观察所有并发任务。
- Coordinator 之外逃逸的异常由 Worker 尝试写入 fenced
  `turn.failed`，并同时关闭 Turn、Run、Command。
- 非终态输出批次只有在 `AppendOutputAsync` 成功后才能从未提交缓冲区移除。
- Cancel、协议错误和基础设施错误必须把未提交输出与终态放入同一 Journal
  事务。
- fencing 不匹配或 Turn 已终态时，基础设施兜底不得覆盖新 Worker 的结果。
- 进程关停主动释放或租约过期回收时，必须在同一数据库事务中完成
  `Run -> lease_lost`、`Command -> pending`、`Turn running -> accepted`；不能只回收
  Command，留下无法再次 `StartRun` 的 Running Turn。

### 3.5 LLM 路由身份

- `ProviderId`、`ProfileId`、`ModelId` 是三个独立字段，从 Agent Profile 一直传递到
  `LlmInvocationService`。
- Runtime 不得从 `Endpoint`、`KeyVaultId`、`ApiKey` 或 `ModelId` 猜测
  Provider/Profile。
- `LlmConfig` 是执行期连接参数，不是命令载荷，也不是路由身份。
- 快照哈希包含 Provider/Profile/Model 及能力引用，但排除 API Key、KeyVault
  明文结果和 Skill 临时下载 URL。

### 3.6 Control

- `IControlInbox.ReadPendingAsync` 不修改状态。
- Cancel 只有在 Runtime 已停止且终态提交成功后才 `AcknowledgeAsync`。
- Acknowledge 必须校验 Conversation、Turn、Run、Worker 和 fencing token。
- Steering 的 Runtime 消费器尚未实现，因此端点返回 `501`，不写入“永远不会
  消费”的伪命令。

### 3.7 前端

- Chat 页面和 Workspace Studio 都使用 canonical Turn API。
- 创建或解析真实 Conversation ID 后，才能发送并订阅该 Conversation 的 SSE。
- `clientRequestId` 与 `clientMessageId` 在前端生成，并在重试时保持不变。
- Outbox 保存完整的 canonical 重放信息：
  `workspaceId + conversationId + agentIds + text + 两个稳定 ID`。
- 乐观消息必须在任何异步 Outbox 操作之前同步绑定到发起发送的 Conversation，
  避免用户切换会话后消息被追加到错误会话。
- `202` 返回的服务端 Turn ID 是持久身份；前端必须在重启 SSE 前原子迁移乐观
  Turn ID、Turn 状态及所有以 Turn ID 为键的缓冲，不能只更新
  `messageId -> turnId` 映射。
- `202` 只表示受理；完成、失败、取消均以持久 SSE 终态为准。
- 断线补读使用 `/api/sessions/{conversationId}/events?from={exclusiveCursor}` 读取
  `conversation_events`；不能调用旧 SessionStateManager `/replay` 读取另一套事实源。
- live SSE 与补读 API 返回相同 canonical envelope；前端投影边界负责保留
  `turnId/messageId/sequence` 并展开 `payload`，两条路径不得各自定义事件形状。
- `JsonElement` 进入异步持久事件前必须取得所有权（`Clone()`），不得保存即将释放的
  `JsonDocument.RootElement`。
- Acceptance 为每个 Command 分配的 assistant `messageId` 必须贯穿
  `turn.started`、输出事件、终态事件和 ChatMessages 投影；Projector 不得重新生成
  一套无法关联的消息身份。
- 投影调度由独立 `ConversationProjectionWorker` 根据持久化
  `conversation_heads > projection_checkpoint` 发现工作。不得把 Projector
  fire-and-forget 绑定到某一个 Event Store 写方法，因为 Acceptance、Journal 和
  Control 都是合法事件写入者，且进程可能在 commit 后、调度前崩溃。
- SSE 是低延迟视图，ChatMessages 是异步物化视图。历史对账必须单调：较旧物化快照
  不得清空或替换已由持久终态事件确认的前端回答；等物化追平后再用相同
  `messageId/turnId/commandId` 收敛。

### 3.8 Composition Root 与健康检查

- 所有环境启用 `ValidateScopes` 和 `ValidateOnBuild`。
- `PlatformDbContext` 由 singleton `IDbContextFactory` 创建，singleton 服务
  不捕获 scoped DbContext。
- `/health/live` 只报告进程存活。
- `/health/ready` 检查数据库、Submit Handler 和 Execution Coordinator。
- `/health` 保留版本信息，但 readiness 失败时返回 `503`。

## 4. 验收条件

1. 新 Turn 端点返回 202 和
   `conversationId/messageId/turnIds/commandIds/acceptedSequence`。
2. 相同 `(workspaceId, clientRequestId)` 重试返回相同受理结果，不重复落库。
3. 一次受理事务同时存在 Message、Batch、Turn、Command 和
   `turn.accepted`。
4. 前端发送 Conversation ID 与 SSE 订阅 ID 相同，禁止发送字面量 `main`。
5. Worker 异常最终产生持久 `turn.failed`，或因 fencing 失效明确拒绝旧 Worker
   写入；不得永久 Running。
6. 终态前尚未提交的输出不会丢失，也不会重复写入。
7. 未实现的 Steering/多媒体/广播返回明确错误，不产生伪受理。
8. Host 在 DI 图不合法时启动失败；ready 探针在执行链不可用时返回 503。
9. LLM 调用日志中的 Provider 必须是配置 ID（例如 `deepseek`），不得出现服务商 URL
   或 KeyVault ID。
10. Worker 关停或租约过期后，原 Turn 可被下一次租约重新启动，不遗留永久 Running。
11. POST 确认后，前端 Turn ID 与 Event Store 的 `turnId` 一致；live SSE 和 replay
    都能将 `message.content.appended + turn.completed` 投影到同一个 Turn。
12. `turn.completed.messageId` 等于 Command 的 assistant `message_id`，投影后的
    ChatMessages 保留相同 `messageId/turnId/commandId`；终态后延迟历史对账不会让
    已显示回答消失。
13. 无论事件由 Acceptance、Journal、Control 或 EventStore 写入，Projection Worker
    都能仅依据持久 head/checkpoint 在重启后追平 ChatMessages。

## 5. 后续工作

1. 使用数据库级原子 head 分配替换 Acceptance/Control 中的 read-modify-write，
   增加同一 Conversation 并发受理压力测试。
2. 实现 Runtime Steering 消费器后再开放 Steering 端点，并验证
   `created -> applied -> acknowledged`。
3. 将视觉、文件输入建模为持久 ContentPart/Artifact 引用并进入 Snapshot，
   不恢复自由格式 metadata 透传。
4. 增加 Worker crash、租约过期、SSE 断线重连、浏览器离线 Outbox 的端到端故障注入测试。
5. 将当前过大的 `useChatState` 迁移到 Conversation Store/Connection Manager，
   保持单一状态写入权威。
