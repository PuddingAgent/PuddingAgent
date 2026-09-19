# 只读根因定位：调度器批量派发已终态卡

- 缺陷卡：`be2608ee21bb4018a19ade685e99f5b4`（P2）
- 仓库：`E:\github\AgentNetworkPlan\PuddingAgent`（branch `master`）
- 方式：**纯静态只读 + 平台库只读 SQL 探针**（`sqlite3 mode=ro`）。未改任何产品文件、未做任何 git/看板写操作。
- 复现事件：`Docs/Reports/G92-1-验收证据与缺口矩阵-2026-09-17.md` §51（单张 `4d152325`，归档后 35s）、§55（批量 5 张，其中 4 张归档后 34s）。

---

## 0. 结论先说（一句话）

**这不是"筛选谓词漏了终态条件"，而是"终态判据与 Agent 实际收到卡的时刻之间有一段几十秒的 TOCTOU 空窗"。**

卡在**消息发出时是合法的 `Reserved`**（平台把 `Ready → Reserved → Assigned` 推进完并落了 outbox），Task Orchestrator 的 `task_instruction` 消息也确实发出去了；但这条消息是**持久化投递**，它在投递队列里等了 **34–240 秒**才被交给 Agent 去开一个 turn。等它落地时，卡早就在这一段里走完 `Assigned → InProgress → Completed → Archived` 了。

平台三重派发判据（候选扫描 / 事件意图 / 发送时刻）**全部显式排除了终态**，且工作正常；**唯独"消息交给 Agent、即将起 turn"这一跳，从头到尾没有重读过卡的任何状态**。这就是缺口。

---

## Q1 决策点在哪

### 1.1 派发链有四跳，前三跳都读卡，第四跳不读

| # | 跳 | 代码位置 | 读卡时刻 / 来源 |
|---|---|---|---|
| ① | **调度器候选扫描** | `Source/PuddingPlatform/Services/Scheduling/TaskAutoDispatchEvaluator.cs:186-205`<br>`EvaluateAsync` 的 `FromSqlInterpolated` 查询 | 扫描轮开始时**直读 `workspace_tasks`**（`AsNoTracking`，无缓存层、无投影层） |
| ② | **事件驱动意图评估** | `Source/PuddingPlatform/Services/Scheduling/TaskSchedulingCoordinator.cs:178-215`<br>`ProcessBatchAsync` 步骤 2 | 每个 intent 处理前**重读 `workspace_tasks`**（`db.WorkspaceTasks.AsNoTracking()`） |
| ③ | **手工/看板派发发送点** | `Source/PuddingPlatform/Services/Tasks/TaskDispatcher.cs:172-207`<br>`DispatchOneAsync` | `messages.SendAsync` **紧邻之前**调 `taskStore.GetTaskAsync(...)` 读一次 |
| ④ | **Agent 实际收到卡 / 起 turn**（← **缺口**） | `Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs:308`<br>`TryClaimAndDispatchAsync`（claim `:512`、busy-hold `:598-628`、batch claim `:536`、merge `:570`、dispatch `:670`） | **从不读卡**。只读投递行 + `claimed.Metadata` |

**决策点定义（本次事故口径）**：决定"把哪张卡以 `task_instruction` 形式交给哪个 Agent 去执行"的最后一次判断发生在 **② 消息封套生成** + **③ 发送前 fence**；而决定"Agent 现在真的去执行这张卡"的判断发生在 **④ `MessageDeliveryDispatcher` 的 claim/admission 门**——这一门只看"Agent 忙不忙"，不看"卡还活着吗"。

### 1.2 事件链的精确时刻（库内实测，非引用文档）

**事件 A（单张 `4d152325a4574b959d6fdc03552581d8`）**

