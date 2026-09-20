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
