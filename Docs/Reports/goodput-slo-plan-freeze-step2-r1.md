# Goodput SLO — Step 2 Plan Freeze（checkpoint-v1）

- goalRunId `tg-9ec9d27543747ec6849a52aae10d9f31`｜task `0b16740022f84b58a9532a87f1bc5509`｜stepNode `tn-ce8d4d92973feee54cf6a4318baf5f28`（**Plan**, seq 2/5）
- step objective 逐字：*"Freeze the implementation path, ownership boundaries and acceptance gates."*
- 上轮 verdict：`blocked / contract_coverage_insufficient`（`unmetCriteria` 空）；step1 已通过（`stepsPassed = 1/5`）
- 依据：step1 checkpoint `Docs/Reports/goodput-slo-explore-step1-checkpoint-r1.md`（commit `ec23514d`）+ 本轮结构探针（§2）

---

## 1. 目标与非目标
**目标**：把卡片三项 SLO（重复失败目标 −50%、耗时 P95 不恶化 >10%、首轮开销 −30%）从"无法度量"变为**有口径、有数据源、可复算**；并把「每 verified task 的输入/miss/输出/费用」打通到真实归因链。
**非目标（明确不做）**：不改 budget/熔断阈值；不改成本数值语义；不新建 Task/Goal 状态机；不给 `task_nodes` 新增 route/model 列；不动 `Source/PuddingAgent`（运行中宿主）。

## 2. 本轮新增结构事实（**逐字，只读探针 temp/ gitignored**）
| 表 | 行数 | 关键列（逐字） |
|---|---|---|
`execution_runs` | **2794** | `fencing_token, run_id, command_id, conversation_id, turn_id, attempt, worker_id, status, lease_until, snapshot_id, started_at, completed_at, terminal_sequence, trace_id` |
`task_nodes` | **126** | `task_node_id, plan_id, parent_task_node_id, depth, title, objective, expected_output_contract, assigned_to_kind/id, status, result_summary, result_artifact_ref, superseded_by_task_node_id, started_at, completed_at, sequence_no, work_unit_kind, depends_on_json, scope_json, required_capability_ids_json, max_rounds/max_tool_calls/max_duration_seconds/max_input_tokens/max_output_tokens/max_cost, retry_policy_json, **progress_fingerprint**, checkpoint_artifact_ref` |
`work_unit_await_handles` | **0（表已存在）** | `await_handle_id, plan_id, task_node_id, kind, external_id, status, fencing_token, metadata_json, created_at_utc, updated_at_utc, signaled_at_utc, consumed_at_utc` |
`goal_iterations` | **147** | `goal_iteration_id, goal_run_id, activation_epoch, iteration_no, status, command_id, turn_id, **run_id**, trace_id, accepted_sequence, terminal_sequence, stop_reason, error_id, started_at_utc, settled_at_utc, llm_rounds, tool_calls, input_tokens, output_tokens, **progress_fingerprint**, created_at_utc` |

**两条**由此得出的关键判断**：
1. `task_nodes` **无任何 provider/model/route 列** ⇒ WorkUnit 级「route 记录位」不存在（与我在路由卡上的悬置问题一致）。
2. **`work_unit_await_handles` 表已建但 0 行** ⇒ 「待等 identity / 触发条件 / 无忙轮询」的**载体已就位、机制未接线** —— 这比"不存在"好得多：应**接线写入**，而不是新造表。

## 3. 归因桥接（本卡核心可行性判断）
- 运行时观察：`TokenUsageEvents.SourceId = "{sessionId}:{32-hex}:{round}"`（该段出现 **13** 次；**从不位于串首**，`LIKE '{seg}%'` = 0）。
- 代码逐字（`Source/PuddingRuntime/Services/AgentExecutionService.cs:2102`）：
  `sourceId: $"{request.SessionId}:{request.ExecutionIdentity?.RunId ?? "no-run"}:{round + 1}:warm-prefix",`
  ⇒ **强证据表明中段 = `RunId`**；而 `execution_runs.run_id`、`goal_iterations.run_id` 均存在。
- ⇒ 预期可行链路：`SourceId 中段 → run_id → goal_iterations(goal_run_id/iteration_no) → goal_runs → task binding → task_nodes(WorkUnit)`。
- ⚠️ **未最终验证**：我的验证查询取了 `PRAGMA table_info[0]`（= `fencing_token`，整数列）作主键，故 `WHERE fencing_token='e01c…'` 必然为空 —— **该空结果是查询缺陷，不是否定证据**。精确验证语句见 §7。

## 4. 实现路径冻结（P1–P6，按依赖排序；每项一个原子迭代）
- **P1 归因桥接（最高优先）**：先跑 §7 的验证 SQL；成立后实现**只读**聚合（SQL 视图或后台聚合服务），产出「按 task_node/plan/任务族聚合的 token / cache-miss / 成本」。
  约束：只读、不改写入路径、不新增 route 列。
- **P2 缺价可区分**：新增价格状态（`priced` / `free` / `unpriced`）落库并使 0 成本可分辨（当前 7725 条零成本中 **93% 来自未注册 provider** ⇒ 属 `unpriced`）。
  约束：**只新增标记，不改既有成本数值语义**；旧行标 `unknown`；**不得**用该标记改写历史成本。