| 时刻 (UTC) | 事实 | 来源 |
|---|---|---|
| 01:47:38.894 | `task.reserved`，assignment `023a5906ad9b42ce8130a9a922aa706b` | `task_events.seq=5` |
| 01:47:38.894 | `task_dispatch_outbox.id=61` 插入（`idempotency_key=task:{taskId}:assign:{assignmentId}`） | outbox #61 |
| **01:47:40.198** | **`room_messages` 落 `system/task-orchestrator` 的 `请完成以下任务：…` 全文卡面** | `room_messages` |
| **01:47:40.331** | **outbox #61 `sent_at`（`TaskDispatcher` 发送完成）** | outbox #61 |
| 01:47:40.285 | `message_deliveries.id=5613` 创建 | deliveries |
| 01:47:47.034 | `task.accepted`（agent 已在做） | `task_events.seq=7` |
| **01:47:51.522** | **`task.completed`** | `task_events.seq=8` |
| **01:48:02.716** | **`task.archived` → 终态** | `task_events.seq=9` |
| 01:49:09.260 | 投递被 busy-defer 一次（`defer_count=1`，`available_at`） | deliveries #5613 |
| 01:50:01.305 | 投递 `ack_at`（= turn 正常结束后批量 ack） | deliveries #5613 |
| ~01:48:37（文档观测） | Agent 侧"收到派发"（turn 起） | 矩阵 §51 |

⇒ **消息创建 01:47:40 → 卡归档 01:48:02 → 消息落地 01:48:37~01:50:01。发送合法、落地过期。**

**事件 B（批量 5 张 = §55）**

| 卡 | Ready | Reserved | **sent_at** | Completed | Archived |
|---|---|---|---|---|---|
| `ef7d6378`（**正常**） | 09-18 04:16 | 01:53:16.836 | 01:53:20.226 | — | — |
| `104fe9c0` | 01:55:42.159 | 01:55:45.777 | 01:55:50.365 | 01:56:07.349 | **01:56:12.182** |
| `77f4141b` | 01:55:42.159 | 01:55:45.829 | 01:55:50.425 | 01:56:07.430 | **01:56:12.384** |
| `d0ac9f71` | 01:55:42.169 | 01:55:45.865 | 01:55:50.477 | 01:56:07.481 | **01:56:12.416** |
| `ec50aeb0` | 01:55:42.221 | 01:55:45.895 | 01:55:50.505 | 01:56:07.541 | **01:56:12.452** |

`message_deliveries` 最终态（**5 条在 4ms 内被一起 ack**）：

```
5615 (ef7d6378) created 01:53:20.207  ack 01:57:19.649   attempts=1 defer=0
5617 (104fe9c0) created 01:55:50.346  ack 01:57:19.653   attempts=1 defer=0
5618 (77f4141b) created 01:55:50.403  ack 01:57:19.650   attempts=1 defer=0
5619 (d0ac9f71) created 01:55:50.434  ack 01:57:19.652   attempts=1 defer=0
5620 (ec50aeb0) created 01:55:50.498  ack 01:57:19.654   attempts=1 defer=0
```

⇒ **"单次批量派发 5 张卡"= 一次 claim + 一次 batch-claim 合并**：`inbox.ClaimBatchAsync(batchRequest, maxBatch: 9)`（`MessageDeliveryDispatcher.cs:536`）把同 target 的排队消息一并取出，`mergedContent` 用 `"[批 {i+1}/{count}] From: …"` 拼成一段文本（`:570-573`，正是文档里"批 1/5 ~ 5/5"的来源）。
⇒ 4/5 张卡在**消息发出后 22 秒**归档，在**归档后 ~34 秒（turn 起）**才被 Agent 看到。
⇒ `ack_at` 是 **turn 成功结束后**写的（`:750-765` 在 `result.IsSuccess` 分支里对整批 ack），所以 `ack_at - created_at` = "排队等待 + turn 执行"时长，而不是"投递失败重试"。

### 1.3 一个反向对照（证明发送前 fence 是有效的）

