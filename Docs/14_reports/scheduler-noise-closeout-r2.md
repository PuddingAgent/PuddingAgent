# P2 Scheduler 降噪 — 收尾报告 v2（closeout-r2 / Plan 修正 + 最终遗留）

- 迭代 30 / 32（剩 2 轮）；task `06898d5dfe004c69ab6d5baf18b2674a`（v30，progress 75%）
- 本轮性质：**收尾轮**，不新增生产代码；只做一次有界只读核实，用于修正 step2 Plan 的落点裁定

---

## 1. 本轮新证据：目标所称「既有 rollup」确实存在，但在**另一条调度链**上

三次收窄 `search_grep` 结果：

| 检索范围 | 查询 | 结果 |
|---|---|---|
| `Source/PuddingRuntime/Services`（递归，含 Background） | `run\.summary｜RunSummary｜rollup｜Rollup｜scan_run｜ScanRun` | **no matches** |
| `Source/PuddingMemoryEngine`（递归，含 Entities/Services） | 同上 | **no matches** |
| `Source/PuddingPlatform` | `ScanRun｜scan_runs` | **命中** ↓ |

命中（平台侧 **Task Scheduler** 链，与潜意识调度器不是同一条）：

- `Source/PuddingPlatform/Services/Scheduling/TaskSchedulerScanRunSchemaBootstrapper.cs:8/13/16/21` — `task_scheduler_scan_runs` 幂等 schema bootstrap（与 `Data.Entities.TaskSchedulerScanRunEntity` 的 `[Column]` 严格一致）；`:48` 索引 `IX_task_scheduler_scan_runs_workspace_started(workspace_id, started_at_utc DESC)`
- `TaskAutoDispatchScanRunner.cs:48-50` — **每轮开始先落 `running` 行**（`scanId` 由 `scan_runs` 生成并贯穿决策持久化，`decisions.scan_id` 可回溯互查；开始失败即 fail-closed 中止）
- `:63-78` — 完成时落 **summary**：`DecisionCodesJson` / `RepairCodesJson` 由 `SerializeCodeDistribution(summary.DecisionCodes…)` 生成 ⇒ **"每轮一条 workspace summary + 原因/代码分布" 的既有范式已存在**
- `:87` — `FailAsync` 失败行
- `Source/PuddingCore/Storage/StorageAdministrationContracts.cs:15` — `Rollups = "diagnostics.rollups"`（存储管理侧亦有 rollup 概念）

## 2. 对本卡 step2 Plan 的直接修正

Plan 裁定① 原写「复用既有 `telemetry_metric_events`，落 `name='scheduler.run.summary'`」。本轮证据表明：

1. **"复用既有 rollup 合同"有真实指涉对象**（`task_scheduler_scan_runs` 的 running→summary→fail 三态 + 原因分布 JSON + workspace/时间索引），不是凭空设想；
2. 但该合同属于 **Task Scheduler（平台侧）**，而本卡噪声源在 **Subconscious 调度器**（`PuddingRuntime/Services/Background` + `PuddingMemoryEngine`）——**该路径下确实没有任何 summary/rollup 实现**（两次 no matches）；
3. 因此 C4 落点必须在二者中显式择一，且**不得**新建表：
   - **(a) 跨层复用**：扩展 `task_scheduler_scan_runs` 合同（或共享 store）承载 subconscious skip 的 reason 分布 —— 最贴合 A07「复用既有 rollup / 不建平行调度器」，代价是跨层依赖需评审；
   - **(b) 同表内新 name**：在既有 `telemetry_metric_events` 落 `name='subconscious.schedule_skip.summary'` —— 不新建表，但**必须在方案中说明为何不等于"平行汇总表"**，并补齐字段：`first/last/window/count/reason 分布/last_flush_watermark/dropped/coalesced/sample_trace_id`。
4. 无论 (a)/(b)，**验收 1 的 `<=120` 分母口径不变**：10 h ÷ 5 min = 120 窗口。

## 3. 最终结论

- **Explore 证据链完整闭合**（触发链、唯一生产者 8 reason、双写对、必留明细、指纹、**实测双写 Δ=30**、基线 1,704.4 行/h/表、8.29 GB 库）。
- **Plan 已有一处需修正**（§2 落点），修正依据是本轮 file:line 级证据。
- **生产代码零改动**，四条验收 **0/4** 达成；本轮不做任何达标宣称。

## 4. 最终遗留清单（交后续轮次 / 平台）

| # | 事项 | 依据 | owner |
|---|---|---|---|
| R1 | 判据 `objective-file-evidence` 误提取 | 卡 `a7aedfe1088d49eb9a5d5b99324abe7c` | 平台 |
| R2 | 工作单元预算 `201617/150000` | `task_update` 回执 | 平台 |
| R3 | 基线：rows/s ✅ 已采；**WAL 须改帧数/checkpoint；dispatch latency 仍缺** | `scheduler-noise-baseline-r1.md §4` | 本卡 |
| R4 | C1 内存计数器 `(workspaceId, reason)` + `FlushSchedulingSkipSummaryAsync` | `scheduler-noise-plan-step2-r1.md §6.1` | 本卡 |
| R5 | C2 累积 → C3 精确移除 `SubconsciousJobQueue.cs:409`（**保留 `:504`**） | checkpoint §2.4 | 本卡 |
| R6 | C4 5 分钟接线 + coalesced；**落点按 §2 择一** | 本报告 §2 | 本卡 + 评审 |
| R7 | C5 同步 **≥7 处** `RecordSchedulingSkipAsync` fake | checkpoint §2.5 | 本卡 |
| R8 | C6 前后对比（同法跑 `temp/sched-baseline-probe.py`） | baseline-r1 §5 | 本卡 |
| R9 | 唤醒路径：durable `EnqueueAsync` 不发信号 ⇒ 改 5 分钟周期会违反 A07「唤醒不变慢」 | step2 裁定三 | 已定论 |

## 5. 剩余 2 轮的最低风险动作

保持**生产代码零改动**（未接线的新增方法即为死代码，本卡拒绝留死代码）；若平台修复 R1/推进节点，则直接执行 R4→R5，每切片单独提交推送。
