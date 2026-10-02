# Scheduler 运行时回放（2026-09-20）—— 待决项 (3) 收口 + Plan 前置结论

- 关联：GoalRun `tg-2efcdf7d329d8387632a67bcbfbdbe2b`｜task `3bd2a4b0ef5f4bff8f175fb7655927ad`
- 数据源：`D:\data\databases\pudding_platform.db`（**只读** `mode=ro`）；探针 `temp/db-probe-sched-attrib.py`、`temp/db-probe-sched-attrib2.py`
- 方法修正：首版探针 `limit 8` 未排序 ⇒ 取到最早 8 条而非最新；已改为 `order by created_at_utc desc`。**未排序的 limit 会给出误导性结论**，此处记为方法教训。

---

## 1. 结论摘要

| # | 结论 | 性质 |
|---|---|---|
| C1 | 09:30:27Z 的 Reserved/Assigned **由自动调度产生**，全链路可回放 | 待决项 (3) **已解决** |
| C2 | Backlog Refinement **已自动运行**并把本卡从 Backlog 推至 Ready | 任务卡「当前问题」再被推翻 |
| C3 | 生产 scan 的 `backlog: 0` **不是缺陷**，而是 194/195 张 Backlog 未 opt-in | 验收标准 1 口径须重定 |
| C4 | 真实障碍是 **`preferred_busy` 自我锁定**：5 张 Ready 卡的 preferred agent 全是本 Agent，而它正是执行者 | 验收标准 4 的实际卡点 |

## 2. C1：自动调度全链路证据

决策表 `task_scheduler_decisions`（本任务，最新 6 条，DESC）：

| created_at_utc | phase | mode | decision | decision_code |
|---|---|---|---|---|
| 2026-09-20T09:30:27.0415Z | candidate | authoritative | **eligible** | `eligible` |
| 2026-09-20T09:25:26.8804Z | candidate | authoritative | deferred | `task_not_yet_eligible` |
| 2026-09-20T09:22:14.9878Z | candidate | authoritative | deferred | `preferred_busy` |
| 2026-09-20T09:17:17.5671Z | candidate | authoritative | deferred | `task_not_yet_eligible` |
| **2026-09-20T09:17:14.8944Z** | **refinement** | authoritative | **ready** | `ready_for_auto_dispatch` |
| 2026-09-20T09:17:14.7465Z | candidate | authoritative | deferred | `preferred_busy` |

对齐任务事件（`task_get`）：`task.reopened` 09:14:34.9255Z → `task.ready` 09:17:14.4130Z → `task.reserved` + `task.assigned` **09:30:27.1827Z**。
⇒ `eligible` 决策 09:30:27.0415Z 与 reserved/assigned 09:30:27.1827Z 相差 **约 141 ms**，即「决策 → 原子启动事务」在毫秒级完成。

`eligible` 决策的 `score_breakdown_json` 含 `priority/dueUrgency/age/criticalPath/retryPenalty…`，`reason` 为
`code=eligible;task_type=implementation;dependency=satisfied;availability=…` ⇒ **任务卡要求的评分分项、deny/defer code、`next_eligible_at_utc` 均已持久化**（deferred 行均带 `next_eligible_at_utc`）。

## 3. C2：Refinement 已在生产自动运行

09:17:14.8944Z 的 `phase=refinement` / `decision=ready` / `decision_code=ready_for_auto_dispatch` 行，**发生在本卡被 reopen（09:14:34）之后约 2 分 40 秒**，且 task 事件紧随其后出现 `task.ready`。
⇒ 任务卡「新增 Backlog Refinement」并非待建能力，而是**已在运行的既有能力**（对应 `TaskAutoDispatchScanRunner.PromoteBacklogAsync → TaskBacklogRefinementStore`）。

## 4. 今日（2026-09-20）决策与结算分布