`task_dispatch_outbox.id=70`（卡 `4fcd4aa5d7da4a5cba6b5c233e47ab82`）：

```
created 2026-09-19 02:32:44.797  status=dead  sent_at=None  attempts=1
last_error = terminal_dispatch_conflict:TaskStateConflict:
             Task '4fcd4aa5…' is not Reserved for assignment 'ae89eb66…' (stale assignment).
```

卡在派发器跑之前就已不在 `Reserved` ⇒ `TaskDispatcher.cs:181` 的 fence 直接 `MarkOutboxDeadAsync`，**消息根本没发出去**。与事件 A/B 形成清晰对照：**"发送时已终态"被正确拒绝，"发送后终态"没人管。**

---

## Q2 为何走到这里

### 2.1 终态是**被显式排除**的（三处谓词，逐一给出）

**① 调度器候选扫描**（`TaskAutoDispatchEvaluator.cs:196-202`）

```sql
AND status IN ({(int)WorkspaceTaskStatus.Ready}, {(int)WorkspaceTaskStatus.Deferred})
AND auto_dispatch_enabled = 1
-- Stage 2（D2）：容器母卡不得进入派发候选（即使 opt-in 也不派发）。
AND NOT EXISTS (SELECT 1 FROM workspace_tasks child WHERE …)
```

**② 事件驱动意图路径**（`TaskSchedulingCoordinator.cs:205-216`）

```csharp
preOutcomeByTask[group.Key] = task.Status switch
{
    WorkspaceTaskStatus.Completed or WorkspaceTaskStatus.Failed
        or WorkspaceTaskStatus.Cancelled or WorkspaceTaskStatus.Archived
        => (TaskSchedulerIntentOutcomes.Terminal, statusLower),
    _ when !task.AutoDispatchEnabled => (TaskSchedulerIntentOutcomes.Ineligible, "not_opted_in"),
    _ when task.Status is not (WorkspaceTaskStatus.Ready or WorkspaceTaskStatus.Deferred)
        => (TaskSchedulerIntentOutcomes.Ineligible, $"status_{statusLower}"),
    …
};
```

**③ Task→Goal 原子启动**（`TaskGoalDispatchTransactionStore.cs:75-78`）

```csharp
if (task.Status is not (WorkspaceTaskStatus.Ready or WorkspaceTaskStatus.Deferred)
    || task.ActiveAssignmentId is not null
    || !task.AutoDispatchEnabled)
    return await RejectAsync(tx, TaskBoundGoalStartCodes.TaskNotEligible, ct, task.Version);
```

**④ 手工派发发送前 fence**（`TaskDispatcher.cs:181-183`）

```csharp
if (task.Status != WorkspaceTaskStatus.Reserved
    || !string.Equals(task.ActiveAssignmentId, entry.AssignmentId, StringComparison.Ordinal))
{ await store.MarkOutboxDeadAsync(entry.Id, $"stale_assignment:…", ct); return; }
```

**判定：属于"已排除仍被派发"这一类，但真正的原因是"读取了过期事实"吗？——不是缓存，也不是投影延迟。**

- `①/②/③` 的读取都是**当轮直读 SQLite，`AsNoTracking`，无任何缓存/投影层**；`④` 是 `SendAsync` 紧邻之前的一次直读。
- 这四个判据读到的都**不是过期快照，而是当时真实的状态**：事件 A 发送时（01:47:40）卡真的是 `Reserved`；事件 B 发送时（01:55:50）四张卡也真的都是 `Reserved`。
- 问题出在**这个"真"只在一个瞬间为真**。消息是 **durable artifact**，它比它的前提（卡处于可执行态）活得更久。从"消息落库"到"Agent 起 turn"之间隔了 **34–240 秒**，而卡的整段生命周期（`Reserved → … → Archived`）只用了 **22 秒**（事件 B：01:55:50 → 01:56:12）。

