---
title: "goal 模式对照 deepseek-harness：我们有两套互不相认的\"目标\""
author: hyfree
date: 2026-11-17
last_reviewed: 2026-11-17
status: proposed
description: "对照 F:\\github\\deepseek\\deepseek-harness 的 goal 子系统，核对本仓已有的 GoalRun 状态机与 goal.md 自由文本工具。结论是\"能力不缺，缺的是一个权威源\"——调度侧的结构化 GoalRun 与模型侧读写的 goal.md 之间没有任何一致性约束，当日多次误判皆源于此。文末给出三档改法与未证实项。"
categories: [docs, design]
tags: [goal, goal-run, deepseek-harness, single-source-of-truth, cas, adr-074]
related_docs: [Docs/11_design/KeyVault明文凭据迁移-2026-11-17.md]
related_files: [Source/PuddingCore/Goals/GoalStateMachine.cs, Source/PuddingGoalPolicy/GoalContinuationPolicy.cs, Source/PuddingRuntime/Tools/BuiltIns/Agents/GoalUpdateTool.cs, Source/PuddingRuntime/Tools/BuiltIns/Agents/GoalReadTool.cs, Source/PuddingRuntime/Services/TaskTools/GoalLifecycleTools.cs]
slug: design-goal模式对照deepseek-harness-2026-11-17
draft: false
---


# goal 模式对照 deepseek-harness：我们有两套互不相认的"目标"

## 结论先说

**不是缺 goal 机制，是同时存在两套目标，且互不相认。**

| 系统 | 形态 | 谁在用 |
|---|---|---|
| `goal.md` | 自由 Markdown，模型自己组织 | 模型（`goal_read` / `goal_update`） |
| `GoalRun` | 状态机 + 预算 + 续跑策略 | 调度器、`goal_start/pause/cancel/resume` |

`GoalUpdateTool` 的类注释把这个定位写得很直白：

> 「goal.md 是 Agent 给自己的便条，不是结构化 API。工具只做写入。」

于是"便条"与"状态机"并存，中间没有一致性约束。当日几次误判（依据 goal.md 里过期的 `HEAD` 与"tts 零调用"去规划、把提交号写坏成 `ESC + 01f289`、撑到 4686 B 超自设 4 KiB）都属于这一类。

## 一、我们已经有的（实测，别当成缺失）

| 能力 | 位置 | 要点 |
|---|---|---|
| phase 机 | `Source/PuddingCore/Goals/GoalStateMachine.cs` | `active/paused/blocked/completed/cancelled/failed/budget_exhausted`；终态无出边；`CanResume` 仅 paused/blocked |
| 预算不变量 | 同上 | 迭代预算 ∈ [1,256]；**受理后即使失败也不回退计数**（ADR-074 §3） |
| 续跑策略 | `Source/PuddingGoalPolicy/GoalContinuationPolicy.cs` | 冻结事实 → `Continue/Wait/Stop/Complete`；review stale / 补丁上限 / 无进展即 Stop；`NextRoundKey` 含 `ActivationEpoch` |
| 模型侧工具 | `GoalLifecycleTools.cs`、`GoalResumeTool.cs`、`GoalReadTool.cs`、`GoalUpdateTool.cs` | `goal_start/pause/cancel/resume/read/update` |

也就是说：**对方 README 里的 phase、round 预算、resume 语义、无进展停止，我们基本都有，而且更细（多了 cancelled/failed 与评审/证据门）。**

## 二、对方（deepseek-harness）的三处不同

依据 `F:\github\deepseek\deepseek-harness\packages\goal\goal\README.zh.md` 与 `docs\subsystems\goal.zh.md`：

1. **单一权威源**。每次变更追加 `goal/change`（携带完整快照），**会话日志是唯一持久权威**；`clear` 写 tombstone。没有第二个文件参与。
2. **投影视图 + CAS**。`GoalView` 携带 `{ id, revision }`，变更必须带精确引用；陈旧引用会得到明确的陈旧错误，**不是静默覆盖**。
3. **跨会话边界 disarm**。四个持久 phase 之外有一个**进程本地** `activation: armed | disarmed`：任何会话开始边界（新建、恢复、fork）之后，`active` 的 goal 一律被停用续行，**必须有人显式 resume**，agent 不会自行继续。

另外它明确不做的事（避免我们误学）：不计量 token/货币/挂钟时间（只数 round）；没有独立评估器；不支持并行 goal。

