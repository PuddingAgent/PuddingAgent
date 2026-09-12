# P2 Scheduler 遥测降噪：五分钟汇总 schedule_skip，停止双库每两秒空轮询写入

- **任务卡**：`06898d5dfe004c69ab6d5baf18b2674a`（p1，Blocked；父任务 `6f49d33e900c4e7e960c630fa7d7c2fb`）
- **权威设计**：`Docs/Features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md` + ADR-084/085/086/087
- **审计来源**：`Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md`（A07 / §9.A07）
- **代码级方案索引**：`Docs/Features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md` §7、§11.4、§12
- **历史意图存档**：`Docs/superpowers/plans/2026-07-01-memory-v2-f3-worker-scheduling-plan.md:319,408`（双投影 `runtime_activity` + `telemetry_metric_events` 为当时**有意**设计，本卡即其降噪后置）
- **证据基线**：`temp/p2-schedule-skip-evidence-20260913.md`（iteration 1，commit `f8236ef` 时点）
- **状态**：设计草稿（未写实现）；编码需在澄清 §7 后另派原子任务

---

## 1. 问题（行号级现状）

| # | 事实 | 证据 |
|---|---|---|
| P1 | 一次 skip 必然写 **两行、两张表、同一 DB** | `Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs:202-211`：方法体只有 `await RecordSchedulingSkipActivityAsync(...)` + `await RecordSchedulingSkipMetricAsync(...)` |
| P2 | activity 投影 | `SubconsciousJobQueue.cs:416` → `RuntimeActivity { Operation="subconscious_job.schedule_skip", Component=Memory, Status=Deferred }` |
| P3 | metric 投影 | `SubconsciousJobQueue.cs:449` → `TelemetryMetric { Name="subconscious_job.schedule_skip", CountValue=1, Unit="job", Status=Deferred, Dimensions=BuildSchedulingSkipFields(request) }` |
| P4 | 无背压 | 两处 `catch (Exception ex) { _logger.LogWarning(...) }` → 写失败仅告警、不阻断、不计 drop |
| P5 | 触发源唯一且高频 | `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs:157 RecordSkipAsync` ← **唯一调用方**是 `TryLeaseNextAsync`（同一文件 `:31`），由 `SubconsciousWorkerService.cs:114` 在 `ConsumeQueueLoopAsync` 内调用 |
| P6 | 2 秒节拍 | `SubconsciousWorkerService.cs:18 IdlePollDelay=2s`；`:107`（暂停分支）/`:133`（无 job 分支）/`:145`（异常分支）三处 `Task.Delay`。稳态空闲下每个迭代**必然**产出 1 次 skip → 2 行 |
| P7 | 量级 | 30.4h 窗口：`runtime_activity` 19,894 + `telemetry_metric_events` 19,894 = **39,788 行**；无任务/cooldown 占绝大多数（no eligible 12,806 / cooldown 4,605） |
| P8 | 清理被此卡阻塞 | `Source/PuddingPlatform/Services/StorageManagement/StorageDataClassCatalog.cs:92` 原文：「telemetry_metric_events 原始行。自动清理需要**小时聚合先落地**，当前默认关闭。」 |
| P9 | 潜在更严重的副作用 | skip 写入在**独占队列的消费循环热路径上被 `await`** → 每 2s 至少 2 次 DB 往返（每次 sink 各自建 DbContext + SaveChanges），与业务吞吐无关却持续抢占 SQLite 写锁 |

**skip 原因枚举（全部产生点，均在 `SubconsciousJobScheduler.TryLeaseNextAsync`）**：
`Disabled`(`:35`) · `Cooldown`(`:46`，`idleDuration < IdleCooldownSeconds`) · `GlobalLimit`(`:60`) · `DryRun`(`:102`) ·
`NoEligibleJob`(`:97` 与 `ResolveNoLeaseReason` `:168`) · `BudgetExhausted` / `WorkspaceLimit` / `SessionLimit`（`ResolveNoLeaseReason`）。

## 2. 设计原则