⇒ **这是 TOCTOU（time-of-check / time-of-use）空窗 + 投递侧无终态复验，不是谓词缺失、不是缓存陈旧。**
⇒ 恰好命中矩阵 §55.2 列的候选解释 **(a)「派发队列在卡 Ready/InProgress 时入队、投递前未重校验终态」**；候选 (b)「读取了过时投影」被本次证据**排除**。

### 2.2 为什么恰好是"批量 5 张"、恰好晚 34 秒

`MessageDeliveryDispatcher` 对同一 target 的排队投递有一条**忙等待**路径：

- `claimedIsForeground` / `_admissionCoordinator.CanStartBackground` / `TryRegisterBackground` 决定"现在能不能给这个 Agent 派活"（`:598-628`）；
- 拿不到 admission 就 `inbox.DeferAsync(…, "Foreground Turn or another background delivery has admission priority.")`；
- 目标 Agent 结束一个 turn 后 `ClearTargetBusy(targetKey)`（`:750-753`，注释原文 *"The target completed a turn — it is no longer busy"*），**扫描/恢复循环（`DiscoverPendingTargetsAsync` `:2077` + `TryDispatchKnownTargetsAsync` `:2096`）随即把该 target 上累积的所有排队投递一次性领走**，于是批量 claim 把 5 条合并成一个 turn。

⇒ "34 秒"不是抖动，而是**上一个 turn 的剩余时长 + 恢复扫描的节拍**；"批量"是这个批量声明机制的**必然形态**，不是偶发。

---

## Q3 修复方案

### 3.1 最小 fail-closed 修复（一处插入 + 一个判定helper）

**改哪个文件/符号**：`Source/PuddingRuntime/Services/Messaging/MessageDeliveryDispatcher.cs`，方法 `TryClaimAndDispatchAsync`。
**插入位置**：batch 组装完成之后、`var mergedContent = …`（`:570`）之前。

```csharp
// 新增：批量组装完成后、合并文本之前 —— 对 task_instruction 做终态复验。
batch = await DropTerminalTaskInstructionsAsync(scope, inbox, batch, executionId, correlationId, ct);
if (batch.Count == 0) return;                 // 全部被丢弃：已逐条 Ack，不起 turn
if (!ReferenceEquals(batch[0], claimed)) claimed = batch[0];
```

```csharp
// 新增私有方法（同文件）。范式直接复用本方法内既有的"claim 后丢弃"先例：
// heartbeat 就是在 claim 之后用 AckAsync 丢掉不合规项（MessageDeliveryDispatcher.cs:421-448），
// 注释原文："Evaluate after claim so both event-driven and restart-recovery paths can ACK/drop
// the occurrence instead of leaving it permanently queued."
private async Task<List<MessageInboxItem>> DropTerminalTaskInstructionsAsync(
    IServiceScope scope, IMessageInbox inbox, List<MessageInboxItem> batch,
    string executionId, string? correlationId, CancellationToken ct)
{
    // 可选解析：Runtime-only 宿主（含既有 MessageDeliveryDispatcherTests 夹具）不注册 ITaskStore，
    // 此时保持旧行为，绝不因缺依赖而丢消息。
    var taskStore = scope.ServiceProvider.GetService<ITaskStore>();
    if (taskStore is null) return batch;

    var kept = new List<MessageInboxItem>(batch.Count);
    foreach (var item in batch)
    {
        if (!TaskInstructionReference.TryRead(item.Metadata, out var taskId, out var assignmentId))
        { kept.Add(item); continue; }         // 非 task_instruction：完全不碰

        var task = await taskStore.GetTaskAsync(item.WorkspaceId, taskId, ct);
        var stale = task is null                                   // 卡已不存在
            || TaskStateMachine.IsTerminal(task.Status)             // Completed/Failed/Cancelled/Archived
            || (assignmentId is not null
                && !string.Equals(task.ActiveAssignmentId, assignmentId, StringComparison.Ordinal));
        if (!stale) { kept.Add(item); continue; }

        await inbox.AckAsync(item.DeliveryId, executionId, ct);     // fail-closed：不派发，落终态回执
        LogExecutionResult(item, MessageDeliveryStatuses.Delivered, executionId, correlationId, item.CausationId);
        _logger.LogInformation(
            "[MessageDeliveryDispatcher] Dropped task_instruction for terminal card task={TaskId} " +
            "status={Status} delivery={DeliveryId} agent={AgentId}",
            taskId, task?.Status.ToString() ?? "missing", item.DeliveryId, item.Target.Id);
    }
    return kept;
}
```

