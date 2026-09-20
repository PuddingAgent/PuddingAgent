# P2 Scheduler 遥测降噪 — Step 2 Plan（r1）

- goalRunId `tg-59f90cc6ee8713427f3c7bc5130d68ab`｜task `06898d5dfe004c69ab6d5baf18b2674a`（v24, InProgress）
- 输入：step1 Explore 证据 `Docs/Reports/scheduler-noise-explore-step1-r1.md`（含 §6 A07 逐字约束）；A07 原文 `Docs/Reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md:250`
- **本步只出方案，不改生产代码**（step3 才改）。

---

## 0. 现状一句话
`SubconsciousJobQueue.RecordSchedulingSkipAsync` 在每次 2 秒空轮询被调用时**同时写两张表**（实测各自 1,367,946 / 1,367,954 行，合计 ≈273.6 万行，占两表 77.2% / 64.0%），且无 owner 声明。

## 1. 裁定一：5 分钟 summary 的落点（**不新增平行表**）
**决定**：写入既有 `telemetry_metric_events`，用新的 `name = 'scheduler.run.summary'`（沿用既有 `source = 'pudding.memory.subconscious_job_queue'`、`category = Memory`），全部计数放进既有 `dimensions_json`：

| dimensions_json 字段 | 含义（对应 A07 要求） |
|---|---|
`window_start_utc` / `window_end_utc` / `window_minutes` | **window** |
`count_total`、`count_skip_no_eligible_job`、`count_skip_cooldown`、`count_dispatched`、`count_error` | 原因计数（A07「no_eligible/cooldown/unchanged 使用原因计数」） |
`first_event_at_utc` / `last_event_at_utc` | **first / last** |
`last_flush_watermark_utc` | **上次 flush 水位**（A07 逐字要求） |
`dropped_count` / `coalesced_count` | 卡片「保留 dropped/coalesced count」 |
`sample_trace_id`、`sample_event_id` | trace drill-down sample（卡片要求保留） |

**为什么恰好满足标准 1**：10 小时 ÷ 5 分钟 = **120 个窗口** ⇒ 每 workspace 10 小时 120 行 ≤ 120 ✓（标准 1 的数字正是该口径）。24 小时 = 288 行/表，相对现状（≈42,000 行/天/表）**降 99.3%** ✓（≥95%）。
**复用理由（卡片硬约束）**：与 Goodput 卡共用同一 store；不新增汇总表。

## 2. 裁定二：owner 归属
- **`telemetry_metric_events` = authoritative owner**（指标语义：summary + 状态变化/派发/错误明细）。
- **`runtime_activity` 只保留状态变化 / 派发 / 错误明细**（活动流水语义），**移除** `subconscious_job.schedule_skip` 的 activity 写入。
- **移除前置条件（fail-closed）**：先排查消费方。已知消费面：`agent_diagnostics(tool_stats)` 按 `Operation` 聚合并依赖 `RuntimeActivityComponents.Memory` 计数；测试工程断言。⇒ **列为 step3 的 C0 探查切片**，未排查完毕不得删除写入。
- 保留 `trace`：summary 行仍带 `trace_id`/`sample_event_id`（drill-down 不丢）。

## 3. 裁定三：5 分钟 reconciliation + coalesced signal（与卡片字面措辞的精确化差异）
**决定的实现语义**（并说明与卡片"主周期改为 5 分钟"的字面差异）：
1. **2 秒消费循环保留**（`SubconsciousWorkerService.ConsumeQueueLoopAsync`）—— A07 逐字要求「**不取消必要的恢复扫描**」；把 2 秒改成 5 分钟会**降低新任务唤醒及时性**，与「任务到达唤醒不变慢」冲突。
2. 变化点在**写入形态**：`SubconsciousJobScheduler` 的 skip 结果→**内存 counter 累积**（按 `workspace_id` × `reason`），不再逐条落库。
3. **5 分钟 flush**：到达窗口边界（或水位异常）时，每 workspace 落 **1 条** `scheduler.run.summary`。
4. **coalesced signal 唤醒**：新任务入队时经既有 `Channel<ConsolidationJob>` 唤醒（**待 step3 探查确认既有唤醒路径**，不新造调度器）。
5. **崩溃容许上限（A07 要求明确）**：内存 counter 最多丢失 **1 个 5 分钟窗口**的 skip 计数（监控口径，**不影响业务事件**）；丢失以 `dropped_count` 显式暴露，不静默。

