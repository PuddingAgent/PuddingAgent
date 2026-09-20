# ExecutionPlan/WorkUnit Explore — Step 1 Checkpoint (checkpoint-v1)

- goalRunId: `tg-dab61d6e65ba7c0b5d3b355077b7c5e5`｜objectiveVersion: 1｜iteration: 2
- taskId: `8df6a4d6d66d4c0cb6b02e838ea2e6c3`｜assignmentId: `c0c832fbf3b9448db93dc487bc8d75f0`｜task expectedVersion: 19（InProgress）
- stepNodeId: `tn-379af80cc3501b0455e9e84629d21734`（kind=Explore, sequenceNo=1/5）
- planId: `tp-a2cd4086ee2ae1ac6846cc1e8128a1f5`｜planFingerprint: `9f80b41af3ba5d78270427be4d213f94f110d5fd43bb8784fb1b55d9c1c2b01e`
- 观测时 repo HEAD: `06c6604f92c3676aa133ae441201697f7510395f`（worktree clean）

---

## 1. OBSERVE

- `lastVerdict`（iteration 1）: outcome=blocked, blockerCode=`work_unit_budget_exhausted`, **unmetCriteria=[]**, completedAtUtc `2026-09-20T10:44:01Z`
  ⇒ 无具体判据可推进；本轮须**极简**（iteration 1 已耗尽工作单元预算），故只做**最小必查证据**。
- `acceptanceContract`: source=`bounded_planning:objective_evidence`, contractVersion=1, **criteriaCount=7**
  （注意：source 不是裸 `bounded_planning`，且 A1 提案通道经上游取证已判定不可达/语义不适用 ⇒ 本轮**不发**提案）

## 2. PLAN
本迭代推进 step 1（Explore）：判据 = 卡片列出的**剩余门禁**逐项能对应到 commit / file:line / 运行态事实。

## 3. ACT
只读取证：`git show --stat 990673e`、HEAD/工作树、进程枚举、定向读 `TaskExecutionPlanContracts.cs`、四份子目录 grep。未运行构建/测试，未改任何代码。

## 4. VERIFY（证据）

### 4.1 A1 语义**已入代码**（不是仅文档声明）

| 事实 | 位置 |
|---|---|
「**Per-request input capacity; cumulative input is a usage metric, not a remaining allowance**」（原文） | `Source/PuddingCore/Scheduling/TaskExecutionPlanContracts.cs:53` 上方注释 |
`MaxInputTokens` 为 `required long`（预算冻结字段） | 同上 `:53` |
`CurrentSchemaVersion = 1` / **`CurrentPlanVersion = 2`** | 同文件 `TaskExecutionPlanSnapshot`（约 `:76`） |
guard 发送前与 provider 上限取小 | `Source/PuddingRuntime/Services/LlmRequestBudgetGuard.cs:54-55` |
provider 输入上限识别（失败关闭） | 同上 `:172-186` |
guard 接入真实调用路径 | `Source/PuddingRuntime/Services/AgentExecution/AgentExecutionLlmInvoker.cs:74-76` |
委派/子执行单次容量校验（容量 < 最小可执行 ⇒ 失败） | `Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs:285-286` |
计划契约携带容量 | `Source/PuddingCore/Platform/IExecutionCommandReader.cs:65`、`Source/PuddingCore/Runtime/ITurnExecutor.cs:103` |

### 4.2 交付提交 `990673e`（对应卡片 A1）

`git show --stat 990673e` → `990673e | 2026-09-14 21:19:57 +0800 | fix(runtime): enforce per-request input capacity across delegated budgets`，**12 个文件**：

```
Source/PuddingCore/Platform/IExecutionCommandReader.cs
Source/PuddingCore/Runtime/ITurnExecutor.cs
Source/PuddingCore/Runtime/SubAgentInvocationContracts.cs
Source/PuddingCore/Scheduling/TaskExecutionPlanContracts.cs
Source/PuddingRuntime/Services/ExecutionCommandReader.cs
Source/PuddingRuntime/Services/Scheduling/TaskExecutionPlanCompiler.cs     ← PlanVersion=2 编译侧
Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs
Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Streaming.cs
Source/PuddingRuntime/Services/AgentExecution/ExecutionUsageBudgetTracker.cs
Source/PuddingRuntime/Services/LlmRequestBudgetGuard.cs
Source/PuddingRuntime/Services/SubAgentInvocationService.cs
Source/PuddingRuntimeTests/Services/ExecutionUsageBudgetTrackerTests.cs（+145 行）
（另 TaskExecutionPlanCompilerTests / GoalContinuationTests）
```