配一个**极小的纯函数**（放在 `PuddingCore/Models/MessageFabricModels.cs` 的 `MessageDeliveryPolicy` 旁边，或 Runtime 内部）：

```csharp
// 与 TaskInstructionEnvelope.OriginTaskManual / Metadata 键名同源（TaskDispatchModels.cs:97-110）
public static bool TryRead(IReadOnlyDictionary<string,string>? md, out string taskId, out string? assignmentId)
{
    taskId = assignmentId = null;
    if (md is null) return false;
    if (!md.TryGetValue("origin", out var origin)
        || !string.Equals(origin, "task.manual", StringComparison.Ordinal)) return false;
    if (!md.TryGetValue("task_id", out var tid) || string.IsNullOrWhiteSpace(tid)) return false;
    taskId = tid;
    md.TryGetValue("assignment_id", out assignmentId);
    return true;
}
```

**为什么这样最小、且不动正常派发**

- 只在 `metadata.origin == "task.manual"`（即 `TaskInstructionEnvelope` 生成的 `task_instruction`，全仓唯一产线见 `TaskCommandService.cs:321-372` + `TaskDispatcher.cs:226`）时生效；heartbeat / 用户消息 / agent 间消息 / sub-agent 结果 / connector 消息**一行都不改**。
- 判定完全复用既有权威口径：`TaskStateMachine.IsTerminal`（Completed/Failed/Cancelled/Archived，`PuddingCore/Tasks/TaskStateMachine.cs:40-44`），并复用 `TaskDispatcher` 已经在用的 `ActiveAssignmentId == assignment_id` 不变量（`TaskDispatcher.cs:181`、`TaskDispatchOutboxStore.cs:137`）。
- 不新增表、不新增列、不新增后台任务、不改 outbox、不改状态机、不改前端。

### 3.2 为什么不会引入新竞态

**(a) 空窗被压到毫秒级。** 复验发生在**同一个方法内、claim 之后、dispatch 之前**——也就是"Agent 正好空闲、turn 正要起"的那一刻。原来 34–240 秒的空窗，被压到"复验 → `DispatchStreamAndCollectAsync` 调用"之间的毫秒级。卡的 `Reserved→Archived` 全程 22 秒，要把毫秒级窗口撞上，概率下降 ~4 个数量级。

**(b) 剩余窗口的后果是有界的、且已被服务端兜住。** 即使这毫秒内卡变终态，Agent 拿到的一只是一段文本；它随后的 `task_claim` / `task_update` 会被服务端的 CAS + 状态机拒绝（`TaskErrorCode.AssignmentStale` / `TaskStateConflict` / `TaskStateMachine.IsTerminal → TryApplyCommand(Update) = false`）。**不会产生重复执行或状态污染，最多浪费一个 turn**——正是本次要消掉的那部分浪费。

**(c) 不会误杀正常派发。** 判定条件是"终态 或 assignment 不匹配"；正常在途派发（`Reserved/Assigned` 且 `ActiveAssignmentId == metadata.assignment_id`）永远通过。刻意**不**把"读不到卡"当成拒绝（`taskStore is null` → 放行），避免在 Runtime-only 宿主或平台库瞬时不可用时把正常派发全掐死；确认终态（`task is null` / `IsTerminal`）才拒绝。