1. **owner 唯一性**：同一事实只允许一个 owner 落库。高频、无状态语义计数 → **telemetry 汇总行**；状态变化 / 派发 / 错误 → **canonical event 明细**（现有路径不变）。
2. **不新建平行调度器、不新建汇总表**：复用既有 rollup 模式（`Source/PuddingPlatform/Services/ContextLayerDailyRollupService.cs`：closed-window 缓存 + live-window 直读 + 幂等重建 + `AggregateRangeAsync`/`MergePayloads`），粒度由「按日」扩到「5 分钟」；汇总行仍写入既有 `telemetry_metric_events`。
3. **降噪不得制造"假节省"**：缺失 / 溢出 / 未知归因必须显式计数（`dropped` / `coalesced` / `other`），不得因为"没写行"而让成本与用量指标显示为 0。
4. **可关断**：汇总与信号唤醒受配置开关控制，默认关闭 → 灰度开启，异常时可一键回退到当前行为。

## 3. 方案

### C1 — skip 记录改为「内存累加 + 5 分钟 flush」（核心降噪）

`SubconsciousJobQueue`：

- 新增进程内累加器 `SchedulingSkipAccumulator`（`ConcurrentDictionary<string,int>` 按 reason + 首末时间戳 + `coalesced` 计数）。
- `RecordSchedulingSkipAsync` 语义变更为 `AccumulateSchedulingSkip(request)`：
  - **不再**调用 `RecordSchedulingSkipActivityAsync`（删除该方法与其 `:416` 投影）→ `runtime_activity` 侧 schedule_skip 归零；
  - **不再**每次写 telemetry，只 `Interlocked.Increment` 计数（无 DB 往返 ⇒ 消除 P9）；
  - `Details` 中的 `skip_reason` 及 `BuildSchedulingSkipFields` 的可聚合字段保留为计数键。
- 新增 `FlushSchedulingSkipSummaryAsync(ct)`：窗口届满时写 **1 行** `TelemetryMetric`（表不变）：

```
Name        = "subconscious_job.schedule_skip.summary"   // 新名，与旧明细名区分，便于灰度对照
Source      = "pudding.memory.subconscious_job_queue"
Category    = TelemetryMetricCategories.Memory
Unit        = "job"
Status      = TelemetryMetricStatuses.Deferred
Severity    = "info"
CountValue  = 窗口内 skip 总数
Dimensions:
  window_minutes, window_start_utc, window_end_utc,
  skipped_total,
  no_eligible_job, cooldown, global_limit, workspace_limit,
  session_limit, budget_exhausted, dry_run, disabled, other,
  drain_wakes, lease_attempts, dispatched, coalesced, dropped
```

- flush 采用 **幂等重建语义**（同窗口重复 flush 覆盖同一 `window_start_utc` 行；参照 `ContextLayerDailyRollupService` `RemoveRange`+`AddRange` 模式），失败计 `dropped` 并在下窗合并，不阻塞热路径。
- `skip_reason` 之外的 per-workspace/session/agent 维度**不进汇总行**（会重新引入高基数写放大）；需要钻取时依赖 §5 的采样。

### C2 — 5 分钟 authoritative reconciliation + coalesced 信号唤醒

`SubconsciousWorkerService.ConsumeQueueLoopAsync` / `SubconsciousJobScheduler`：

- 新增 `ISubconsciousJobWakeSignal`（`SemaphoreSlim(0)` 或 `Channel<bool>` 单元素合并语义）：
  - `DurableEnqueueAsync` 成功路径 / `_channel` 写入路径 → `Notify()`（合并多次通知为 1 次唤醒）；
  - retry 到期由 5 分钟对账兜底。
- 循环体改为 `await _signal.WaitAsync(ReconciliationInterval /*5min*/, ct)`：
  - **有信号** → 立即尝试 lease（触发的 lease-null 也走 C1 累加）；
  - **超时** → 执行权威对账：`GetStatsAsync` + 到期 retry 扫描 + `FlushSchedulingSkipSummaryAsync`。
- `IdleCooldownSeconds` 语义**不变**，但只在唤醒点求值（不再每 2s 求值）——cooldown 计数由 12 次/小时 取代 1,750 次/小时。
- `IdlePollDelay=2s` 常量退役；`:145` 异常分支改为**指数退避 + 上限**（1s→…→60s），避免"异常风暴 → 2s 重试 → 又一轮噪声"。
- `:107` 暂停分支改为等待"恢复"信号（暂停期间零唤醒、零 skip 计数）。
- 明细保留（AC 要求）：状态变化、派发、错误仍走既有 canonical event 写入；本卡**不修改**这些路径，只在 `dispatched` 上做计数便于对账。

### C3 — 落位与 owner

