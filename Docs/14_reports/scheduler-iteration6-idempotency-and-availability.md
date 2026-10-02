# Scheduler 迭代 6 证据：per-intent 幂等核对（通过）+ 可用性自锁数据（2026-09-20）

- GoalRun `tg-2efcdf7d329d8387632a67bcbfbdbe2b`｜task `3bd2a4b0ef5f4bff8f175fb7655927ad`
- 数据源：`D:\data\databases\pudding_platform.db`（只读 `mode=ro`）｜探针 `temp/db-probe-idempotency-avail.py`

---

## 1. 验收标准 2 的幂等半边：**通过**（并补上上一轮标为"待验"的判据）

上一轮我把「同一 intent 至多启动一个 goal_run」标为待验，本轮已核对：

```
task_scheduler_intent_outcomes
  total_outcomes = 517      distinct_intent_id = 517      ⇒ 每个 intent 恰好一条结算记录
  intents_with_multiple_outcomes              = 0   ✅
  goal_run_started_by_multiple_intents        = 0   ✅
  assignment_started_by_multiple_intents      = 0   ✅

outcome 分布: ineligible 457 | terminal 29 | noop 18 | deferred 12 | denied 1
reason_code 分布: not_opted_in 433 | completed 27 | availability_refreshed 18 | status_blocked 16 |
                  task_not_yet_eligible 6 | status_backlog 6 | preferred_busy 6 | archived 2 |
                  status_inprogress 1 | is_container 1 | execution_plan_unavailable 1
```

⇒ **无重复结算、无重复启动**；12 类拒绝/收敛原因**全部有稳定 reason code**（标准 5 的「每个拒绝均有 reason code」面同样成立）。
注意上一轮我把 task 级 7 条 binding 误当作疑似重复，本轮按 **intent 粒度**核对证明是顺序重试而非重复启动 —— 判据选对了，结论随之反转。

## 2. 新发现的缺口（**待查，尚未定性为缺陷**）

```
distinct_started_goal_run_id   = 0        -- 517 条 outcome 中全为 NULL
distinct_started_assignment_id = 0        -- 同上
outcome = 'started' 的行数      = 0
```

即**没有任何结算记录携带「本次启动了哪个 goal_run / 哪个 assignment」**，尽管我们知道该路径确实在启动执行
（`task.reserved`+`task.assigned` @09:30:27，且 `task_scheduler_decisions` 有 `eligible` 决策、`goal_runs` 有 22 条）。

⇒ 对标准 5「**可回放对账**」而言：**拒绝路径的 reason code 完备，成功路径的 per-intent 溯源链缺失**。
**本轮未追到代码层**（未确认是「成功启动不写 outcome」还是「outcome 词汇表另有取值」），故**标为待查**，不作缺陷结论。

## 3. availability 投影：`preferred_busy` 自锁得到数据闭环

`agent_availability_projection` 全 8 行（`observed_at_utc` ≈ 2026-09-20 10:25:39~40，`valid_until` +30 s）：

| agent_id | state | reason_code | idle_since |
|---|---|---|---|
`default.global_general-assistant.6a8`（**本 Agent**，全 5 张 Ready 卡的 preferred） | **2** | `idle_confirmed` | 2026-09-20 10:25:39 |
`default.audit-agent.001` | 2 | `idle_confirmed` | 2026-09-19 21:12:54 |
`default.global_general-assistant.258` | 1 | `agent_disabled` | — |
`default.global_general-assistant.0e0` | 1 | `agent_disabled` | — |
`…-sub-*` × 4 | 1 | `agent_configuration_missing` | — |

⇒ 全工作区**可调度的 Agent 实际只有 2 个**（本 Agent 与审计员），其余要么 disabled，要么是残留的陈旧投影行（观测时间停在 08-27 / 09-03 / 09-12）。
而 5 张 `Ready` 卡的 `preferred_agent_id` **全部是本 Agent**；扫描每 5 分钟一次，**恰好落在本 Agent 执行 turn 的时段内** ⇒ 每次都是 `preferred_busy`。

⇒ **「能开工的 Agent 永远是正在忙的那个」在数据上成立**：不是缺少调度机制，而是**可用性/硬亲和策略**问题。
对标准 4（authoritative 下连跑 10 个任务）而言，解法是**可用性感知的软亲和回退**（preferred 忙时降级到其它合格 Agent）或**并发槽**，而不是新增机制。审计员虽 idle 近 13 小时，但按硬亲和完全没有机会接手（且它对这些 `implementation` 卡是否满足 `AllowedRoles:["Service"]` 未核）。

## 4. 对 Plan 的输入（累计）

| 标准 | 现状 |
|---|---|
1 | 口径需重定：195 张 Backlog 中 194 张 `auto_dispatch_enabled=0` ⇒ `not_opted_in`；`general` 缺路由须结构化拒绝 |
2 | **延迟面通过**（p95 1.98 s ≪ 30 s）＋ **幂等面通过**（517/517，零重复） |
4 | 卡点是可用性硬亲和自锁（本轮数据闭环），非缺机制 |
5 | 拒绝路径 reason code 完备；**成功路径 per-intent 溯源缺失（待查）** |

## 5. 诚实限定
- 全部为只读快照；`agent_execution_reservations` 仅抽样（所见均 `status=released`），**未统计全量状态分布**。
- §2 未做代码级核实，故标「待查」；§3 的 `state=1/2` 语义由 `reason_code` 反推，未读枚举定义。