## 4. 裁定四：调用方契约边界（实测 grep 结果）
`RecordSchedulingSkipAsync` 现有 8 处声明/调用：
- **生产 1 处**：`Source/PuddingRuntime/Services/Background/SubconsciousJobScheduler.cs:157`
- **测试 6 处**：`PuddingMemoryEngineTests/SubconsciousJobQueueTests.cs:283/292`（真实实现 + 断言）、`PuddingRuntimeTests` 的 `SubconsciousJobSchedulerTests.cs:274`、`SubconsciousRuntimeControlServiceTests.cs:162`、`SubconsciousWorkerServiceTests.cs:421`（fake 实现）、`PuddingPlatformTests/Controllers/SubconsciousDebugApiControllerTests.cs:134`、`PuddingWebApiTests/SubconsciousDebugApiControllerTests.cs:350`（fake 实现）

**决定**：**方法签名不变**（保护 6 个 fake），语义由"立即双写"改为"累积"；**新增** `FlushSchedulingSkipSummaryAsync(workspaceId, ct)` 显式落 summary。
**已知影响面（不隐瞒）**：`SubconsciousJobQueueTests.RecordSchedulingSkipAsync_ShouldRecordTelemetryMetric` 断言"立即写 metric" ⇒ 改造后必须改为「skip 后 flush，再断言 summary 行」；该测试**必须同步更新**，不是可选项。

## 5. 裁定五：前后对比口径（可核查、附样本量）
| 维度 | 基线（已有/待采） | 判定 |
|---|---|---|
skip 行速率 | **≈1,750 行/小时/表**（实测 45.6 天窗口，样本 5,000） | 12 小时空闲窗口 skip 明细写入 **降 ≥95%**（A07 逐字） |
rows/s 总量 | 两表 skip 合计 ≈0.486 行/秒（45.6 天均值） | 改造后同长度窗口对比 |
WAL 增长 | **未采**（step4 实测：同长度窗口的 `-wal` 字节增量） | 不得恶化 |
DB busy / 写延迟 | **未采** | 不得恶化 |
dispatch latency | **无基线**（需先定采集点，step3/C0 探查） | 未取得基线前**不宣称**"不退化" |

## 6. Step 3（Change）切片序列（每个切片一次提交）
- **C0 探查（只读）**：`runtime_activity` skip 的消费方；新任务唤醒路径（Channel/事件）。
- **C1**：`SubconsciousJobQueue` 增内存 counter + `FlushSchedulingSkipSummaryAsync`（**不改**现有调用行为）。
- **C2**：`SubconsciousJobScheduler` 改为累积计数（去掉每次 skip 即时写入）。
- **C3**：移除 `runtime_activity` 的 skip 写入（**前置 = C0 结论**）。
- **C4**：接线 5 分钟 flush（复用既有循环节拍，不加新调度器）。
- **C5**：消费方适配（`agent_diagnostics` 口径、上述测试更新）。
- **C6 = step4 Test**：前后对比实测 + 验收映射。

## 7. 验收映射
| 标准 | 承载切片 | 证据形式 |
|---|---|---|
1. 10h no-op ≤120 行 summary | C1/C2/C4 | 空闲窗口实测计数（含样本量与窗口长度） |
2. 状态变化/错误保留明细 + trace；聚合不丢总数/原因分布 | C1/C3/C5 | 明细行抽样 + summary 计数与明细对账 |
3. 同事实单一 owner | C3 | 代码检索：`schedule_skip` 写入点从 2 处降至 1 处 |
4. rows/s、WAL、CPU/IO、dispatch latency | C6 | 前后对比表（含基线缺失项诚实标注） |
新增：降 ≥95% / 唤醒不变慢 / DB busy 不恶化 / first-last-window 齐备 / **不删业务记录**（历史 273.6 万行不动） | C2/C4/C6 | 同上 + `git diff` 证明无删除脚本 |

