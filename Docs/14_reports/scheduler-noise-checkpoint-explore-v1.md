# Scheduler Noise — Explore Checkpoint (checkpoint-v1)

| field | value |
|---|---|
| schemaVersion | `checkpoint-v1` |
| goalRunId | `tg-59f90cc6ee8713427f3c7bc5130d68ab`（objectiveVersion 1） |
| iteration | 26（maxIterations 32） |
| taskId / assignmentId | `06898d5dfe004c69ab6d5baf18b2674a` / `ebd4e146b90f4d31bcfc31647fb9b9ae`（InProgress v26） |
| stepNodeId | `tn-77de109db49badc89461226ee678c403`（kind=Explore, sequenceNo=1/5） |
| expectedOutputContract | `checkpoint-v1 with evidence refs, progress fingerprint and terminal verdict` |
| observedAtUtc | 2026-09-20T13:24Z |
| repo / branch / HEAD | `E:\github\AgentNetworkPlan\PuddingAgent` / `master` / `08b3cbb5cba5a2f577fd686bb1e296c2dc9d92f0` |
| worktree | clean（`git_status.has_changes=false`，仅 Ignored 条目） |
| lastVerdict（本轮开始） | `blocked` / `blockerCode=execution_protocol_error` / `unmetCriteria=[]` |
| acceptanceContract | `null` ⇒ 本轮不产出 `meta.goal_contract_proposal` |

---

## 1. Progress fingerprint（本轮实测，SHA-256，`certutil -hashfile … SHA256`，exit=0）

| 文件 | sha256 | 角色 |
|---|---|---|
| `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs` | `54426b515b7cbfd97527fc0607aca207b4a8a6d436704951876f770a2af5cf93` | 2 秒 idle 轮询驱动源 |
| `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs` | `95a362f6a3eef8e74698273decf14e78a2aa2501ef570f08b760256691498c35` | 8 处 skip 原因产生点（唯一生产者） |
| `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs` | `dded3e1d8c8fb979392a6e4cd2da1712ad0105fd6596e23b659a477ea009f776` | 同一事实双库写入点 |
| `Source/PuddingMemoryEngineTests/SubconsciousJobQueueTests.cs` | `135f33ba43e4f27a2ab57abe351a78f8345cfb4429e866f5979b72b37885bb77` | 受影响契约测试（C5 需同步改） |

复算命令（可复核）：
`certutil -hashfile "Source\PuddingMemoryEngine\Services\SubconsciousJobQueue.cs" SHA256`

---

## 2. Evidence refs（file:line，均为本轮只读复核）

### 2.1 触发链：2 秒空轮询 → Scheduler
- `SubconsciousWorkerService.cs:18` — `private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(2);`
- `SubconsciousWorkerService.cs:107` / `:133` / `:145` — 三条路径均 `await Task.Delay(IdlePollDelay, stoppingToken)`
⇒ 空闲期每 2 秒唤醒一次调度尝试（与任务卡「约每 2.06 秒一对」口径一致）。

### 2.2 skip 原因：8 个产生点（唯一生产者）
- `SubconsciousJobScheduler.cs:12` — `public sealed class SubconsciousJobScheduler`
- `:39 Disabled`｜`:49 Cooldown`（条件 `:46` `idleDuration < IdleCooldownSeconds`）｜`:64 GlobalLimit`｜`:103 NoEligibleJob`｜`:108 DryRun`｜`:131 BudgetExhausted`｜`:171 WorkspaceLimit`｜`:173 SessionLimit`
- `:157` — **全仓唯一** `_queue.RecordSchedulingSkipAsync(new SubconsciousSchedulingSkipRequest{…})` 出口
⇒ 计数键应为 `(workspaceId, reason)`，reason 取值域共 8 个（≤ 8×workspace 计数条目/窗口）。

### 2.3 双写点：同一 scheduler fact 写两张表（验收 3 的整改对象）
- `SubconsciousJobQueue.cs:202` — `public async Task RecordSchedulingSkipAsync(...)`
  - `:209` → `RecordSchedulingSkipActivityAsync` ⇒ `runtime_activity`
  - `:210` → `RecordSchedulingSkipMetricAsync` ⇒ `telemetry_metric_events`
