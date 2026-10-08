---
title: 2026-10-08 F2 接线：warm-prefix 落库、真实代际与降级分支
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "把上一提交的落库原语接进运行时：冻结候选时读真实持久代际（Plan.SourceGeneration 不再恒 0）、通过校验后在被替换跨度上解析持久身份并调用事务落库、解析不全或无服务时降级为内存-only 并如实上报 durableCheckpoint=false + 原因码；VerifyApplication 带出被替换跨度的原始对象，AgentExecutionService 新增可选 IContextCompactionService 依赖（DI 已注册，无需改装配）。"
categories: [docs, changelog]
tags: [context, compaction, f2, persistence, wiring, adr-095]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/00_changelog/2026Year/10/2026-10-08-warm-prefix-checkpoint事务落库原语.md, Docs/00_changelog/2026Year/10/2026-10-08-持久消息身份登记.md]
related_files: [Source/PuddingRuntime/Services/WarmPrefixCompaction.cs, Source/PuddingRuntime/Services/WarmPrefixDurableCommitDecision.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingRuntime/Services/ContextWindowManager.cs, Source/PuddingRuntimeTests/Services/WarmPrefixDurableCommitDecisionTests.cs, Source/PuddingRuntimeTests/Services/WarmPrefixCompactionApplyTests.cs, Source/PuddingRuntimeTests/Services/DurableMessageIdentityTests.cs]
slug: changelog-f2-wiring-durable-checkpoint-2026-10-08
draft: false
---

# 2026-10-08 F2 接线：warm-prefix 落库、真实代际与降级分支

> 范围：`Source/PuddingRuntime/Services/WarmPrefixCompaction.cs`、
> `Source/PuddingRuntime/Services/WarmPrefixDurableCommitDecision.cs`（新增）、
> `Source/PuddingRuntime/Services/AgentExecutionService.cs`、`Source/PuddingRuntime/Services/ContextWindowManager.cs`
> 依据：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D4/D5、
> [修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) §3
> 前置：[事务落库原语](2026-10-08-warm-prefix-checkpoint事务落库原语.md)、[持久消息身份登记](2026-10-08-持久消息身份登记.md)

## 1. 改了什么

### 1.1 `VerifyApplication` 带出被替换跨度（`WarmPrefixCompaction`）

`WarmPrefixApplyVerification` 新增 `RemovedMessages`：CAS 通过后按冻结时的布局
（leading system 前缀 → removed span → retained suffix）切出跨度的**原始对象**，
供调用方按引用解析持久 `MessageId`。被拒绝时保持 `null`（调用方据此不落库）。

同时 `TryCreatePlan` 新增可选参数 `sourceGeneration`（默认 0 仅为兼容既有调用），
`Plan.SourceGeneration` 不再恒为 0 —— 「仅代次变化」这一路 CAS 从今天起真的抓得到。

### 1.2 真实代际读取（`ContextWindowManager`）

新增 `GetCompactionGenerationAsync(sessionId, ct)`：读 `Session.CompactionGeneration`；
会话不存在或无库时返回 0。`TryWarmPrefixCompactionAsync` 在 `TryCreatePlan` **之前**读它，
并同时用于（a）冻结候选的 `SourceGeneration`，（b）落库请求的 `ExpectedSourceGeneration`。

### 1.3 降级决策抽成纯函数（`WarmPrefixDurableCommitDecision`）

`AgentExecutionService` 里「要不要落库 / 不能落库怎么说」的判断被抽成纯判定，使其可脱离执行宿主单测：

| 判定 | 语义 |
|---|---|
| `ShouldAttempt(serviceAvailable, resolution)` | 有服务 **且** 跨度**全部**解析出持久 id **且** id 非空 |
| `ResolveSkipReason(...)` | 无服务 ⇒ `compaction_service_unavailable`；未全解析 ⇒ `unresolved_durable_identities:N` |
| `Interpret(result)` | `Committed` ⇒ `(true, null)`；其余状态 ⇒ `(false, "durable_commit_<status>")` |

**降级不撤销运行期压缩**：内存历史照常替换（保住延迟收益），只是如实上报未持久化。

### 1.4 接线（`AgentExecutionService`）