**(d) 丢弃语义与既有先例一致。** `heartbeat` 在 claim 后不合规则就地 `AckAsync` 丢弃（`:421-448`、`:598-616`），本修复沿用同一"claim 后丢弃 + Ack + 结构化日志"范式，不引入新的投递终态语义。

> 备选/进阶（不作为最小修复）：把复验挪到 `dispatchFactory.CreateForWorkspaceAgentAsync` 之后、`DispatchStreamAndCollectAsync` 之前，可再压窄窗口，但要额外处理"批量已部分丢弃时的主项重选"，复杂度更高；建议先落最小版，观察后再决定。

### 3.3 回归测试方案

**放置位置与风格**：`Source/PuddingRuntimeTests/Services/MessageDeliveryDispatcherTests.cs`（既有夹具风格：`RecordingMessageInbox` + `RecordingRuntimeAgentDispatcher` + `services.AddSingleton(...)`，见该文件 `:759-779`、`:835-856`）。需要在该夹具里**新增一个最小 `ITaskStore` 假实现**（当前夹具未注册 `ITaskStore`——这正是不必改产品代码就能插入该门的证明，也说明"可选解析"是必须的）。

| # | 用例 | 断言 |
|---|---|---|
| T1 | `Dispatch_TerminalTaskInstruction_IsDroppedWithoutTurn`：排队一条 `origin=task.manual, task_id=t1, assignment_id=a1` 的投递；假 `ITaskStore` 返回 `Status=Archived, ActiveAssignmentId=null` | `RecordingRuntimeAgentDispatcher.CallCount == 0`（**没有起 turn**）；`RecordingMessageInbox` 记录到该 `DeliveryId` 的 `AckAsync`；日志/决策含 `task_id=t1` |
| T2 | `Dispatch_LiveTaskInstruction_StillDelivered`：同一 metadata；假 `ITaskStore` 返回 `Status=Assigned, ActiveAssignmentId=a1` | `CallCount == 1`，`MessageText` 含卡面正文 → **防回归：正常派发不被误杀** |
| T3 | `Dispatch_MixedBatch_DropsOnlyTerminalCard`：同 target 排队 2 条，1 条终态卡 + 1 条在途卡 | `CallCount == 1`；`MessageText` 只含在途那张的正文（不含被丢那张的标题）；被丢那条已 ack |
| T4 | `Dispatch_AssignmentMismatch_IsDropped`：`Status=Assigned` 但 `ActiveAssignmentId=a2 ≠ metadata assignment_id=a1` | `CallCount == 0`、已 ack（覆盖"卡被改派/释放后旧 instruction 失效"） |
| T5 | `Dispatch_WithoutTaskStore_BehavesAsBefore`：不注册 `ITaskStore` | 投递照常执行（`CallCount == 1`）→ 锁住"可选解析"契约，保证既有夹具与 Runtime-only 宿主不被破坏 |

平台侧**不需要**新增测试（三处既有谓词不动）；若想加一条护栏，可在 `PuddingPlatformTests/Services/Tasks/TaskDispatcherTests.cs` 补一条"发送时已终态 → outbox dead 且 `SendAsync` 计数为 0"的用例（当前已有等价的 `terminal_dispatch_conflict` 行为，属可选加固）。

**运行时证据（验收标准③）怎么给**：修复上线后，下次出现"卡在归档后被派发"的窗口时，`message_deliveries` 应出现该投递**被 ack 但无对应 turn / 无 Agent 回执**，且日志出现 `Dropped task_instruction for terminal card …`。可直接用本次的只读探针（`temp/rca-dispatch-probe*.py`）复查：`created_at → ack_at` 仍然可能很长，但被丢的投递**不再产生 Agent 侧 turn**。

---

## Q4 反模式警告（明确**不允许**的绕过）