### 4.3 同批其它修复（依据 `Docs/Reports/默认助手停滞修复与预算接管-2026-09-14.md`）

| 提交 | 内容 | 自述验证 |
|---|---|---|
`4ce9d6f` | 搜索有界 + 响应取消（扫描预算 10s/50,000 条） | FileSearch **32/32** |
`8d2ad3b` | 失效接收者不再堵住回复投影队列 | MessageRouter/投影 **23/23** |
`990673e` | A1 输入容量（本卡范围） | Runtime **87/87**、Platform **32/32** |
`a18702b` | 租约前兑现取消，避免重建上下文 | ExecutionControl **30/30** |

### 4.4 运行态事实

- Core 进程 **PID 23280，StartTime `2026-09-20 17:25:06`（本地）** ⇒ 加载的是该时刻之前的产物。
- 本会话中我于 2026-09-20 提交的 `b81c69bf`（PuddingPlatform 修复）**晚于该启动时刻** ⇒ **未部署**。若需验证最新行为须重启 Core。

### 4.5 重要正面证据：本卡头号门禁**正在被执行**

`CurrentPlanVersion = 2`（`TaskExecutionPlanContracts.cs`）＋ 本次 Goal 的 work unit 携带冻结预算
（`maxRounds 25 / maxToolCalls 60 / maxDuration 1800s / maxInputTokens 1000000 / maxOutputTokens 100000 / maxCost 1.0`）
＋ 5 步计划（Explore→Plan→Change→Test→Review）
⇒ 卡片剩余门禁「**新 PlanVersion=2 task-bound 真实运行**」**不是待造能力，而是当前正在发生的执行**（iteration 2 / step 1）。

## 5. 进入 step 2（Plan）的开放问题（本轮未定性，禁止假设）

**Q1（最高优先，直击剩余门禁核心）—— 「输入累计是否仍被当作剩余额度」**
A1 声明「cumulative input **is a usage metric, not a remaining allowance**」；但：
- 本 goal iteration 1 的**新鲜** verdict 仍为 `blockerCode=work_unit_budget_exhausted`（`2026-09-20T10:44:01Z`，unmetCriteria 空）；
- scheduler goal 的 `task_update` 返回**三次回显同一粘滞值** `WorkUnit input Token budget exhausted (1020083/1000000)`（数值一字不差 ⇒ 疑为历史投影，非新测量）。
**`work_unit_budget_exhausted` 覆盖全部预算轴**（轮次/工具/时长/输入/输出/成本），本轮**未取到该次 blocker 的 reason 原文** ⇒ **无法判定是否仍由输入累计触发**。
待办：取 `goal_verifications.blocker_message`（或等价字段）原文，确认触发轴；这直接对应门禁「独立账本累计与单轮容量对齐」。

**Q2** 「输出/成本有限零值不可绕过」缺**行为级**端到端证据（现有仅为 JSON 往返与 guard 单测）。
**Q3** 卡片要求「不以两项只读工具 smoke 替代 600 轮产品验收」⇒ 需在 step 2 定义**可执行的验收脚本与判据**。
**Q4** 部署滞后（§4.4）：验证最新行为前需重启 Core。

## 6. 终端判定
- **Step 1（Explore）= COMPLETE**：卡片剩余门禁已逐项对应到 commit / file:line / 运行态事实，并识别出 4 项开放问题。
- **Task = NOT COMPLETE**：step 2–5 未开始。**本判定为自述，终态由服务端 verifier 裁决。**

## 7. 诚实限定
- `search_grep` 对 `Source` 全量会撞 2000 文件上限而返回 partial（本轮已按子目录收窄）；其中一次调用因目录不存在失败（未产生证据）。
- `Source/PuddingRuntime` 的 `PlanVersion` grep 受 `max_results=25` 截断；PlanVersion=2 的结论来自 `TaskExecutionPlanContracts.cs` 直接阅读，非该 grep。
- 未运行构建/测试；未修改任何产品代码；未重启。