## 8. 风险与限定
- **plan_version 必须保持稳定**：recompile 使 `tp-561ea9576451d093f33057113865f791` 的 plan_version ≥3 会坠入缺陷 `5413ce1b` 的永久死锁 ⇒ 本步不动 plan。
- 平台 `objective-file-evidence` 判据把目标散文片段 `（A07 / §9.A07）。` 当作路径 `§9.A07）。` 检查存在性（见 Explore 文档 §6.2）；**不伪造该名字的文件**。
- dispatch latency 基线缺失 ⇒ 相关结论在取得基线前**保持 unknown**。
- 本步未改任何代码；所有改动集中在 step3 的 C0–C5。

---

## 9. Step 3 / C0 探查结论（迭代 5，**只读**；C2/C3/C4 的 fail-closed 前置）

### 9.1 `runtime_activity` 的消费面（实测：查询点只有诊断工具 3 处）
`RuntimeActivityQuery` 构造处全仓仅 3 处，均在 `Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs`：
- `:98` `tool_stats`（按 `tool_name` 元数据过滤）
- `:181` `slowest_tools`
- `:690` `diagnose`：`QueryAsync(new RuntimeActivityQuery { Limit = 2000 })` 取**最近 2000 条**，并**只保留** `Metadata["tool_name"]` 非空的条目做工具维度（`:692-700` 逐字）

其余命中全部是**写入方**（`_activitySink` 注入的 emit 侧）：`AgentExecutionService`、`DirectLlmClient`、`MemoryWriteCoordinator`、`SubconsciousPlanGenerationService`、`HookPublisher`、`EventDispatcher`、`InternalEventBus`。

**结论**：
1. 移除 skip 的 activity 写入**不破坏任何业务读取路径**（消费面只有诊断工具）。
2. **可核查的连带收益**：当前 77.2% 的噪声**正在挤占 `diagnose` 的"最近 2000 条"窗口** —— 降噪后工具维度诊断的可见性提升（这是"删噪声"之外的正当理由，不是为达标而达标）。
3. C3 需同步核查测试面对这两个动作的断言（`PuddingRuntimeTests` / `PuddingWebApiTests`）——属**测试面**，非产品路径。

### 9.2 `SubconsciousJobQueue.cs` 的 activity 写入点是**两处**，必须区别对待
- `:409`（在 `RecordSchedulingSkipActivityAsync` 内）→ **skip 噪声，C3 的移除对象**
- `:504`（`RecordActivityAsync` 路径）→ **必须保留**（A07 逐字「状态变化、派发、错误才写明细」）
⇒ C3 必须**精确到 `:409` 一处**，不得整体删除 activity 能力。

### 9.3 唤醒路径（C4 的关键事实，直接影响 Plan 裁定三）
- 既有 **coalesced signal 机制存在，但只服务 legacy 路径**：`SubconsciousConsolidationHook.cs:43-56` 构造 `ConsolidationJob` 后 `_channel.Writer.TryWrite(job)`（内存 `Channel`）。
- **durable 入队 `SubconsciousJobQueue.EnqueueAsync`（`:38-63`）是纯 DB 写入**（`SubconsciousJobs` 表 + 幂等键查询/插入），**不写 Channel、不发任何信号**。

**结论**：当前新任务的"唤醒"**实际依赖 2 秒轮询**。因此把轮询周期改为 5 分钟会使新任务唤醒最多延迟 5 分钟 ⇒ **违反** A07 逐字「任务到达唤醒不变慢」。⇒ **维持 Plan 裁定三**（保留 2 秒循环，只把"写入"改为"计数"）。若将来要实现真正的 coalesced 唤醒，C4 需**新增轻量 signal**（候选：worker 暴露 wake handle；或 durable 入队后向既有 `_channel` 写入轻量信号）—— **本步不定型、不改动**。

### 9.4 C0 限定
- 消费面结论覆盖范围：`Source/PuddingRuntime` 全目录（文件名过滤 `*.cs`）；`Source` 全量 grep 曾撞 2000 文件枚举上限并返回零命中，**该次结果不可作为否定证据**（已收窄目录后重查）。
- 本轮**未改任何生产代码**。
