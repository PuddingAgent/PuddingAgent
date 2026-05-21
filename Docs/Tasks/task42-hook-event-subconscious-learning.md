# task42 — Hook 事件与潜意识学习闭环

> **创建日期：** 2026-05-20
> **优先级：** P0.8（事件系统与记忆系统闭环）
> **状态：** ✏️ 设计中
> **依赖：** task34-event-bus-and-subscription、task38-subconscious-memory-engine、task41-hook-system
> **ADR：** [28ADR-027Hook事件潜意识学习闭环ADR](../07架构/28ADR-027Hook事件潜意识学习闭环ADR.md)

---

## 任务目标

把 Agent Loop 完成后的潜意识学习从“Hook 直接写 Channel”改造为“Hook 发布标准事件，事件系统排队派发，潜意识消费者持久化 Job，后台空闲执行学习”的闭环。

目标链路：

```text
Agent done
  -> AgentLoopEventPublisherHook
  -> IInternalEventBus
  -> IPriorityEventQueue
  -> EventDispatcher
  -> SubconsciousEventHandler
  -> SubconsciousJobs
  -> SubconsciousWorkerService
  -> ISubconsciousOrchestrator
  -> IMemoryLibrary
```

## 实施范围

### Phase 1：事件化 Hook

- 新增 `AgentLoopCompletedPayload`。
- 新增 `AgentLoopEventPublisherHook`。
- 在 DI 中注册该 Hook。
- 保留现有 `SubconsciousConsolidationHook` 作为短期兼容路径，但新路径默认启用。

### Phase 2：潜意识事件消费者

- 新增 `SubconsciousEventHandler : IEventHandler`。
- 订阅 `agent.loop.completed`。
- 把事件 payload 转换为 `SubconsciousJob`。
- Handler 只入队，不执行 LLM。

### Phase 3：持久化潜意识 Job 队列

- 新增 `ISubconsciousJobQueue`。
- 新增 `SubconsciousJobEntity`。
- 新增 `SubconsciousJobQueue` 实现。
- 支持幂等入队、lease、retry、dead_letter、stats。

### Phase 4：空闲 Worker

- 改造 `SubconsciousWorkerService`，从持久化 Job 队列消费。
- 新增 `IRuntimeIdleSignal`。
- 忙碌时不 lease 新 Job。
- 同一 Workspace 串行处理潜意识 Job。

### Phase 5：记忆来源指针

- `ConsolidateAsync` 的 Job 输入带 `SourceEventId`。
- 写入 `IMemoryLibrary` 时创建 `source-session` / `source-event` 指针。
- `SubconsciousJobLogs` 记录 source event、job id、结果摘要。

### Phase 6：诊断与验收

- 事件诊断能查到 `agent.loop.completed`。
- 潜意识诊断能查到 pending/running/completed/dead_letter Job。
- 增加单元测试和一个端到端 smoke。

## 验收标准

1. Agent done 后发布 `agent.loop.completed`。
2. `agent.loop.completed` 能进入 `IPriorityEventQueue`。
3. `SubconsciousEventHandler` 能创建持久化 Job。
4. 同一会话重复 done 事件不会重复创建未终态 Job。
5. Worker 在空闲时调用 `ISubconsciousOrchestrator.ConsolidateAsync`。
6. Worker 忙碌时积压 Job，不阻塞主对话。
7. Job 失败后进入 retry，超过上限进入 dead_letter。
8. 记忆图书馆条目可以回溯到原始 session 和 source event。
9. 不引入 ZeroMQ 作为 V1 主路径。

## 不做

- 不自动修改 Skill 文件。
- 不重写事件总线。
- 不新增跨进程事件传输。
- 不让 Hook 直接执行潜意识 LLM。

