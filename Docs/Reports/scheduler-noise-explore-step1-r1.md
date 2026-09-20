# P2 Scheduler 遥测降噪 — Step 1 Explore 证据（checkpoint-v1）

- goalRunId `tg-59f90cc6ee8713427f3c7bc5130d68ab`｜task `06898d5dfe004c69ab6d5baf18b2674a`（v21，InProgress）｜stepNode `tn-77de109db49badc89461226ee678c403`（**Explore**, seq 1/5）
- step objective 逐字：*"Collect canonical repository and runtime evidence before deciding changes."*
- `lastVerdict` = null（本 Goal 首轮）

---

## 1. Canonical 代码证据（文件:行，逐字）

### 1.1 **单调用点双写**（核心事实）
`Source/PuddingMemoryEngine/Services/SubconsciousJobQueue.cs:202-211`
```csharp
public async Task RecordSchedulingSkipAsync(
    SubconsciousSchedulingSkipRequest request,
    CancellationToken ct = default)
{
    if (string.IsNullOrWhiteSpace(request.Reason))
        throw new ArgumentException("Scheduling skip reason is required.", nameof(request));

    await RecordSchedulingSkipActivityAsync(request, ct);   // → runtime_activity
    await RecordSchedulingSkipMetricAsync(request, ct);     // → telemetry_metric_events
}
```
⇒ **一次调用同时写两张表**，两处都带同一个 `BuildSchedulingSkipFields(request)`、同一个 `Summary` 文本 ⇒ 完全冗余（验收标准 3 所指的"同一事实双写"）。

### 1.2 两个写入器
- `SubconsciousJobQueue.cs:400-429` `RecordSchedulingSkipActivityAsync` → `_activitySink.RecordAsync(new RuntimeActivity{ Component = RuntimeActivityComponents.Memory, **Operation = "subconscious_job.schedule_skip"**, Status = Deferred, Metadata = BuildSchedulingSkipFields(request) })`
- `SubconsciousJobQueue.cs:432-461` `RecordSchedulingSkipMetricAsync` → `_telemetrySink.RecordAsync(new TelemetryMetric{ Source = "pudding.memory.subconscious_job_queue", Category = Memory, **Name = "subconscious_job.schedule_skip"**, CountValue = 1, Unit = "job", Severity = "info", Dimensions = BuildSchedulingSkipFields(request) })`
- `SubconsciousJobQueue.cs:467-489` `BuildSchedulingSkipFields`：`skip_reason` + 可选 `job_id/job_type/workspace_id/session_id/agent_id/agent_template_id/source_hook_name/source_event_id/source_compaction_id` + `request.Details`

### 1.3 触发链（**Runtime 侧，不是 Task Scheduler**）
| 环节 | 位置 | 逐字 |
|---|---|---|
2 秒节拍 | `Source/PuddingRuntime/Services/Background/SubconsciousWorkerService.cs:18` | `private static readonly TimeSpan IdlePollDelay = TimeSpan.FromSeconds(2);` |
轮询循环 | 同文件 `:100-145`（三处 `await Task.Delay(IdlePollDelay, stoppingToken)` 位于 :107 / :133 / :145） | `ConsumeQueueLoopAsync` |
跳过判定 | `Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs:124-142` | `if (leased is null) { var reason = ResolveNoLeaseReason(...); await RecordSkipAsync(reason, details, ct); }` |
单一生产调用点 | `SubconsciousJobScheduler.cs:150-159` `RecordSkipAsync` → `_queue.RecordSchedulingSkipAsync(...)` | 全仓 grep `RecordSchedulingSkipAsync` 在 `Source/**` 的**唯一非测试调用点** |

⇒ 结论：**噪声源是潜意识 Job 路径（Runtime + MemoryEngine）**，与 Task Scheduler（`TaskAutoDispatchScanRunner`）**无关**。卡片提醒的"别只改 Task Scheduler 而漏掉潜意识 Job 路径"**被证据确认**。

### 1.4 既有 rollup / summary 设施（用于"复用、不建平行表"）
`sqlite_master` 中与 rollup/aggregate 相关的表**只有 2 张**：`context_layer_daily_rollups`、`llm_usage_daily_aggregates`。
代码侧：`PuddingPlatform/Services/ContextLayerDailyRollupService.cs`（按日 rollup + 闭日缓存 + `RemoveRange` 重建）、`TokenUsageDailyAggregateService.cs`。
⇒ **不存在 scheduler 运行摘要表**；本卡要求的「5 分钟一条 scheduler_run summary」需要一个落点决策（**留给 step2 Plan 裁定**，候选：复用 `telemetry_metric_events` 以 `name='scheduler.run.summary'` + `dimensions_json` 计数，而非新建表）。

---

## 2. 运行时证据（只读实测，2026-09-20T12:54Z）

探针：`temp/db-probe-scheduler-noise-step1.py`（gitignored，只读 `file:…?mode=ro`）→ 输出 `temp/scheduler-noise-step1.json`

