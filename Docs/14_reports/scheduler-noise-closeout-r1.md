# P2 Scheduler 遥测降噪 — 收尾报告（closeout-r1）

| 字段 | 值 |
|---|---|
| goalRunId | `tg-59f90cc6ee8713427f3c7bc5130d68ab`（objectiveVersion 1） |
| iteration / 预算 | 28 / 32（剩 5 轮；steering 要求收尾） |
| taskId / assignmentId | `06898d5dfe004c69ab6d5baf18b2674a` / `ebd4e146b90f4d31bcfc31647fb9b9ae` |
| task 版本 / 进度 | v28，`InProgress`，progress 60% |
| workUnit | `tn-77de109db49badc89461226ee678c403`（kind=Explore, 1/5，`stepsPassed=0`） |
| goal 状态 | `blocked` / `criterion_failed`（+ 平台侧 `work_unit_budget_exhausted`） |
| 生产代码 | **零改动**（本卡至今全部交付物为文档/证据，无行为变更） |

---

## 1. 本轮交付物清单（全部已提交并推送）

| commit | 内容 | 推送 |
|---|---|---|
| `e49f8b9e` | step1 Explore 证据：双写点、触发链、273.6 万行实测基线 | ✅ |
| `fdb5ab6a` | Explore 增补：引用溯源 + 判据缺陷诚实登记 | ✅ |
| `f36ac743` | step2 Plan：5 项裁定 + 切片序列 + 验收映射 | ✅ |
| `79b920ec` | step3/C0：只读探查（消费面 / 写入点区分 / 唤醒路径） | ✅ |
| `94c972db` | **Explore checkpoint-v1**（证据 refs + SHA-256 指纹 + 终审结论） | ✅ |

本轮（28）新增：本收尾报告；并把「判据缺陷」转为**可追踪看板卡**（详见 §4.1）。

## 2. 已闭合的证据链（可复核）

| 事实 | 锚点 | 复核方式 |
|---|---|---|
| 2 秒空轮询驱动 | `SubconsciousWorkerService.cs:18`（`IdlePollDelay=FromSeconds(2)`），使用点 `:107/:133/:145` | `search_grep` |
| skip 唯一生产者 | `SubconsciousJobScheduler.cs:157`（全仓唯一 `RecordSchedulingSkipAsync` 出口） | `search_grep` |
| 8 个 skip 原因 | `SubconsciousJobScheduler.cs:39/49/64/103/108/131/171/173` | `search_grep` |
| **双写对** | `SubconsciousJobQueue.cs:202 → :209 activity / :210 metric`；维度字典共用 `:468` | `file_read` 398–460 |
| 必留明细路径 | `:91/92`、`:262/263`、`:395/396`、**`:504`（不得误删）** | `file_read` + `search_grep` |
| 契约影响面 | `RecordSchedulingSkipAsync` 的 fake 实现 **≥7 处**（含 `SubconsciousJobQueueTests.cs`） | `search_grep`（有枚举上限告警 ⇒ 下界） |
| 引用锚点存在 | `01-自主工作轨迹与自改进审计.md:250` = `### A07 / P1：后台空轮询写放大，沿现有卡收口` | `search_grep` |
| 内容指纹 | `WorkerService 54426b51…`｜`JobScheduler 95a362f6…`｜`JobQueue dded3e1d…`｜`JobQueueTests 135f33ba…` | `certutil -hashfile … SHA256`（exit=0） |

## 3. 未完成（明确不做虚假声明）

- **step3 代码切片 C1–C6 全部未开工**：无任何生产代码改动 ⇒ 四条验收（含新增验收）**均未达成**。
- **改造前基线未采集**：rows/s、WAL 增长、CPU/IO、dispatch latency 无数据 ⇒ 验收 1/4 与「不退化」类断言**当前不可判定**，本轮不做任何达标宣称。
- **内存计数器/5 分钟 summary/coalesced 注入点**仍停留在设计（`scheduler-noise-plan-step2-r1.md`）。

## 4. 阻塞与遗留清单

### 4.1 阻塞 A：判据把散文当路径（平台的判据提取缺陷）
`objective-file-evidence:§9.A07）。: file_evidence_missing` — 判据从目标「…（A07 / §9.A07）。」中抽出 `§9.A07）。` 当作文件名要求存在，恒不可满足，导致 Explore 节点 5 轮无法步进（`stepsPassed=0`）。
**处置**：已建可追踪看板卡 `a7aedfe1088d49eb9a5d5b99324abe7c`（type=defect，priority=p1，Backlog，2026-09-20T13:28:23Z 创建，含复现/根因/影响/验收标准 4 条）；已提交 `meta.goal_contract_proposal` 走契约精修通道；**拒绝**创建名为 `§9.A07）。` 的文件来凑检（证据完整性 + 仓库卫生）。

### 4.2 阻塞 B：工作单元预算
`task_update` 回执持续携带 `blocker_kind=work_unit_budget_exhausted`，`201617/150000`（input-token 口径；与 payload `maxInputTokens=1000000` 不同源）。跨 26 轮累积超支 ⇒ 需平台**授予预算或推进节点**；**不自行抬高**。

### 4.3 遗留（owner / 下一步）
1. **R1 判据修复**（平台）→ §4.1 卡片验收标准 1–3。
2. **R2 预算裁决**（平台）→ §4.2。
3. **R3 基线采集**（本卡下一轮，命令见 §5）→ 产出 AC1/AC4 的判定前提。
4. **R4 C1 计数器 + `FlushSchedulingSkipSummaryAsync`**（不改调用行为）。
5. **R5 C2 累积 → C3 精确移除 `SubconsciousJobQueue.cs:409` → C4 5 分钟接线 + coalesced → C5 同步 7 处 fake**。
6. **R6 C6 前后对比实测**（依赖 R3）。
7. **R7 唤醒路径**：`SubconsciousConsolidationHook` 的 coalesced 信号仅服务 legacy 路径，durable `EnqueueAsync` 不发信号 ⇒ 新任务唤醒实际依赖 2 秒轮询；**若把周期改 5 分钟会违反 A07「任务到达唤醒不变慢」**（step2 裁定三据此保留 2 秒消费循环、只改写入形态）。

## 5. 下一轮最小收口方案（剩余 29–32 轮）

- 若 R1 修复 → Explore 通过后**直接进 R4**（文档已备齐，不需再探查）。
- 若 R1 未修复 → 仍可执行 **R4/R5**（代码切片不依赖判据），但节点步进仍可能不计数。
- R3 基线采集（可直接执行、只读、有界）：
  1. `sqlite3 D:\data\databases\pudding_platform.db "select count(*) from runtime_activity where operation='subconscious_job.schedule_skip';"`（同表 `telemetry_metric_events` 以 `name` 计数）
  2. 记录 `t0` 计数 → 空闲 60 s → 记录 `t1` 计数 ⇒ `rows/s = (t1-t0)/60`；
  3. 同步记录 `-wal` 文件大小与进程 CPU/IO（前后各一次）；
  4. 业务 dispatch latency 用现有 trace/metric 口径采样，**改造前后同法**。

## 6. 诚实声明

本报告不声称任何验收项达标；未采集的指标一律标注为「未测」。所有 hash/行号/提交号均可按 §2 复核命令复现。
