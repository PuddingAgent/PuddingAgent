---
title: 2026-10-08 warm-prefix 接入压缩提交单飞租约（D4 锁序，短临界区）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "warm-prefix 是唯一绕开 CompactionCoordinator 的压缩入口（DB 水合与自动/手动压缩都走它）。现在候选校验 + 内存历史替换 + 完成登记被放进同一个 per-session 单飞短临界区，远端摘要调用留在锁外；生命周期事件在释放锁之后发布（事件失败不撤销已提交历史）。"
categories: [docs, changelog]
tags: [context, compaction, f3, d4, locking, adr-095]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/00_changelog/2026Year/10/2026-10-08-F2首批warm-prefix唯一应用边界与真实after.md, Docs/00_changelog/2026Year/10/2026-10-08-F3首批硬保护逐轮重新评估.md]
related_files: [Source/PuddingRuntime/Services/ContextWindowManager.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingRuntimeTests/Services/CompactionCommitLeaseTests.cs]
slug: changelog-warm-prefix-compaction-commit-lease-2026-10-08
draft: false
---

# 2026-10-08 warm-prefix 接入压缩提交单飞租约（D4 锁序，短临界区）

> 范围：`Source/PuddingRuntime/Services/ContextWindowManager.cs`、`Source/PuddingRuntime/Services/AgentExecutionService.cs`、
> `Source/PuddingRuntimeTests/Services/CompactionCommitLeaseTests.cs`（新增）、`Source/PuddingRuntimeTests/Services/ContextWindowManagerTests.cs`（工厂加一个可选参数）
> 依据：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D4（锁序显式、模型调用不占维护提交锁）、[修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) §3（「commit 在已有 session 单写者/压缩协调边界内执行 CAS」）

## 1. 问题

`ContextCompactionService`（自动/手动压缩）与 `ContextWindowManager` 的 DB 水合都走
`CompactionCoordinator` 的 **per-session 单飞锁**，而 **warm-prefix 完全绕开它**：F2 首批引入的
`VerifyApplication`（候选 CAS）与内存历史替换都没有任何并发保护。

当前单写者 Turn 内不会真的并发，所以这不是眼下可见的 bug；但它是 D4 明确要求的锁序纪律，
且一旦 T5 尾款把可选维护挪到终态之后（引入后台提交者）就会立刻变成真问题。

## 2. 改动

### 2.1 `ContextWindowManager` 暴露两个受控接缝

| 方法 | 作用 |
|---|---|
| `AcquireCompactionCommitLeaseAsync(sessionId, ct)` | 取 `CompactionCoordinator` 的 per-session 单飞租约（返回 `IAsyncDisposable`），供调用方划定**提交短临界区** |
| `RecordCompactionCommitCompleted(sessionId)` | 只在**真的改写了历史**之后登记完成（记录冷却 + 清除该会话的无收益抑制） |

注释写明三条纪律：① 不包住远端摘要调用（D4 明禁持锁跨网络）；② 调用方必须已持
`SessionExecutionGate`（锁序：执行锁 → 压缩锁，反向 AB-BA 会死锁 —— 这正是
`CompactionCoordinator` 类注释里记录的历史事故）；③ 临界区内不得等待消息 dispatch 回执。

### 2.2 提交临界区收敛为一段

`TryWarmPrefixCompactionAsync` 现在这样组织：

```
（锁外）摘要 LLM 调用
── 取单飞租约 ──────────────────────────────
   VerifyApplication（CAS + 收益 + 硬上限 + 真实重算 after）
   若通过：durableHistory.Clear()/AddRange(...)   ← 内存替换与校验在同一临界区
          RecordCompactionCommitCompleted(...)
   若不通过：只做拒绝原因归类（不写历史、不发事件）
── 释放租约 ────────────────────────────────
（锁外）日志 + 生命周期事件（failed / completed）
```

事件放在锁外发布是有意的：ADR-095 D5 要求「事件发布失败不撤销已提交历史」，同理也不该让
事件写入把提交锁的持有时间拉长（它只影响投影，不影响权威状态）。

