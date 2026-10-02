# 计划版本自毁循环 + 预算轴实测（2026-09-20，只读取证）

- 关联：GoalRun `tg-dab61d6e65ba7c0b5d3b355077b7c5e5`（本卡）与 `tg-2efcdf7d329d8387632a67bcbfbdbe2b`（Scheduler）
- 数据源：`D:\data\databases\pudding_platform.db` 只读｜探针 `temp/db-probe-blocker-axis.py`、`db-probe-blocker-axis2.py`

---

## 1. Q1 有结论：**输入累计未触发任何预算阻断**（支持 A1 的声明）

`tg-2efcdf7d` 全 **32** 次 verdict 的 blocker 预算轴统计：

```
budget_axis_dist = { "COST": 1, "other": 31 }      # 无一条消息含 "input Token budget"
```

唯一的预算耗尽（两个 goal 各 1 次）**都在成本轴**：

| goal | iteration | blocker_message |
|---|---|---|
`tg-dab61d6e` | 1 | `WorkUnit cost budget exhausted (1.005420/1.000000).` |
`tg-2efcdf7d` | 1 | `WorkUnit cost budget exhausted (1.001452/1.000000).` |

⇒ 与 `TaskExecutionPlanContracts.cs:53` 的声明「cumulative input is a usage metric, not a remaining allowance」
**一致，且当前无反例**；卡片要求「不再把输入累计耗尽当作阻断原因」在这两段运行中成立。

**诚实限定（两条未解）**：
- `goal_verifications` 的 `input_tokens / output_tokens / cost` 列**全为 0**（未填充），故用量无法从该表读出；
  §1 的结论建立在 **blocker_message 文本**上，而非用量列。
- `task_update` 曾**三次回显**同一粘滞串 `WorkUnit input Token budget exhausted (1020083/1000000)`
  —— 该串在上述 32 条 verdict 中**并不存在**，来源未明（疑为 task 级投影或更早的其它运行），**未解**。

## 2. 新 P0：计划版本「自毁型」死循环（吃掉 27/32 迭代）

`tg-2efcdf7d` 的 iteration 6–32（共 **27** 次）全部为：

```
blocker = execution_protocol_error
task_execution_plan_version_unsupported:
  plan=tp-0c58dc16df38cd734e3e241a51c7ce89 version=N;
  recompile the execution plan before starting a new attempt.
```

`N` **随重编译单调递增**，每个 N 恰好重复 4 次（N=9 为 3 次）：

```
N=3 → it 6,7,8,9      N=6 → it 18,19,20,21
N=4 → it 10,11,12,13  N=7 → it 22,23,24,25
N=5 → it 14,15,16,17  N=8 → it 26,27,28,29
                      N=9 → it 30,31,32
```

### 语义矛盾（核心）
平台给出的修复指引是「**recompile the execution plan**」，但每次重编译都把 `version` 抬高，
而运行时**只接受特定版本** ⇒ **修复动作使阻塞更严重，循环不可自愈**。已消耗该 Goal 27/32 的迭代预算。

### 与代码的对照（**疑为**，未读校验代码，故不断言）
`Source/PuddingCore/Scheduling/TaskExecutionPlanContracts.cs` 中
`TaskExecutionPlanSnapshot.CurrentSchemaVersion = 1`、**`CurrentPlanVersion = 2`** 是**语义版本常量**。
被拒绝的值 3..9 逐个递增、与「重编译次数」同序，**高度疑似把「计划修订号」当作「计划版本」比较**（或反之）。

### 对本卡门禁的影响
卡片头号剩余门禁是「**新 PlanVersion=2 task-bound 真实运行**」。本次观测表明：
**该版本门禁确实在运行**（已在真实路径上拒绝），但观测到的是**拒绝分支**且值域为 3..9 —— 因此
该门禁当前状态是「**门禁生效、但无法通过**」，需平台修复后重测。