| ❌ 禁止 | 为什么 |
|---|---|
| 关 `TaskAutoDispatch:Enabled` / 把 `Mode` 设为 `disabled` / 加 `PausedWorkspaceIds` | 等于关掉整个调度器。修复退化为"不派发"，正常卡也推不动 |
| 屏蔽 / 暂停 `system:task-orchestrator` 发送方，或在消息层加"来自 task-orchestrator 一律拒收"的规则 | 把正常的 `task_instruction` 一起打死，派发链整体失效 |
| 拉长 `ScanInterval` / `MinimumIdle`，或加全局降频、全局 cooldown | 只是把 34 秒变长或变短，空窗仍在；且牺牲正常派发时效 |
| 在 Agent 侧按"标题/正文看起来已完成"做启发式忽略 | 用提示词猜测做正确性判断，不是终态判据；封套里的 `task_id` 才是权威 |
| 让 `TaskDispatcher` 不再发消息 / 删 outbox 行 / 关掉 `Reserved→Assigned` 自动推进 | 破坏手工派发闭环（ADR-072 §8.1），并引入 `Sent`/`Binding` 不一致 |
| 对所有带 `task_id` 的消息一律丢弃 | 过宽：`Reopen`/`Requeue` 后重新派发的同类指令会被一起丢弃 |
| 直接在 DB/看板上手工把已归档卡删掉 | 破坏账本，掩盖问题 |

**唯一允许的形态**：**在"Agent 起 turn 之前"这一点上，对 `task_instruction` 做精确的终态/归属复验，命中即拒绝该条投递并落结构化回执；其余消息类型与正常派发路径一行不改。**

---

## 附：本次只读取证的可复现命令

```powershell
# 只读探针（temp/ 已 gitignore，不进入版本库）
python temp\rca-dispatch-probe.py     # outbox / workspace_tasks / task_events
python temp\rca-dispatch-probe2.py    # 逐卡事件时间线
python temp\rca-dispatch-probe3.py    # 派生 message_id 反查投递
python temp\rca-dispatch-probe4.py    # 投递时序（created/available/ack + defer_count）
python temp\rca-dispatch-probe5.py    # room_messages 正文（证明 from=system/task-orchestrator）
```

库：`D:\data\databases\pudding_platform.db`（以 `file:…?mode=ro` 只读打开）。
关键表：`task_dispatch_outbox`、`message_deliveries`、`task_events`、`workspace_tasks`、`room_messages`。

**已知口径差异**：矩阵 §55 记录的派发时刻 `01:56:46Z` 与我实测的 `ack_at=01:57:19.65Z` 不同。二者不矛盾：`ack_at` 在 `result.IsSuccess` 分支写入（`MessageDeliveryDispatcher.cs:750-765`），即 **turn 结束时**；§55 记的是 **turn 起点（Agent 侧收到）**。两者相差 33 秒，即该 turn 的执行时长。事件 A 同理（文档 01:48:37 = turn 起，实测 ack 01:50:01 = turn 止）。**"归档后 34–35 秒被派发"这个观测依然成立，且已由 turn 起点解释。**

---

## 边界与未做

- 未改任何产品代码、未提交、未推送、未动看板。
- 未把 `4fcd4aa5`（outbox #70 dead-letter）之外的其他历史 outbox 逐条审计；`task_dispatch_outbox` 中 `status=dead` 的历史行可作为"发送前 fence 生效"的旁证集，留待需要时再统计。
- 独立缺陷（本次未处理，仅记录）：`MessageDeliveryDispatcher.cs:405-407` 的 metadata 采用"整体替换"而非合并；与本缺陷无因果，但会影响批量合并时的 metadata 准确性（合并后 turn 只带 `claimed` 那一条的 metadata）。若采纳 Q3 修复，注意**逐条**判定（本方案已按逐条 `item.Metadata` 判定，不受该缺陷影响）。
