---
title: DeepSeek Harness goal 模式实现解剖与 PuddingAgent 对齐方案
status: proposed
date: 2026-11-01
author: 鲸鲸
---

# DeepSeek Harness goal 模式解剖

## 0. 源码位置（含一次我自己的错误更正）

**正确路径（用户 2026-11-01 19:12 提供，已实测）**：

```
F:\github\deepseek\deepseek-harness
  packages\goal\goal\                 types.ts / domain.ts / fold.ts / index.ts / runtime.ts / client.ts
  packages\goal\tool-goal\             index.ts / authority.ts / wrapup.ts
  packages\goal\command-goal\
  packages\goal\goal-round-driver\     index.ts(477) / prompt.ts(26)
```

### 我此前错在哪

我先前的扫描只遍历了 `D:\` 与 `C:\Users\hyfree`，据此得出「本机没有源码」——**结论是错的**，
源码一直在 F: 盘。参考文档里写的 `E:\github\deepseek\deepseek-harness` 也只是盘符过时（E: 不存在），
**目录结构本身是对的**（`github\deepseek\deepseek-harness`）。

> **教训（已记入记忆）**：判定「某物不存在」之前，必须先枚举全部盘符
> （`Get-PSDrive` / `os.listdrives`），否则「扫不到」只是扫描范围的函数，不是事实。

下面的分析基于**实读源码**，不是 README 转述。

## 1. goal 组 = 4 个包，职责严格分离

| 包 | 职责 | 挂载点 |
|---|---|---|
| `goal` | 每会话**唯一**的持久目标：create / edit / pause / resume / complete / block / clear | `ctx.goals` |
| `tool-goal` | 模型工具：`get_goal` / `create_goal` / `update_goal` | `ctx.tools` |
| `command-goal` | 人类 `/goal` 命令（**不消耗模型轮次**） | `ctx.commands` |
| `goal-round-driver` | **自动续跑**：把 active goal 变成连续的 Rounds | 无服务键 |

关键分工：**service 管状态、tools 给模型控制、driver 只负责调度**。
driver 自己**没有配置** —— round 上限属于 goal 定义，blocked 阈值属于 tool-goal，
避免同一个值在两处产生分歧。

## 2. round-driver 真正的机制（我们目前完全没有的部分）

### 2.1 reservation → admission（先记账后准入）

```
idle 时：reserve(roundsStarted + 1) 绑定 { goalId, revision } → 入队一条 <goal_round> prompt
只有真正进入 history 的 goal message 才 roundsStarted++
被判定 stale 的 reservation 不消耗轮次编号
```

**为什么要这样**：人类消息、过期预约都不该吃掉额度。我们现在的策略只判断“要不要继续”，
没有额度记账口径，等于没有可审计的上限。

### 2.2 race fences（前后各校验一次）

`agent/pre-step` 监听器在**下游监听器执行前后各校验一次**完整 claimed record 与当前 goal 一致；
过期 / 已取消 / 竞争的 prompt 在进入 step 之前就被拒。

### 2.3 durability checkpoint（持久化义务）

`goal/changed` 产生一条持久化义务：入队前必须 `await ctx.sessions.flush()`，
**await 之后要再次校验** goal revision 与竞争输入。flush 失败经 `agent/error` 到达时，
在下一轮开始前 disarm。

### 2.4 停止与取消的语义（细）

| 情形 | 行为 |
|---|---|
| complete / pause / blocking | 抑制续跑 |
| 宿主发起 pause | **中止正在跑的这一轮** |
| 模型在自己轮内 pause | 本轮正常结束 |
| 轮次耗尽 | 记 blocker，稳定码 `round-limit` |
| max tokens 结束 / 持久化失败 / 取消 / 插件卸载 | 停止 |
| 取消 | **从不自动重启**：有轮次在进行中的 goal 在下一个 idle 点被 pause |
| 与 goal 无关的取消 | 只 disarm，不 pause |
| resume / fork / unload 之后 | **永不自动 arm**，必须有显式的人类授权 resume |

### 2.5 人类优先与 subagent 门槛

- 预约到达之前来的人类输入 → 自动工作让位，直到 agent 再次 idle；
- 若加载了 subagent 服务，**必须等该 agent 正在推进的子孙结算**才开始新一轮；
- 队列里还没被消费的 goal prompt 在 agent 到 idle 时**从 inbox 移除**，人类输入不排在它后面。

## 3. 与我们现有 `ContinuationPolicy` 的差距

我们已落地（`Source/PuddingRuntime/Services/AgentLoop/ContinuationPolicy.cs`）：
三条同时成立才续跑（有未完成事项 + 有余量 + 上一轮有推进），失败不重试，连续两轮无进展即停。

差距对照：

| 能力 | DeepSeek | 我们 | 影响 |
|---|---|---|---|
| 目标持久化（跨重启/恢复/分叉） | 有（`goal` 服务 + `goal/change` 事件） | **无**（只有 goal.md 文件，无生命周期） | 重启后目标状态不可恢复 |
| 轮次额度记账口径 | 只算进入 history 的 goal message | **无记账** | 上限不可审计 |
| 过期/竞争 prompt 的围栏 | pre-step 前后双校验 | 无（尚未有 prompt 队列） | 并发下可能重复推进 |
| 持久化检查点 | flush + 复核 revision | 无 | 崩溃可能丢一轮 |
| 取消语义 | 取消不自动重启；host pause 中止在飞轮 | 只有 `Cancelled → Halted` | 取消后可能被心跳复燃 |
| resume/fork 后不自动 arm | 强制人类授权 | 无此概念 | 恢复后可能无人值守乱跑 |
| subagent 结算门槛 | 必须等后代 settle | 无 | 子代理在跑时可能插队 |
| 人类优先 | 排队让位 + inbox 移除 | `UserMessagePending → Halted`（较粗） | 方向对，粒度不足 |

## 4. 对齐方案（分三步，每步都可独立验收）

**Step 1 — 额度与记账（不碰并发）**
给续跑加 `roundsStarted` 账本，口径与 DeepSeek 一致：只有"真正进入模型历史的 goal round"才 +1；
预留失败不计数。可纯函数化并单测（给定序列 → 期望计数）。

**Step 2 — 围栏与检查点**
在真正接入 agent loop 时加 pre-step 双校验 + 持久化 checkpoint（flush 后复核 revision）。
这一步必须等 Step 1 的记账口径稳定，否则围栏无从校验。

**Step 3 — 生命周期语义**
补 pause/resume/complete/block/clear 与"resume 后不自动 arm"。
这一层建议**复用现有 `IAgentLoopHook`**，而不是新建插件体系。

## 5. 明确不照搬的部分

- **不引入 Cordis 插件体系**：我们已有 DI + `IAgentLoopHook`，引入第二套生命周期会割裂；
- **不做同会话之外的 fork/Ralph 式独立尝试**：DeepSeek 自己也说那属于另一插件层；
- **不做 evaluator 认证**：DeepSeek 明确列为 deferred，我们更没有依据先做。

## 6. 源码级实现（实读，2026-11-01 19:15）

### 6.1 类型模型（`goal/src/types.ts`，149 行）

```ts
type GoalId = Branded<'GoalId'>
interface GoalRef { id: GoalId; revision: number }        // CAS 身份：每次持久变更 revision++
type GoalPhase = 'active' | 'paused' | 'blocked' | 'complete'
interface GoalBlockReason { code: string; message: string }  // code 是稳定 lower-kebab
interface GoalSnapshot extends GoalRef { objective; phase; blockedReason?; maxGoalRounds }
type GoalActivation = 'armed' | 'disarmed'                 // 进程本地，永不持久化
interface GoalProjection { goal: GoalSnapshot; roundsStarted; createdAt; updatedAt }
```

两个要点我们完全没有：

- **revision 即 CAS**：所有变更都带 `GoalRef{id, revision}`，陈旧写不会覆盖新状态；
- **activation 与 phase 分离**：phase 是持久生命周期，activation 是**进程本地**的续跑开关。
  重启后 phase 仍是 active，但 activation 回到 disarmed —— 这正好实现「恢复后不自动乱跑」。

`GoalProjection` 由 session log **严格折叠**得出（`fold.ts` 349 行），replay 失败记 `failure`
而不是猜一个值。这解释了为什么它敢说「durable state as authority」。

### 6.2 driver 的调度循环（`goal-round-driver/src/index.ts`，477 行）

```ts
interface RoundAttempt extends RoundIdentity {           // RoundIdentity = {goalId, revision, round}
  messageId; content; phase: 'queued'|'claimed'|'admitted'; cancelled; stale
}
```

`drive()` 的**实际顺序**（与 README 的说法一致，但有实现细节）：

1. `readyToDrive`：插件 ACTIVE + 未 stopping + agent 仍是同一个 + `status==='idle'` + 无竞争 prompt；
2. `needsCheckpoint` → `await ctx.sessions.flush(agent.session)`，**失败即 disarm 并 return**；
   flush 后再 `readyAfterCheckpoint` 复核（期间可能来了变更或人类消息）；
3. 有 attempt → 清空并置 `needsCheckpoint=true`（下一轮先落盘）；
4. `drivable(goal)` = `phase==='active' && activation==='armed'`；
5. **等子代理**：`await Promise.race([subagents.waitForChildren(agent), unloading.promise])`；
6. `roundsStarted >= maxGoalRounds` → `goals.block(ref, {code:'round-limit'})`；
7. `round = roundsStarted + 1` → 渲染 prompt → `createUserMessage({source:{kind:'goal',goalId,revision,round}})` → `agent.followup()`；
8. 入队失败 → 若 goal 仍是同一 revision 且 active/armed，则 `block({code:'queue-failed'})`。

### 6.3 三个围栏（事件驱动，不是定时轮询）

| 事件 | 动作 |
|---|---|
| `agent/status → idle` | attempt 仍是 `queued` → `inbox.remove`（**人类输入不排在它后面**）；pause 条件成立 → `goals.pause` |
| `agent/inbox/inserted` | 非本 attempt 的消息 → `competingQueued = true`，attempt 置 `stale` |
| `agent/inbox/claimed` / `discarded` | attempt `phase='claimed'` / `cancelled=true` |
| `session/event: user/message` | `id === attempt.messageId` → `phase='admitted'`（**只有此刻才计轮次**） |
| `session/event: turn/end` | `max-tokens` → disarm；`aborted` → 在飞 attempt 置 cancelled，否则 disarm |
| `goal/changed` | `needsCheckpoint = true`；host 发起 pause 且 agent running → `agent.cancel({kind:'user'})` |
| `agent/pre-step` | `validReservation` 全量复核（见下），不合法 → stale + 移除 + `restoreOtherClaimed` + 重新调度 |

```ts
validReservation = fiber ACTIVE && !stopping
  && attempt?.phase === 'claimed' && !attempt.stale
  && 内容与来源完全一致
  && goal 仍是 source.goalId/revision 且 phase==='active' && activation==='armed'
  && source.round === goal.roundsStarted + 1