## 3. 待查（交 step2 / 平台）
1. 计划版本校验的确切代码位置：`TaskExecutionPlanCompiler` **写入**的 version 字段 vs 校验侧**读取**的字段是否同源。
2. 粘滞串 `input Token budget exhausted (1020083/1000000)` 的来源（§1 未解）。
3. `goal_verifications` 用量列未填充（`input_tokens/output_tokens/cost` 全 0）是否为设计。

## 4. 诚实限定
- 全部只读快照；未运行构建/测试；未修改任何产品代码；未重启。
- §2 的「疑为修订号/版本号混淆」是基于值域单调性的**推断**，非代码级断言。

---

## 5. 根因坐实（代码 + DB + 行为，三点交叉）

### 5.1 校验侧（`Source/PuddingPlatform/Services/ExecutionCommandReader.cs:98-102`）
```csharp
var plan = await db.TaskPlanRuns.AsNoTracking()
    .SingleOrDefaultAsync(item => item.PlanId == binding.TaskPlanId, ct);
if (plan is not null && plan.PlanVersion != TaskExecutionPlanSnapshot.CurrentPlanVersion)
    throw new InvalidOperationException(
        $"task_execution_plan_version_unsupported: plan={plan.PlanId} version={plan.PlanVersion}; recompile the execution plan before starting a new attempt.");
```
⇒ 比较的是 **`task_plan_runs.plan_version` 列**与**语义常量** `CurrentPlanVersion`。

### 5.2 写入侧（`Source/PuddingPlatform/Services/Scheduling/TaskExecutionPlanCompiler.cs`）
```csharp
:87   planVersion = TaskExecutionPlanSnapshot.CurrentPlanVersion,   // 写的是常量（=2）
:103  PlanVersion = material.planVersion,
```

### 5.3 表结构：两个独立列
`task_plan_runs` 列含 **`plan_version`** 与 **`schema_version`**（`tp-0c58dc16…` 行：`plan_version=9`、`schema_version=1`）。
该行其余事实：`status=Failed`、`failure_code=accepted_iteration_budget_exhausted`、`failed_stage=settlement`、
`error_message="Goal accepted-iteration budget exhausted."`、`workspace_task_version=53`。

### 5.4 全库分布（18 个 plan）
| plan | plan_version | 数量 |
|---|---|---|
16 个旧 plan | **1** | 16 |
`tp-a2cd4086ee2ae1ac6846cc1e8128a1f5`（**本卡**） | **2** | 1 |
`tp-0c58dc16df38cd734e3e241a51c7ce89`（Scheduler） | **9** | 1 |

### 5.5 行为三点交叉验证（决定性）
| plan_version | 观测到的运行行为 |
|---|---|
**2**（本卡） | 与常量相等 ⇒ Goal **能正常迭代**（verdict 为 `criterion_failed` / 成本轴耗尽，**无** `protocol_error`） |
**9**（Scheduler） | 恒不等 ⇒ iteration **6–32 全部** `execution_protocol_error` |
**1**（16 个旧 plan） | 被拒 —— 恰好符合 A1「旧冻结计划显式拒绝并要求新计划」的本意 |

⇒ **`plan_version` 的实际语义是「该任务计划的修订号」**：首次编译 = 1，每次重编译 +1。
校验侧把它当作**语义版本**去比常量 2 ⇒ 结果只取决于修订号**恰好是否等于 2**：
恰好为 2 则通过（本卡），≥3 则永久拒绝，且**每次 recompile 使其更大**。

### 5.6 结论
`recompile the execution plan` 这条指引**在数学上不可能收敛**：目标值 2 只能靠「恰好重编译过一次」达成，
而该指令要求的修复动作每次 **+1**。已吃掉 Scheduler Goal 的 27/32 迭代预算。