- **P3 心跳投影**：**复用** `Docs/Reports/tracker-watchdog-step2-heartbeatoutcome-design-r1.md` 的 `HeartbeatOutcome` 设计（原则：既有 canonical 事实的投影，**不是新状态机**）。
  `awaiting_external` 的载体 = `work_unit_await_handles`（接线写入）；`recovered` = 熔断/lease 恢复事件；`progressed` = `progress_fingerprint` 变化；`failed` = 迭代 `error_id`/`stop_reason`；`no_ready_work` = 调度 scan 决策码（`TaskSchedulerDecisionCodes`）。
- **P4 时间账派生（只读口径）**：active work / LLM wait / tool wait / queue 全部**从既有时间戳派生**，不新增采样：
  queue = `goal_outbox.due_at_utc → claimed`；active = `execution_runs.started_at → completed_at`；
  LLM wait = `rate_limit_wait_ms` + `stream_first_chunk_wait_ms`；等待 = `goal_iterations.started_at_utc → settled_at_utc` 减去 active。
- **P5 三项 SLO 度量定义（**必须连口径一起交付**）**：
  ① 重复失败目标 −50%：分子 = 同 taskType 内 `blocked` 且 `blocker_code` 重复的迭代数；分母 = 同族迭代数；**必须同时报告前后窗口与样本量**。
  ② 耗时 P95 不恶化 >10%：口径 = `goal_iterations.settled_at_utc − started_at_utc`；**必须附样本量与难度说明**（卡片原文要求）。
  ③ 首轮开销 −30%：口径 = 每个 Goal 的首个 iteration 的 `input_tokens/output_tokens/cost`。
- **P6 对账缺边补齐**：`Task→…→Run` 已可用（`TaskExecutionTracker`），缺「→模型」⇒ 用 `run_id → SourceId → TokenUsageEvents.provider_id/model_id` 补，**不新增 route 列**（复用既有唯一事实源）。

## 5. 所有权边界（**冻结**）
1. **不新建 Task/Goal 状态机**（沿用 `HeartbeatOutcome` 投影原则）。
2. **只读优先**：P1/P4/P5 全部只读；只有 P2（新增标记列）与 P3（`work_unit_await_handles` 接线）涉及写入。
3. **只增列、不改语义**：P2 不得改变既有成本数值或历史行。
4. **不改 budget/熔断阈值**；不动 `Source/PuddingAgent`。
5. **源码交付 / 已加载构建 / 产品验收分开登记**（卡片原文要求），三者不得互相冒充。

## 6. 验收门 ↔ 卡片标准
| 卡片标准 | 冻结的门 |
|---|---|
同固定任务族前后质量一致 | P5① 的分母/窗口固定 + 质量口径与成本口径同源 |
重复失败目标 −50% / P95 不恶化 >10% / 首轮开销 −30% | P5 三项口径齐备 + 样本量与难度随报告给出（**无样本量不得下结论**） |
全部心跳可解释 / 等待无忙轮询 | P3 五分类投影落地 + `work_unit_await_handles` 有写入（**当前 0 行**）+ P4 时间账可算 |
0usage/缺价/未知归因不被当节省 | P2 标记落地 + 复算证明「93% 零成本属 unpriced」不再被计入节省 |
任务与真实 worker/assignment/Run 可对账 | P1 + P6：Task→…→Run→模型的端到端单条 SQL 可复算 |

## 7. 唯一待验证项（**进入 step3 的第一个动作**）
```sql
-- 用正确列名验证中段 = run_id（注意 execution_runs 主键顺序首列是 fencing_token，勿再用它）
SELECT run_id FROM execution_runs WHERE run_id = 'e01cde260def42d4bf2b62dddc7ac756';
SELECT goal_run_id, iteration_no, run_id FROM goal_iterations WHERE run_id = 'e01cde260def42d4bf2b62dddc7ac756';
```
成立 ⇒ P1 按预期链路实现；不成立 ⇒ P1 改为经 `snapshot_id`/`command_id` 或 session+时间窗桥接（并记录该事实）。

## 8. 未解决风险
R1 归因中段身份未最终确认（§3/§7）—— 本卡可行性的唯一硬前提。
R2 `work_unit_await_handles` 0 行 ⇒ 若接线失败，`awaiting_external` 仍无载体（退化到"不可解释"）。
R3 P2 新增标记涉及 schema 变更 ⇒ 必须走既有 bootstrap 迁移模式，避免破坏既有库。
R4 本卡 plan `tp-c56870cfa30198ae26c3c987a899e395` 的 `plan_version` 须保持稳定（recompile ≥3 会触发缺陷 `5413ce1b` 死锁）。
R5 `goal_iterations.progress_fingerprint` 历史上 100% NULL（旧观测 0/110）⇒ `progressed` 判定在当前数据下可能仍不可用，需在 P3 中先确认填充率。

## 9. 诚实限定
- 本轮**纯只读**（探针为 temp/ 临时件），未改产品代码与数据库。
- §2 的行数/列名为**单库单时刻**快照；列清单逐字来自 `PRAGMA table_info`。
- §3 的"中段 = RunId"是**代码逐字 + 形状吻合**的强推断，**尚未被查询证实**；§7 给出精确验证语句。
- 我的两次探针脚本均因**假设了错误的表名/列名大小写**（PascalCase vs snake_case）产生 ERR 与一次无效查询 —— **已如实标注，未当作数据结论**。
