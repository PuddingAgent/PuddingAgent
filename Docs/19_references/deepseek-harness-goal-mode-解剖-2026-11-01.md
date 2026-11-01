---
title: DeepSeek Harness goal 模式实现解剖与 PuddingAgent 对齐方案
status: proposed
date: 2026-11-01
author: 鲸鲸
---

# DeepSeek Harness goal 模式解剖

## 0. 源码位置（先把路径校正清楚）

参考文档里写的 `E:\github\deepseek\deepseek-harness` 是**陈旧投影**（本机无 E: 盘）。
现场实测本机只有它的**工作区与运行态**，没有源码：

```
C:\Users\hyfree\Documents\deepseek-harness\     仅 default-workspace，无 packages/
D:\Users\huany\Documents\deepseek-harness\      同上
C:\Users\hyfree\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\
                                                运行态依赖，无 goal 相关文件（GOAL_FILES=0）
```

**真源码在 GitHub**：`deepseek-ai/deepseek-harness`（TypeScript，"Everything is a Plugin"）。
本文件内容取自 `packages/goal/` 的 README（master 分支）。

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