## 6. 修复建议（对应卡 `5413ce1b`）
1. **概念分离**：新增 `plan_semantic_version`（写 `CurrentPlanVersion`）与 `plan_revision`（每次重编译 +1）；校验侧只比较前者。
2. **或最小改动**：校验侧改读语义来源（计划快照内的 `PlanVersion` / `schema_version`），不再读修订号列。
3. **拒绝原因区分**：`..._outdated`（旧语义版本）与 `..._unsupported`（不可识别值）分开；并同时输出 semanticVersion 与 revision。
4. **迁移路径**：对既有 `plan_version ≥ 3` 的计划提供重编译到语义版本的通道，否则这些任务**永久不可执行**。

---

## 7. 门禁 #2「输出/成本有限零值**不可绕过**」证据（2026-09-20 补）

### 7.1 代码语义（`Source/PuddingRuntime/Services/AgentExecution/ExecutionUsageBudgetTracker.cs`，逐字）
- 类注释：provider call 是**最小可执行边界**；**一旦触限，不再启动任何工具或后续 LLM 轮**。
- 判定用 `>=`（剩余恰为 0 即停止）：
  - 输出：`budget.HasOutputLimit && OutputTokens >= budget.MaxOutputTokens`
  - 成本：`budget.HasCostLimit && Cost >= budget.MaxCost`
- **「未设置」与「有限零值」由独立的 `HasOutputLimit` / `HasCostLimit` 表达**，不用 `> 0` 判断。
- 失败关闭：供应商无 usage payload ⇒ `WorkUnitUsageUnavailable`；设了成本限额但无冻结定价 ⇒ `WorkUnitPricingUnavailable`。
- 输入轴：按 `PeakRoundInputTokens`（**单轮峰值**）比较，消息中**另列** `cumulative input=` ⇒ 与 A1 声明一致。

### 7.2 行为级断言（**已存在**，测试名逐字）
| 测试 | 断言要点 |
|---|---|
`CreateRemainingBudget_ExhaustedOutputRemainsLimitedAtZero` | `MaxOutputTokens=0` 且 `HasOutputLimit=true`，且 `new ExecutionUsageBudgetTracker(remaining).EvaluateBeforeRound().ShouldStop == true` ⇒ **0 剩余仍受限、立即停止** |
`CostExhaustedAtZeroRemainsLimitedThroughDelegationAndSerialization` | 成本 0 剩余经**委派与序列化**后仍保持受限 |
`UnsetCumulativeAxesRemainUnsetThroughDelegationAndSerialization` | 未设置轴保持未设置（区分「有限零值」的另一半） |
`InputCapacity_IncludesExactBoundary_AndAccountsRejectedRequest` | 精确边界 + 被拒请求记账 |
`EvaluateBeforeRound_CostBudgetWithoutPricingFailsClosed`、`Record_MissingProviderUsageFailsClosedWhenBudgetIsActive` | 两条**失败关闭** |
`SixHundredRequests_KeepCumulativeLedgerWithoutExhaustingInputCapacity` | 600 次请求的累计账本**不**触发输入容量 |
`Record_AccumulatesInputWithoutConsumingPerRequestCapacity` | 输入累计不消耗单请求容量（A1 核心声明） |

### 7.3 运行时证据（真实发生过的一次阻断）
观测到的 `WorkUnit cost budget exhausted (1.005420/1.000000)` 与成本分支格式串
`$"WorkUnit cost budget exhausted ({Cost:F6}/{budget.MaxCost:F6})."` **逐字一致**
⇒ 该路径在**已部署运行时**中确实生效并真实阻断过一次迭代（不只是单测成立）。

### 7.4 未覆盖（诚实）
- **输出轴的运行时观测缺失**（目前仅有单测）。
- 卡片要求的「**600 轮产品验收**」需长程真实运行，本轮未执行；不得以只读证据替代。
- 本轮未运行测试套件（纯只读）。