| 事实 | 实测值 |
|---|---|
`runtime_activity` 全表 | **1,773,005** 行 |
`telemetry_metric_events` 全表 | **2,136,437** 行 |
`operation='subconscious_job.schedule_skip'` | **1,367,946** 行（占全表 **77.2%**） |
`name='subconscious_job.schedule_skip'` | **1,367,954** 行（占全表 **64.0%**） |
两表 skip 合计 | **≈ 2,735,900** 行 |
第 2 大 operation（`chat`） | 124,269 行 ⇒ 噪声是其 **11.0 倍** |
持续窗口 | `2026-08-05T23:04:35Z` → `2026-09-20T12:54:36Z`（**≈ 45.6 天**） |
原因分布（最近 5,000 条样本） | `skip_no_eligible_job` **2,681**（53.6%）｜`skip_cooldown` **2,319**（46.4%） |
节拍（相邻 4,999 对） | min **2.002s**｜p50 **2.040s**｜p95 **2.127s**｜max 4.342s |
观测写入速率 | **1,749.5 行/小时**（telemetry 侧，样本 2.858 小时） |
最近整点桶 | 12 时 1,599｜11 时 1,749｜10 时 1,652 |
维度 | `distinct_workspaces = 1`、`distinct_sessions = 1`（样本内） |

**与卡片描述的差异（必须记录）**：
- 卡片给的 19,894 条/12 小时是 **2026-09-11 的窗口值**；**当前实际累计已达 1,367,946/表**，单表速率 **≈1,750 行/小时 ≈ 42,000 行/天** ⇒ 噪声是**长期最大单一写入源**，而不是一次性峰值。
- 卡片写的原因名 "no eligible job / cooldown" **不是逐字常量名**；实测快照内的精确常量是 **`skip_no_eligible_job`** 与 **`skip_cooldown`**。

---

## 3. 对四条验收标准的证据基线（本步只给基线，不改动）
| 标准 | 当前基线（实测） |
|---|---|
1. 空闲 10 小时 no-op 持久行 ≤120 workspace summary | 当前 10 小时 ≈ **17,500 行/表**（≈1,750×10）⇒ 现状比目标高 **约 146 倍/表** |
2. 派发/拒绝状态变化/错误保留明细 + trace | 现有实现**只**写 Deferred 的 skip；`RecordActivityAsync`（状态变化路径）与 skip 路径分离 ✅ 结构上可保留 |
3. telemetry/runtime_activity 单一 owner | ❌ **同调用点双写**（1.1/1.2 逐字），无 owner 声明 |
4. rows/s、WAL、CPU/IO 与 dispatch latency | rows/s ≈ 0.486 行/秒（两表合计，按 45.6 天均值）⇒ 改造前后可对比；**改造前基线已记录**，latency 基线**未采集**（需 step2 设计 + step4 实测） |

新增验收：`12 小时无任务场景 skip 明细写入降 ≥95%`、`计数 first/last/window 齐备`、`不通过删除业务记录达标`（**历史 2.7M 行属业务记录，不得删除**；本卡只改写入形态）。

---

## 4. Step 2（Plan）需要裁定的未决问题
1. **summary 落点**：复用 `telemetry_metric_events`（`name='scheduler.run.summary'`，用 `dimensions_json` 携带 reason 计数与 first/last/window）还是新增最小表？（卡片要求"与 Goodput 卡共用 schema/store、不新增平行汇总表"⇒ 倾向于复用。）
2. **owner 归属**：建议 `telemetry_metric_events` 为 authoritative owner（指标语义），`runtime_activity` 仅保留**状态变化/派发/错误**明细；skip 的 activity 写入**保留还是移除以避免破坏现有查询方**（需先排查消费方）。
3. **5 分钟节拍**：`SubconsciousJobScheduler` 主周期改 5 分钟 reconciliation + 事件触发 coalesced signal 的具体接线点（是否复用 `BackgroundService` 现有通道）。
4. **`RecordSchedulingSkipAsync` 的 8 个调用方契约**（含 4 个测试工程）如何在不破坏测试的前提下改变语义。

## 5. 诚实限定
- 本轮**未修改任何生产代码**（Explore 步）。
- 探针脚本 v1/v2 各有**我自己的缺陷**：v1 `stamps` 未初始化（NameError）；v1 列名候选用 PascalCase 而实际为 snake_case（`occurred_at_utc`/`name`/`operation`/`dimensions_json`）导致解析全空；v2 的 `totals`/`skip_rows` 字段因我的 `scalar()` 解析条件写错返回 **null** —— 相应数值改由 `GROUP BY` 结果给出（**数值等价**，非缺失证据）。
- 原因分布、workspace/session 维度为**最近 5,000 条样本**，非全量；写入速率为 2.858 小时窗口外推。
- `COUNT(*)` 全表扫描在 2.1M 行上耗时较长（本探针单次运行 ≈10 分钟），未做多轮重复测量 ⇒ 不以单次快照宣称稳定速率。
