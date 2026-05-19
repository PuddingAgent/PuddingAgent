# QA-2026-05-19 Architecture Foundation Review

> 状态：REVIEWED_WITH_BLOCKERS
> 范围：待提交改动中的配置、事件系统、执行引擎、会话层、子代理、Fake LLM、Admin Chat 相关基础设施
> 参考：`Docs/07架构/19架构基础设施增强下一步ADR.md`

## 结论

本轮改动已经把 `ADR-019` 的 Phase 1 到 Phase 4 大部分骨架落到了代码中：文件配置、事件 envelope、SQLite 队列、会话 replay/consistency/trace-report、LLM 执行生命周期、Fake LLM 和部分前端简化都有实质进展。

提交前仍建议先处理 3 个阻塞级问题，否则后续子代理和事件诊断会基于不稳定语义继续扩张。

## 阻塞问题

### P0-1 默认 Agent 实例目录 ID 与 manifest ID 不一致

位置：`Source/PuddingAgent/Program.cs:1109-1122`

当前代码用 `instanceId = "general-assistant"` 创建目录：

```text
data/agents/general-assistant/
```

但写入的 manifest 是：

```json
{
  "agentInstanceId": "default.general-assistant-001"
}
```

这会导致后续按 `agentInstanceId` 查找实例配置时出现两个身份：目录身份是 `general-assistant`，配置身份是 `default.general-assistant-001`。这会直接影响 `AgentProfileProvider`、LLM profile 解析、workspace agent ref、后续子代理 workspace 隔离。

建议：二选一统一。

- 推荐：目录也使用 `default.general-assistant-001`。
- 或者：manifest 也改为 `general-assistant`。

验收：新增一个启动 bootstrap 单元测试，断言默认实例目录名与 manifest.agentInstanceId 完全一致。

### P0-2 EventSchemaRegistry 存在重复 eventType，后注册覆盖先注册

位置：

- `Source/PuddingCore/Events/EventSchemaRegistry.cs:106-117`
- `Source/PuddingCore/Events/EventSchemaRegistry.cs:198-201`

`subagent.spawned` 和 `subagent.completed` 被注册了两次，第一次 category 是 `session`，第二次 category 是 `subagent`。构造字典时后者覆盖前者，导致会话 SSE 的 `subagent.spawned` schema 实际变成子代理后台事件 schema。

后果：

- schema 文档显示有 42 种事件，但实际注册表会丢失重复项。
- 同名事件在不同上下文中 required fields 不一致，兼容性检查无法准确判断。
- 事件诊断 UI 后续会展示错误类别。

建议：

- 会话帧继续使用 `session.subagent.spawned` / `session.subagent.completed`，或者保持现有 SSE event name 但 schema key 增加 `scope`。
- 内部事件使用 `subagent.spawned` / `subagent.completed`。
- `EventSchemaRegistry` 初始化时检测重复 key，发现重复直接抛异常，避免静默覆盖。

验收：新增测试验证 schema registry 不允许重复事件类型。

### P0-3 zombie event 回收后未重新 lease，可能造成并发重复处理

位置：`Source/PuddingRuntime/Services/Events/PriorityEventQueue.cs:170-177`

当 `processing` 且 lease 过期的事件被取出时，当前逻辑只把状态改成 `retrying` 并清空 `LeaseUntil`，然后立即返回该事件。它没有把该事件重新设置为 `processing`，也没有给当前消费者新的 lease。

后果：

- 当前消费者会处理一个状态仍为 `retrying`、无 lease 的事件。
- 另一个 dispatcher 可能同时再次 dequeue 同一事件。
- dead-letter/retry 统计会出现难以复现的重复处理。

建议：回收 zombie 后应在同一个锁和事务内立即重新 lease：

```text
processing expired -> retrying/reclaimed -> processing
LeaseUntil = now + LeaseDuration
RetryCount 可按“实际处理尝试”语义决定是否递增，但状态必须是 processing。
```

验收：新增并发测试，构造过期 processing 事件，两次 dequeue 不能同时返回同一 eventId。

## 重要问题

### P1-1 会话 JSONL 双写一致性只比较行数，不能发现内容错位

位置：`Source/PuddingPlatform/Services/SessionStateManager.cs:598-631`

当前 consistency 只比较 SQLite count 和 JSONL line count。行数一致但 sequence、eventType、data 不一致时会误报一致。

建议下一阶段扩展为抽样或全量 hash 校验：

- SQLite 侧计算 `sequenceNum:eventType:dataHash`
- JSONL 侧解析并计算同样签名
- 报告 missing、extra、mismatched sequence

### P1-2 会话 trace-report 解析事件 payload 的字段名不统一

位置：`Source/PuddingPlatform/Services/SessionStateManager.cs:681-812`

`usage` 解析使用 `inputTokens/outputTokens/durationMs`，但系统中已有 `TokenUsageDto` 常用字段是 `PromptTokens/CompletionTokens/TotalTokens` 或 camelCase 变体。字段不统一会导致 LLM 统计长期为空。

建议：建立统一 `SessionDiagnosticEventPayload` DTO，或在 trace-report 中兼容 `promptTokens/completionTokens/totalTokens`。

### P1-3 DirectLlmClient 熔断 key 使用 providerId，但 providerId 依赖 endpoint.StartsWith

位置：`Source/PuddingRuntime/Services/DirectLlmClient.cs`

provider 匹配通过 endpoint 前缀反推，代理、尾部 `/chat/completions`、兼容网关路径变化时容易匹配失败，最终落到 `unknown`，多个 provider 会共享一个熔断器。

建议：`LlmConfig` 或解析结果应显式携带 `ProviderId`，不要从 endpoint 反推。

### P1-4 PuddingWebApiTests 被文件锁阻断，E2E 测试可靠性仍未达标

命令：

```powershell
dotnet test Source\PuddingWebApiTests\PuddingWebApiTests.csproj --no-restore --filter "FullyQualifiedName~FakeLlmControllerTests" --logger "console;verbosity=minimal"
```

结果：失败于 `CS2012` 文件锁，`github.hyfree.GM.dll` 和 `PuddingCore.dll` 被其他进程占用。该问题不是测试断言失败，但会影响自动化验收。

建议：E2E 阶段需要独立输出目录或测试前清理持有锁的后台进程；不要依赖共享 `bin/obj`。

## 已验证

```powershell
dotnet build Source\PuddingAgent\PuddingAgent.csproj --no-restore --nologo
```

结果：通过，0 error，59 warnings。新增 warnings 包含既有废弃 `LlmConfig.ApiKey`、nullable、`System.Security.Cryptography.Xml` 高危包告警等。

```powershell
dotnet test Source\PuddingCoreTests\PuddingCoreTests.csproj --no-restore --filter "FullyQualifiedName~Configuration|FullyQualifiedName~AgentProfileProviderTests" --logger "console;verbosity=minimal"
```

结果：通过，20 tests。

## 下一步建议

提交前建议先修复 P0-1 到 P0-3。修复后可以提交当前大阶段，提交信息建议：

```text
feat: strengthen runtime config events and session observability
```

提交后下一阶段不要继续扩张事件/会话层，优先执行 `ADR-021`：子代理 workspace 与运行归档。该阶段会验证当前 trace/event/session 骨架是否足以支撑多 Agent 透明化。