## 8. Q1 遗留项收口：粘滞串来源已查明
- HEAD 中输入轴消息为 `WorkUnit per-request input Token capacity exceeded ({peak}/{capacity}); cumulative input={...}`，判据是**单轮峰值**。
- 全仓检索 `input Token budget exhausted`，**唯一命中是文档注释**：
  `Source/PuddingPlatform/Services/Goals/GoalSettlementStore.cs:35`
  `/// <summary>Error message from the terminal turn.failed payload (e.g. WorkUnit input Token budget exhausted).</summary>`
  ⇒ 该串是**存储型字段（terminal `turn.failed` 的 error message）里的历史数据**，
  **不表示仍有活代码把输入累计当作剩余额度**。
- 结论：`task_update` 三次回显的同一数值 `1020083/1000000` 属**陈旧存储值**，与 §1 的结论（预算阻断均由成本轴触发，输入轴 0 次）**不冲突**。
- 限定：未读该字段的写入/刷新策略，故仅断定为「非 HEAD 活代码产生」。

---

## 9. **更正（撤回 §8 结论）**：该串由**活代码**产生

2026-09-20 19:01 的 `task_update`（task `77883a50`）返回：
```
blocker_kind   = work_unit_budget_exhausted
blocker_reason = "WorkUnit input Token budget exhausted (187495/150000)."
```

- **数值与分母都变了**（旧 `1020083/1000000` → 新 `187495/150000`，`150000 ≠ 1000000`）
  ⇒ 该消息**实时产生**，§8 的「陈旧存储值 / 非活代码产生」结论**不成立，予以撤回**。
- 它与 HEAD 的措辞不一致（HEAD 输入轴为 `WorkUnit per-request input Token capacity exceeded (…); cumulative input=…`，
  判据是**单轮峰值**）⇒ **发出者不是 HEAD 的实现**。这与本文件已记录的**部署滞后**一致
  （Core PID 23280 早于 2026-09-20 的 `b81c69bf`，即运行的是旧产物）。
- **Q1 的适用范围必须收窄**：可保留的仅是「`goal_verifications` 的 32 次 verdict 中**没有一次**由输入轴触发」；
  **不能**再据此断言「输入累计在任何活路径上都不再被当作额度」—— 现存在一个**活的反例字符串**，
  且其分母 `150000` 来源不明。
- **待查（高优先）**：①发出者是谁（HEAD 之外的哪个组件／旧产物）；②分母 `150000` 的配置来源；
  ③它是否构成真实阻断（本轮 turn 仍正常完成，故至少不是硬阻断，或阻断发生在会计/汇报侧）。
- **方法论教训**：「全仓字面检索零命中」只能证明**HEAD 没有该字面量**，**不能**证明「没有活代码产生该消息」——
  运行中的产物可能比 HEAD 旧。判断消息是否活体，要看**它是否携带会变化的数据**（本例分母变了）。

---

## 10. step2 取证（Tracker 卡）：nextAction 缺口 + objective_evidence 第二例 + 活体 blocker 语义

### 10.1 验收标准 1 的 **nextAction 面确认缺失**
`TaskExecutionTrackingDecision`（`Source/PuddingCore/Scheduling/TaskExecutionTrackingContracts.cs:19`）字段（逐字）：
`WorkspaceId/TaskId/AgentId/AssignmentId/GoalRunId/ReservationId/TaskPlanId/ExecutionPlanFingerprint/ExecutionPlanStatus/`
`WorkUnitKind/WorkUnitStatus/TaskStatus/GoalPhase/IterationStatus/CommandStatus/RunStatus/ExecutionRunId/`
`ExecutionFencingToken/ExecutionBindingId/TaskVersion/OutboxStatus/OutboxId/**Verdict**/**Code**/ObservedAtUtc/LastProgressAtUtc`
⇒ **`Verdict`(state) 与 `Code`(reason) 齐备，但没有 `NextAction`**。
verdict 枚举 5 项：`Healthy / Waiting / Stalled / Inconsistent / CleanupRequired`。
（另有 `TaskExecutionRepairSummary{Examined, Repaired, RepairedByCode}` —— 只有计数，无逐项动作。）