### 2.3 不改动的部分（避免误伤）

- **不接入冷却（cooldown）**：`TryGetCooldownSkipReason` 的 30s 冷却会**屏蔽硬保护**，
  D1/D2 明禁。本轮只用单飞锁，不引入任何会跳过硬保护的准入。
- **不调用 `InvalidateHistory`**：warm-prefix 只改内存、不写库；失效内存会从 DB 重建出
  未压缩的历史，反而把本次优化抹掉（这是 F2 持久化尾款的已知代价，见 §4）。

## 3. 验证（本机真实执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj` | **exit 0**，0 错误 |
| `dotnet test … --filter "…CompactionCommitLeaseTests"` | **exit 0**，失败 0 / 通过 **4** |
| 全量 `PuddingRuntimeTests` / `PuddingCoreTests` / `PuddingMemoryEngineTests` / `PuddingPlatformTests` | 见 §3.1 |

新增用例（`CompactionCommitLeaseTests`）：

1. `Lease_SerializesConcurrentCommits_OnSameSession`：第二个提交在锁外等待（200ms 内不得进入），
   持有期间 `coordinator.IsRunning(sessionId) == true`，全部释放后为 false。
2. `Lease_DoesNotBlockOtherSessions`：per-session 锁不是全局锁，另一会话不受阻。
3. `Lease_CancelledWaiter_DoesNotHoldTheLock`：等待中被取消必须抛 `OperationCanceledException`
   且**不释放**别人持有的锁；释放后可正常再次获取（无坏状态残留）。
4. `RecordCompactionCommitCompleted_ClearsNoGainSuppression`：真正提交后清除同候选的无收益抑制。

### 3.1 全量回归

| 套件 | 结果 |
|---|---|
| `PuddingRuntimeTests` | **exit 0**，失败 **0** / 通过 **2013** / 跳过 6（总计 2019） |
| `PuddingPlatformTests` | **exit 0**，失败 **0** / 通过 **1435** |
| `PuddingCoreTests` | 失败 **1** / 通过 **1065** —— 既有用例隔离缺陷（`LoggingFoundationTests`，**上一轮 1066/0 通过、本轮 1 失败**，顺序/并行相关，与本改动无关；单独跑恒通过） |
| `PuddingMemoryEngineTests` | 失败 **3** / 通过 **286** —— 既有环境依赖的 ContextPipeline 分层用例（干净基线已复现 4 例失败） |

「提交路径被重构却没有回归」是本轮的关键证据：`PuddingRuntimeTests` 2013 全绿说明把
`durableHistory.Clear()/AddRange(...)` 移进单飞临界区、并把事件挪到锁外，
都没有破坏既有行为。


## 4. 未完成

- **F2 持久化尾款**（事务 checkpoint/generation + 重启恢复）：阻塞点已定位 ——
  内存历史 `List<ChatMessage>` **不携带持久消息 id**，无法安全地把「被替换跨度」映射回
  `MessageEntity` 行去写 `CompactedBy`/覆盖清单；错标等于静默隐藏真实消息。解除阻塞需要先在
  hydration 边界引入稳定消息身份（且 `ChatMessage` 是 record，加字段会进入值相等语义，
  需要单独设计与回归）。
- **T5 尾款**：可选维护移到终态 + 前台租约释放之后、后台候选的抢占/取消 —— 仍需执行测试宿主
  （本仓库对 `ExecuteStreamingAsync` 零引用）才能验证生命周期改动。
- 前端消费 `context.request.trimmed` 事件本身（目前经终态 timing 载荷展示）。
- 交付状态仍为 **ready-for-external-deploy**；无真实模型调用、无部署、无浏览器证据。

## 5. 关联

- ADR：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D4/D5
- 前序：[F2 首批](2026-10-08-F2首批warm-prefix唯一应用边界与真实after.md)（`VerifyApplication`）、[F3 首批](2026-10-08-F3首批硬保护逐轮重新评估.md)（逐轮硬保护）
- 方案 §3：[首 Token 等待与上下文指示器修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)