在既有的压缩提交单飞临界区内、内存历史替换之后：

```
解析跨度持久身份（ResolveDurableSpan）
  ├─ 可落库 ⇒ CommitWarmPrefixCheckpointAsync(期望代际, 覆盖 id, 摘要, 前后计数/实测 after)
  └─ 不可   ⇒ 记原因码（不写库、不部分标注）
durableCheckpoint / durableSkipReason ⇒ 日志 + completed 事件（不再是硬编码 false）
```

新增可选依赖 `IContextCompactionService? contextCompactionService = null`；
生产装配**无需改动**（`IContextCompactionService` 与 `AgentExecutionService` 都是 `AddSingleton`，
容器自动注入）；未注册/测试替身走接口默认实现 ⇒ 返回 `Unsupported` ⇒ 同样降级并如实上报。

## 2. 验证（本机真实执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj` | **exit 0**，0 错误 |
| `dotnet test … --filter "…WarmPrefixDurableCommitDecisionTests\|…WarmPrefixCompactionApplyTests"` | **exit 0**，失败 0 / 通过 **10** |
| `dotnet test … --filter "…DurableMessageIdentityTests"` | **exit 0**，失败 0 / 通过 **9** |
| 全量 `PuddingRuntimeTests` / `PuddingCoreTests` / `PuddingPlatformTests` | 见 §2.1 |

新增/扩展用例：

- `WarmPrefixDurableCommitDecisionTests`（3）：只有在「有服务 + 跨度全解析 + id 非空」时才尝试落库；
  无服务、未全解析、无可覆盖行三种降级都给出**显式原因码**（含未解析条数）；
  `Interpret` 把每个非 `Committed` 状态折成 `durable_commit_*` 降级事实。
- `WarmPrefixCompactionApplyTests`（+2）：`VerifyApplication_ExposesRemovedSpanObjects_ForDurableCommit`
  用**引用相等**断言切分准确（`AreSame`）——切错会去标注错误的库行；并断言被替换消息不在提交后的历史里、
  前导 system 原样保留；`VerifyApplication_WhenRejected_ExposesNoRemovedSpan` 断言拒绝时不带跨度对象。
- `DurableMessageIdentityTests`（+2）：`GetCompactionGenerationAsync` 读到真实列值（0 → 5）、
  未知会话与无库时为 0。

### 2.1 全量回归

| 套件 | 结果 |
|---|---|
| `PuddingRuntimeTests` | **exit 0**，失败 **0** / 通过 **2035** / 跳过 6（总计 2041） |
| `PuddingCoreTests` | **exit 0**，失败 **0** / 通过 **1066** |
| `PuddingPlatformTests` | **exit 0**，失败 **0** / 通过 **1435**（上一轮那例顺序相关的 flake 本次未复现，与既有判定一致） |

`AgentExecutionService`（2700+ 行、执行主路径）新增依赖与提交分支后三个套件全绿：
构造参数为可选、默认实现走 `Unsupported` 分支，因此既有调用点（含测试）无需改动。


## 3. 未完成

- **端到端未验证**：本仓库没有驱动流式/缓冲执行的测试宿主（`ExecuteStreamingAsync` 在测试里 0 引用），
  因此「第 0 轮软维护延期 → 后续越界 → mid-turn 硬保护 → 落库 → 重启后仍是压缩态」这条完整链路
  只有**各段单测**（判定、CAS、身份解析、落库、重启水合）与代码走查，没有一次真实 Turn 串联。
  要把它变成端到端证据，需要先补执行测试宿主。
- T5 尾款（终态后调度 + 抢占/取消）同样卡在这个宿主缺失上；F1 尾款（实测 vs 估算并排展示）与
  T6（外部部署验收）未做。
- 交付状态仍为 **ready-for-external-deploy**；无真实模型调用、无部署、无浏览器证据。

## 4. 关联

- 前置：[事务落库原语](2026-10-08-warm-prefix-checkpoint事务落库原语.md)、[持久消息身份登记](2026-10-08-持久消息身份登记.md)、[提交单飞租约](2026-10-08-warm-prefix接入压缩提交单飞租约.md)
- ADR：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D4/D5
- 方案 §3：[首 Token 等待与上下文指示器修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)