- `:400-421` `RecordSchedulingSkipActivityAsync`：`Component=Memory`、`Operation="subconscious_job.schedule_skip"`（`:416`）、`Status=Deferred`（`:417`）、`Metadata=BuildSchedulingSkipFields(request)`（`:419`）
- `:432-457` `RecordSchedulingSkipMetricAsync`：`Source="pudding.memory.subconscious_job_queue"`、`Name="subconscious_job.schedule_skip"`（`:449`）、`Status=Deferred`、`CountValue=1`、`Unit="job"`、`Dimensions=BuildSchedulingSkipFields(request)`（`:455`）
- `:468` `BuildSchedulingSkipFields(request)` — 同一维度字典**同时**喂给两处 ⇒ 语义完全重复，可直接收敛为单 owner。

### 2.4 必须保留明细的路径（降噪边界）
- `:91`/`:92` — `subconscious_job.enqueue`（activity + metric）
- `:262`/`:263` — `subconscious_job.complete`
- `:289`/`:293`、`:308`/`:312`、`:339`/`:343` — failed / retried 明细
- `:395`/`:396` — `subconscious_job.lease`（Started）
- `:504` — 状态变化 activity（**C3 不得误删**）
⇒ 降噪只针对 `schedule_skip` 这一"高频 no-op"事实；状态变化/派发/错误明细一律保留（与 A07「减少日志不是删除业务事件」一致）。

### 2.5 契约影响面：`RecordSchedulingSkipAsync` 的 fake 实现 ≥ 7 处
`PuddingRuntimeTests/Services/SubconsciousJobSchedulerTests.cs:274`、`SubconsciousWorkerServiceTests.cs:421`、`SubconsciousRuntimeControlServiceTests.cs:162`、`RuntimeServiceExtensionsTests.cs:150`、`SessionCompressedMemoryMaintenanceHookTests.cs:168`、`PuddingWebApiTests/SubconsciousDebugApiControllerTests.cs:350`、`PuddingPlatformTests/Controllers/SubconsciousDebugApiControllerTests.cs:134`

> **订正**：`scheduler-noise-plan-step2-r1.md` §④ 记为「6 个测试 fake」，本轮实测为 **7**，step3 的 C4/C5 影响面以本行为准。

---

## 3. Coverage / 诚实限定

1. 上述 `search_grep` 在 `Source/` 全量检索时**触及 2000 文件枚举上限**（`scanned 890/2000 files`）⇒ §2.2「唯一出口」、§2.5「≥7 处」均为**下界**，非穷尽断言；收窄目录（`Services/`、`Services/Background/`）后的结果不带上限提示。
2. 运行库计数（`runtime_activity`/`telemetry_metric_events` 各 19,894 条、39,788 行）来自任务卡审计窗与既有证据档 `scheduler-noise-explore-step1-r1.md`，**本轮未重新探库** ⇒ 属继承证据，不是本轮实测。
3. 验收 1 的「10 小时 ≤120 行」与验收 4 的「dispatch latency 不退化」所需的**改造前基线**（rows/s、WAL 增长、CPU/IO、dispatch latency）本轮未采集 ⇒ 在采集前不得宣称达标或不退化。

---

## 4. Terminal verdict

**Explore = DONE（证据充分，可进入 step2 Plan / step3 Change）**

可判决的问项均已锚定到 `file:line` + 内容哈希：
- 生产源唯一（`SubconsciousJobScheduler.cs:157`）× 8 个 reason；
- 双写对确定（`SubconsciousJobQueue.cs:209/210`，维度字典共用 `:468`）；
- 驱动频率确定（`SubconsciousWorkerService.cs:18` = 2s）；
- 必须保留的明细路径与可降噪路径已分离（§2.4）；
- 契约影响面下界 7 个 fake 已列名。

**残余未知（转 step3 前置，非阻塞 Explore 收口）**：
1. 改造前 SQLite rows/s、WAL、CPU/IO、dispatch latency 基线（验收 1/4 的判定前提）；
2. 新任务到达唤醒路径实测延迟（验收新增项，决定是否需新增 coalesced signal）；
3. `runtime_activity` skip 的消费方排查（step2 §② 的 fail-closed 前置）。

---

## 5. 本轮协议自述

- 本文件即 **Explore 节点 required output contract 的交付物**（evidence refs + progress fingerprint + terminal verdict 三段齐备）。
- `acceptanceContract = null` ⇒ 不产出 `meta.goal_contract_proposal`。
- 生产代码**未改动**；本轮不产生行为变更，fingerprint 即为改动前基线坐标。