```

**注意最后一行**：round 号必须等于「当前+1」。这让重复/跳号的 prompt 无法通行 —— 比单纯比对
revision 更强。

### 6.4 给模型的 prompt（`prompt.ts`，26 行）

```
<goal_round>
Objective: "<JSON 转义的目标>"
Round: 3/8

Continue working toward the objective in this same session. Treat the current workspace,
tool results, and durable session state as authoritative; inspect them instead of assuming
earlier narration is still current. Make concrete progress and verify the result. Before
claiming completion, gather evidence that the whole objective is achieved, read the current
goal, and mark it complete. If work remains, leave the goal active for the next round.
Follow the configured goal-tool policy before reporting a blocker.
</goal_round>
```

三个关键设计：**目标用 JSON 转义**（防止目标文本里的换行/引号改写指令结构）、
**明说"不要相信先前的叙述"**（对抗上下文里的过期自述）、**收尾要求证据 + 读 goal + 标完成**。

### 6.5 工具侧权限（`tool-goal/src/authority.ts`，117 行）

```ts
authority = {kind:'direct-human'} | {kind:'goal-round', goal}
// 调用必须：agent === ctx.agents.get(agent.id) && status==='running' && currentInitiator()===agent
// 且必须有 open model turn，否则 GOAL_TOOL_DRIVER_REQUIRED
```

即：**改动目标状态的工具只能在"确切的、活的、当前正在跑的那个 agent 的打开中的回合里"调用**。
这挡住了「心跳/子代理/另一个 agent 顺手改目标」。

## 7. 对我们 Step1 的反馈（实读后修正）

我们的 `GoalRoundLedger` 已经对齐了「预约不计、admission 才计」，但对照源码还缺三点：

1. **revision 要当 CAS 用**，不只是围栏值 —— 我们的 `TryReserve(id, revision)` 已有，但下层没有
   "陈旧写被拒"的语义（因为没有持久层）；
2. **activation 与 phase 分离** —— 我们的模型里还没有这个概念，直接导致「resume 后不自动 arm」无法表达；
3. **round 号必须 == roundsStarted+1** —— 这是比 revision 更强的合法性检查，我们应该在
   `TryAdmit` 里加 `expectedRound` 参数。