它还有一个对模型面很干净的约定：只有三个动词 `get_goal` / `create_goal` / `update_goal`，`update` 带 `goal_id + revision + action`。

## 三、三处差异各咬了什么

| 差异 | 当日实际后果 |
|---|---|
| 无单一权威 | `goal.md` 里留着"HEAD 71a1254""tts 零调用"等过期条目，我两次据此规划 |
| 无 revision | 多轮覆写靠人工核 sha256/字节数；一次把提交号写成 `ESC + 01f289` |
| 无能力上限约束 | goal.md 涨到 4686 B（超自设 4 KiB），需要整轮重写 |

## 四、建议（按性价比排序）

**P0｜让 goal 成为投影，而不是第三套系统**
- `GoalRun` 保持唯一权威；
- `goal_read` 返回**结构化视图**：`phase`、`iterationsStarted/max`、`blocker`、`revision`、最近一次 `GoalPolicyDecision`；
- 现有 `goal.md` 的正文降级为"人类可读附注"字段（保留，因为它的自由发挥对我有价值），但**不再作为事实来源**；
- `goal_update` 增加 `revision` 的 CAS 语义（对齐对方 `get/create/update`）。

**P1｜补 disarm**
宿主启动后，把所有 `active` 的 `GoalRun` 标记为"需显式 resume"。目的是防止重启后自动续跑继续消耗预算——本条比 P0 更容易先落地，且收益是安全性的。

**P2｜严格回放**
状态机已在，缺的是在**持久流**上校验：revision 单调、phase 转换合法、时间戳不倒退。

## 五、未证实（不要当结论用）

1. 本仓是否**已经存在** `goal.md` 与 `GoalRun` 之间的同步代码——本轮未查到，但未查到不等于不存在（当日已两次栽在"搜不到≠没有"）。
2. 重启后 `active` 的 `GoalRun` 是否会自动续跑。`GoalContinuationPolicy` 有 `ActivationEpoch` 字段，但"重启是否令其失效"未实测。
3. 对方 `dsh-goal-round-driver` 的具体驱动节奏未读，只读了 goal 服务本体与其文档。

## 六、下一步

先做 P1（重启 disarm）：改动小、可实测（重启前后各取一次 `GoalRun` 状态即能取红/取绿），并且能立刻消除"重启后继续烧预算"的隐患。P0 需要动模型侧工具契约，应单独出一份设计再动。

## 七、复核更正（同日 19:45）

**P1 不需要新建——它已经存在，而且默认就是 disarm。**

| 证据 | 位置 |
|---|---|
| 重启分流逻辑 | `Source/PuddingPlatform/Services/Goals/GoalRestartReconciler.cs`（ADR-074 §12 + ADR-092） |
| 实体默认值 | `GoalRunEntity.cs:64` `ResumePolicy = GoalResumePolicies.Paused` |
| 接线点 | `Source/PuddingHost/Hosting/PuddingApplicationInitializer.cs:124`（schema bootstrap 之后、workers 之前） |
| 测试 | `Tests/.../GoalRestartReconcilerTests.cs`（含幂等重放、quota 退化、同 boot 重放） |

实际行为：

- `paused`（默认）：Active → Paused，原因码 `core_restart_disarm`；
- `auto_resume_on_restart`：保持 Active，但 `ActivationEpoch++` 且 `ActivationBootId=bootId`（换发 fence 让旧 writer 失效），并受 `MaxAutoResumesPerBoot` 配额约束，超额自动退回 paused。

### 误判原因

我只读了 `GoalStateMachine` 与 `GoalContinuationPolicy`，没搜 reconciler，就把"对方有 disarm、我们未验证"写成了待办。**又一次把"我没搜到"当成了"没有"**——这是当日第三次同类错误。

### 未证实 #2 结案

`goal_runs` 表 27 行，status 分布：

```
4 = BudgetExhausted   7
5 = Completed         2
6 = Cancelled         1
7 = Failed           17
1/2/3 = Active/Paused/Blocked   0
```

当日 18:54 重启时没有 Active 目标，所以日志里没有 `[GoalRestart]` 行——**这是空集，不是失效**。

### 结论修正

P1 划掉。**P0（单一权威 + 模型侧投影）仍是唯一真实差距**，但它的前提要先由用户裁定：`goal.md` 是降级为"人类可读附注"，还是整体废弃。这一步决定模型日常读写行为，不该由实现方单方面决定。