`task_scheduler_decisions`：
```
preferred_busy              52
task_not_yet_eligible       12
ready_for_auto_dispatch      6
eligible                     1
```

`task_scheduler_intent_outcomes`：
```
ineligible / not_opted_in          205   ← 见 §5
terminal   / completed              16
deferred   / task_not_yet_eligible    6
ineligible / status_backlog           6
ineligible / is_container             1
```

## 5. C3：`backlog: 0` 的解释（194/195 张未 opt-in）

`workspace_tasks` 交叉分布：

```
status=0(Backlog)  auto_dispatch=0 -> 194     <-- 绝大多数
status=0(Backlog)  auto_dispatch=1 ->   1
status=1(Ready)    auto_dispatch=1 ->   5     <-- 即 scan 报的 candidates=5
status=6(InProgress) auto_dispatch=1 -> 1     <-- 本任务
status=8(Completed) auto_dispatch=0 -> 15
status=11(Archived) auto_dispatch=0 -> 49 ; auto_dispatch=1 -> 2
```

⇒ 194 张 Backlog 因 **`auto_dispatch_enabled = 0`** 被判 `ineligible / not_opted_in`（205 次），因此 scan 的 `backlog` 计数为 0，且它们**永不进入候选**。
**这不是 evaluator 的缺陷**（`status IN (Ready, Deferred)` 是设计），而是「未 opt-in 的卡是否应获得 refinement 状态投影」这一口径问题。

## 6. C4：真实障碍是 `preferred_busy` 自我锁定

近三次 scan（`trigger=recovery_scan`、`mode=authoritative`、`status=succeeded`、耗时 136–676 ms、间隔约 5 分钟 = 配置 `ScanInterval 00:05:00`）：

```
backlog=0  candidates=5  eligible=0  started=0  tracked=1
decision_codes_json = {"preferred_busy": 5}
```

5 张 Ready 卡的 `preferred_agent_id` **全部是 `default.global_general-assistant.6a8`**（见 `§5` 关联查询 D 段），而该 Agent **正是当前执行本 Goal 的 Agent**，长期处于 busy。
⇒ authoritative 模式下形成**自我锁定**：能开工的 Agent，永远是正在忙的那个。（今日 52 次 `preferred_busy` 即此现象。）

## 7. 对验收标准的影响与 Plan 建议

| 标准 | 影响 |
|---|---|
| 1（34 个 Backlog 全部可解释 refinement） | 口径须重定为：**195 张中 1 张已 Ready 路径走通**；194 张未 opt-in 的卡属于 `not_opted_in`，需先决定它们是否纳入调度 |
| 2（P95 30s / crash 幂等恢复） | 现成证据有利：scan 136–676 ms；outcomes 表已按 intent 落库 |
| 4（authoritative-single 连跑 10 个任务、Heartbeat=0） | **实际卡点是 `preferred_busy` 自锁**，而非缺少机制 ⇒ 需 availability 并发槽设计，或放宽 preferred 硬亲和 |
| 5（可回放对账、每拒绝有 reason code） | **已具备**：decisions/outcomes 双表 + score_breakdown + `next_eligible_at_utc`，本文件即样本 |

**建议 Plan 优先处理**：① 194 张未 opt-in 卡的口径裁定；② `preferred_busy` 自锁的可用性策略；③ 复核「剩余施工两个 commit」是否已实质完成（`task_scheduler_intent_outcomes` 已在用、三件套已铺开）。

## 8. 诚实限定

- 均为**只读**快照，非审计基线；`workspace_tasks` 计数含 2026-09-20 我执行的一次 DB 级恢复（197 张由 Cancelled 改回 Backlog）。
- C1 的因果链为「时间戳毫秒级对齐 + 决策码语义」推断，**未**做代码级调用栈验证；如需强证需在 `TaskAutoDispatchStarter` 加 trace。
- 本文件不修改任何产品代码；探针脚本位于 `temp/`（gitignored）。