| 事实 | owner | 形态 |
|---|---|---|
| skip 高频无状态计数 | `SubconsciousJobQueue`（MemoryEngine） | 5 分钟 1 行 summary |
| 5 分钟对账 + flush 触发 | `SubconsciousWorkerService`（Runtime） | 既有 hosted service，无新调度器 |
| 状态变化 / 派发 / 错误 | 既有 canonical event | 明细，频率天然低 |
| 汇总读取 / 长期留存 | Platform 侧（与 telemetry 同库） | 沿用 ContextLayer rollup 读侧模式 |

## 4. 验收（可测量）

1. **写入量**：`subconscious_job.schedule_skip*` 行数 ≤ `2 × (窗口数)`（即 24h ≤ 576 行/表，含 summary）；相对基线 39,788 行/30.4h **下降 ≥ 99%**。
2. **明细不丢**：状态变化 / 派发 / 错误事件计数与改造前一致（同窗口对比，允许 ±0）。
3. **可解释性**：任一 5 分钟窗口内 `sum(reason counts) + dropped == skipped_total`，且 `skipped_total == lease_attempts - dispatched`。
4. **无忙轮询**：空闲 1 小时 `drain_wakes ≤ 13`（12 次对账 + 1 次容错），CPU 时间与 DB 往返同比下降。
5. **无假节省**：人为注入 sink 抛错 → `dropped > 0` 可见；不出现"指标消失 = 成本为 0"。
6. **回归**：`SubconsciousJobQueue` / `SubconsciousJobScheduler` / `SubconsciousWorkerService` 现有单测全绿；新增 summary/flush/信号合并用例。

## 5. 风险与回滚

| 风险 | 缓解 |
|---|---|
| 信号丢失导致任务延迟 | 5 分钟对账为权威兜底（延迟上界 = 1 个窗口，需在 §7 确认可接受；否则缩短对账到 60s 仅作 lease 探测、summary 仍 5 分钟） |
| 明细被误删导致排障困难 | 明细路径零改动 + `drain_wakes`/`lease_attempts`/`dispatched` 计数保持可对账 |
| 汇总行高基数 | 汇总行不含 workspace/session 维度；需要钻取时保留 trace drill-down sample（采样率可配） |
| 进程崩溃丢失窗口计数 | flush 使用幂等覆盖；丢失窗口计 `dropped`（保守方向：宁可多算不可少算） |
| 回归面 | 配置开关 `Subconscious:Scheduling:SkipSummary:Enabled`（默认 false）一键回退 |

## 6. 实施顺序（原子任务，供后续派工）

1. **T1**：`SchedulingSkipAccumulator` + `FlushSchedulingSkipSummaryAsync`（纯新增 + 单测，不改调用方）→ 可独立验收。
2. **T2**：`RecordSchedulingSkipAsync` 切到累加器、删除 activity 投影（同一原子提交内改调用方与测试）。
3. **T3**：`ISubconsciousJobWakeSignal` 注入 + 循环体改造 + 指数退避；`IdlePollDelay` 退役。
4. **T4**：`StorageDataClassCatalog` 放行 telemetry 自动清理（P8 的前置已由 T1/T2 满足；本卡只登记，不代做）。
5. **T5**：灰度 + 前后行数/唤醒数/明细计数对照报告（写入 `Docs/Reports/`）。

## 7. 待澄清（阻塞编码）

1. 5 分钟对账是否可接受为任务延迟上界？（若不可接受，需保留 60s 轻量 lease 探测——会削弱但不会摧毁降噪效果。）
2. `subconscious_job.schedule_skip`（旧明细名）是否需要保留一个"首日对照窗口"再彻底停写？
3. 与 **P1 Goodput SLO 卡 `0b16740022f84b58a9532a87f1bc5509`** 共享 schema/store 的边界：`skipped_total`/`drain_wakes` 是否直接作为 Goodput 的 `no_ready_work` / `queue_time` 输入（避免两处定义同一指标）。

## 8. 明确不做（本卡范围外）

- 不改 `TaskAutoDispatchEvaluator` / Task Scheduler 侧（旧卡只改 Task 侧是错的，反向亦然：本卡只覆盖潜意识 Job 路径）。
- 不新建汇总表、不新建调度器、不引入新的 telemetry 时间序列存储。
- 不改 skip 判定逻辑本身（原因集合与阈值保持原样）。