### 10.2 objective_evidence 缺陷 **第二例（独立复现）**
本卡 verdict 的 2 条 unmet：
```
objective-file-evidence:§3          → file_evidence_missing - Evidence file does not exist: §3
objective-file-evidence:§9.A02）。    → check_not_run
```
来源 objective 行：`设计/证据：E:/github/…/01-自主工作轨迹与自改进审计.md（A02 / §3、§9.A02）。`
⇒ **第三个层面**：真正的证据是**绝对路径**，被 `IsSafeEvidenceFilePath`（只收「安全相对路径」）**拒掉**；
而括号里的**章节号**（`§3`、`§9.A02）。`）反而通过并被登记为「证据文件」。**意图被完全反转**，且该缺陷在
**两个互不相关的 objective 上独立复现**（另一例见卡 `14c02e8b`）。

### 10.3 运行中 blocker（§9 待查项）的真实语义
`GoalContinuationTests.TaskPlanSettlement_FailedTurn_ArchivesRealErrorCodeAndBlockerReason`（逐字）用**合成 turn.failed 载荷**：
`{"kind":"failed","errorCode":"work_unit_budget_exhausted","errorMessage":"WorkUnit input Token budget exhausted (input 150000 tokens)."}`
并断言：
- 真实 errorCode/errorMessage 归档到 Goal 与 Task；
- **ADR-092：回合失败是「当前单元可修复的未通过」，Goal 保持 `Active` 且 `TerminalAtUtc == null`**；
- **「Repair 不释放逻辑归属」**：Task 保持 `Assigned`、`ActiveAssignmentId` 不变，且 **`BlockerKind` / `BlockerReason` 必须为 `null`**；
- `goal_iterations.error_id` 归档真实 errorCode、status=failed；下一轮 continuation 已投递。

**`150000` 的线索**：`GoalContinuationTests.cs:607 Assert.AreEqual(150_000, command.WorkUnit.MaxInputTokens)`、
`:1523 MaxInputTokens = 150_000` ⇒ `150000` 是该路径下 WorkUnit 的**真实取值**（非虚构数字）。
但本轮实测**两个 plan 的 `task_nodes.max_input_tokens` 为 1000000/750000/2500000/1750000/1000000，不含 150000**
⇒ **分母来源仍未完全定位**（可能是另一 plan／另一任务的 work unit）。

**与 §9 的关系**：HEAD 中**未命中**该字面量（PuddingRuntime／PuddingPlatform／PuddingCore 三处），
但**本轮未检索 `PuddingAgent` / Desktop 等其它工程** ⇒ 只能说「**不是这三个工程的 HEAD 字面量**」，
**不能**说「不是 HEAD」。§9 的「发出者不是 HEAD 实现」因此**降级为待定**。

### 10.4 work unit 预算实测（本卡 plan `tp-6455158554163827b2d1899d06c3148f`，`task_nodes`）
| seq | status | rounds | toolCalls | duration | maxInput | maxOutput | maxCost |
|---|---|---|---|---|---|---|---|
1（Explore）| **Running** | 25 | 60 | 1800 | 1000000 | 100000 | **1.0** |
2 | Draft | 25 | 30 | 1200 | 750000 | 100000 | 0.75 |
3 | Draft | 40 | 120 | 3600 | 2500000 | 160000 | 2.5 |
4 | Draft | 30 | 100 | 3600 | 1750000 | 120000 | 1.75 |
5 | Draft | 25 | 60 | 1800 | 1000000 | 100000 | 1.0 |

⇒ **预算按工作单元差异化、非统一** ✓（符合卡片「不统一 600 / 不强制 25–40 轮切片」的设计合同）；
且 `max_cost = 1.0` 与观测到的 `cost budget exhausted (1.005420/1.000000)` **同源**
⇒ 成本轴确实在执行**冻结的 work unit 预算**（而非运行时拍脑袋）。

### 10.5 疑似未填充
`task_nodes.progress_fingerprint` 在两个 plan 的全部 6 行**均为 NULL** ⇒「按 fingerprint 推进」的落库面**疑未填充**
（`goal_iterations.progress_fingerprint` 与 `goal_runs.last_progress_fingerprint` 本轮未查，不得据此结论）。

---

## 11. **更正之二**：§9 的推理有缺陷（跨任务比较不能证明「实时」）

**新事实**：任务 `77883a50` 连续两次 `task_update`（v28 → v29）返回的 `blocker_reason` **完全相同**：
`WorkUnit input Token budget exhausted (187495/150000).`

**逻辑缺陷**：§9 据以推翻 §8 的那个「新值」取自**另一个任务**（`3bd2a4b0` 的 `1020083/1000000`）。
但**不同任务各自保存自己的 blocker**，因此「跨任务取值不同」**不能**证明「实时计算」。
判断活体性的正确检验是：**在同一对象上重复观测，看取值是否随时间变化** —— 本任务两次调用**逐字一致**。

**修正后的定位**：
- 该字段更像**按任务存储的 blocker**（或一个未增长的累计量）；
- §8「存储值」的判断**方向正确**；**§9 的全盘撤回属于过度纠正，现予修正**。
- 旁证：`150000` 与测试中 `WorkUnit.MaxInputTokens = 150_000` 的取值一致，而本任务当前 plan 的
  `task_nodes.max_input_tokens` 为 1000000 ⇒ 提示它来自**更早的计划/预算版本**，支持「历史存储值」读法。

**仍然成立的未解释事实**（不因本次更正而消解）：HEAD 三个工程（Runtime / Platform / Core）
**未命中**该字面量，而它确实出现在运行数据中；**未检索 `PuddingAgent` / Desktop**，
故「发出者是否在 HEAD 内」**仍待查**。

**方法论（第二次自我纠正的教训）**：
- 跨对象比较**不能**用于证明「随时间的实时性」；必须在**同一对象**上做**重复观测**。
- 我在同一文件里连续两轮得出相反结论，根因是**证据选取面**变动（跨任务 vs 同任务），
  而不是事实变动。**发布结论前必须写明证据的取样面**（哪些对象、几次观测）。

---

## 12. 活体 blocker 发出者：**6 个工程全为负**（收敛为「存储值」读法）

逐工程定向检索 `Token budget exhausted|budget exhausted`：

| 工程 | 结果 |
|---|---|
`PuddingRuntime` | 仅命中 `ExecutionUsageBudgetTracker.cs:134` 的**输出**分支（输入分支已是 `capacity exceeded`） |
`PuddingPlatform` | 仅 `GoalCommandService.cs:633`（相位标签）、`GoalSettlementStore.cs:35`（**文档注释**）、`:1436`（另一句） |
`PuddingCore` | 无 |
`PuddingAgent` | 无 |
`PuddingController` | 无 |
`PuddingHost` | 无 |

`input Token budget exhausted` 仅出现在 **`PuddingPlatformTests/Services/Goals/GoalContinuationTests.cs` 的合成载荷 fixture**
与 **`GoalSettlementStore.cs:35` 的文档注释**。

⇒ **当前源码中不存在产生该串的活代码** ⇒ §11 的「**按任务存储的历史值**」读法**得到支持**；
§9 的「活代码实时产生」**不被支持**（§9 已由 §11 修正）。

**限定**：已验证 6 个**可能执行 work-unit 预算**的工程；**未穷举其余 26 个工程**（Desktop / Gateway /
CodexService / Browser.* …）。另：仓库根目录全量扫描撞 2000 文件上限（仅扫 82/2000），**其无命中结论不可采信**。
