---
title: 2026-10-08 warm-prefix checkpoint 事务落库原语（F2 持久化主体）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "新增 IContextCompactionService.CommitWarmPrefixCheckpointAsync：把 warm-prefix checkpoint 的摘要行 + 精确到行的 CompactedBy + 覆盖清单 + Session.CompactionGeneration 自增放在同一个 SaveChangesAsync 事务里；代际 CAS 不符、覆盖 id 找不到、已被别的摘要覆盖、空集合、会话不存在一律零写入，绝不部分标注。含重启恢复与故障注入回滚测试。"
categories: [docs, changelog]
tags: [context, compaction, f2, persistence, transaction, adr-095]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/00_changelog/2026Year/10/2026-10-08-持久消息身份登记.md, Docs/00_changelog/2026Year/10/2026-10-08-warm-prefix接入压缩提交单飞租约.md]
related_files: [Source/PuddingCore/Runtime/ContextCompactionContracts.cs, Source/PuddingRuntime/Services/ContextCompactionService.cs, Source/PuddingRuntimeTests/Services/WarmPrefixCheckpointCommitTests.cs]
slug: changelog-warm-prefix-checkpoint-durable-commit-2026-10-08
draft: false
---

# 2026-10-08 warm-prefix checkpoint 事务落库原语（F2 持久化主体）

> 范围：`Source/PuddingCore/Runtime/ContextCompactionContracts.cs`、
> `Source/PuddingRuntime/Services/ContextCompactionService.cs`、
> `Source/PuddingRuntimeTests/Services/WarmPrefixCheckpointCommitTests.cs`（新增，8 例）
> 依据：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D5（持久 checkpoint/代际是权威）、
> [修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) §3
> 前置：[持久消息身份登记](2026-10-08-持久消息身份登记.md)（本批的输入）、[提交单飞租约](2026-10-08-warm-prefix接入压缩提交单飞租约.md)

## 1. 改了什么

### 1.1 契约（`PuddingCore`）

- `WarmPrefixCheckpointCommitRequest`：只传事实与 id（会话/工作区/compactionId/**期望代际**/
  被覆盖的持久消息 id 列表/摘要正文/前后计数/token 事实/原因）。
- `WarmPrefixCheckpointCommitResult`：`Status` + `SummaryMessageId` + `TargetGeneration` +
  `CoveredCount` + `ProblemMessageIds`；`Committed` 只是 `Status == Committed` 的便捷判据。
- `WarmPrefixCheckpointCommitStatus`：`Committed / Unsupported / SessionNotFound /
  GenerationMismatch / CoveredMessagesNotFound / AlreadyCovered` —— 判据显式，调用方不得把
  「非 Committed」当成功。
- 接口新增 `CommitWarmPrefixCheckpointAsync(...)`，**带默认实现**返回 `Unsupported`：
  与本接口既有的 `IsCompactionRunning => false` 同一约定，测试替身无需同步修改；
  调用方据此**降级为内存-only 并如实上报 `durableCheckpoint=false`**，绝不假装已持久化。
  注释同时写死一条纪律：调用方必须已持压缩提交单飞租约，本方法**不再取锁**（否则自死锁）。

### 1.2 实现（`ContextCompactionService`，只负责持久化）

**不选候选、不调用模型**，只做一次事务写入：

| 步骤 | 说明 |
|---|---|
| 0 | 规整覆盖 id（去空、去重）；空集合直接 `CoveredMessagesNotFound` |
| 1 | 取会话行，缺失 ⇒ `SessionNotFound` |
| 2 | **代际 CAS**：`session.CompactionGeneration != ExpectedSourceGeneration` ⇒ `GenerationMismatch`（回传当前代际供调用方重取候选） |
| 3 | 查覆盖行：有 id 缺失 ⇒ `CoveredMessagesNotFound`（**整批拒绝**）；有行已被别的摘要覆盖 ⇒ `AlreadyCovered` |
| 4 | 构造摘要行（`Role=system`、`ContentType=compact_summary`、`Source=warm_prefix_checkpoint`、Metadata 带 compactionId/前后计数/source→target 代际） |
| 5 | 构造 `CompactionCoverageManifestEntity`（`Generator=warm_prefix_checkpoint`、`OmittedCount=0`、前后字节/token、summary hash） |
| 6 | 逐行写 `CompactedBy = summaryMessageId`（并补 `CanonicalContentHash`），`session.CompactionGeneration = source + 1` |
| 7 | **单次 `SaveChangesAsync`** ⇒ 摘要行 + 覆盖标注 + 覆盖清单 + 代际要么全写、要么全不写 |

**刻意不调用 `InvalidateHistory`**：调用方的内存历史已经是压缩后的版本（warm-prefix 在同一临界区
内替换过），失效会强制从 DB 重水合并丢掉本次延迟收益；此时 DB 与内存是一致的。

## 2. 验证（本机真实执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj` | **exit 0**，0 错误（含 Core 契约） |
| `dotnet test … --filter "…WarmPrefixCheckpointCommitTests"` | **exit 0**，失败 0 / 通过 **8** |

用例与断言：

1. `Commit_WritesSummaryCoverageManifestAndGeneration_InOneTransaction`：恰好 `m1..m3` 被打标、
   **跨度之外的 `m4` 保持 null**；摘要行 ContentType/Source/Content 正确；清单 Generator/CoveredCount/
   OmittedCount/代际对；会话代际 0 → 1。
2. `Commit_WithStaleGeneration_WritesNothing`：`GenerationMismatch`，且摘要行/打标/清单/代际**全部零残留**。
3. `Commit_WithMissingMessageId_MarksNothing`：`m1/m2` 存在但整批拒绝 ——
   **它们也不得被打标**（防「跨度与摘要脱节」）。
4. `Commit_WithAlreadyCoveredMessage_IsRejected`：指向真实 `previous-summary` 行（`CompactedBy` 有 FK）
   ⇒ 拒绝且不写清单、不推代际。
5. `Commit_WithEmptyCoveredSet_WritesNothing` / `Commit_ForUnknownSession_IsRejected`。
6. **`CommittedCheckpoint_SurvivesRestart_AndHydratesAsCompactedHistory`（重启恢复）**：
   提交后用**全新 manager + 新 DbContext** 在同一库上 `BuildContextFromDbAsync` ⇒ 水合出摘要、
   被覆盖的 `m1..m4` 不再出现、跨度之后的 `m5` 保留。
7. `Commit_WhenSaveFails_LeavesZeroPartialWrites`（故障注入）：暂存 `compact_summary` 行时抛错 ⇒
   摘要行/打标/清单/代际全部随事务回滚。

> 有一条既有实现细节被测试揭示并写进用例：`MessageEntity.CompactedBy` 有 **FK 约束**，
> 因此「已被覆盖」的种子状态必须指向真实摘要行 —— 这本身也说明覆盖标注是强一致的持久事实。

## 3. 未完成（下一步）

本提交只交付**落库原语**；warm-prefix 运行时**尚未调用它**。剩余：

1. `WarmPrefixApplyVerification` 带出**被移除跨度对象**，提交前用 `ResolveDurableSpan` 解析 id；
   `AllResolved == false` ⇒ 降级内存-only 并记录原因（绝不部分标注）。
2. `AgentExecutionService` 在压缩提交租约内调用 `CommitWarmPrefixCheckpointAsync`，
   把 `durableCheckpoint` 从硬编码 `false` 改为真实结果，并把失败原因带进 `completed` 事件。
3. 代际读取：`Plan.SourceGeneration` 目前恒为 0，需改为「冻结候选时读到的会话代际」，
   让 CAS 真正生效（durable compaction 与 warm-prefix 交错时才会触发）。
4. 交付状态仍为 **ready-for-external-deploy**；无真实模型调用、无部署、无浏览器证据。

## 4. 关联

- ADR：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D4/D5
- 前置：[持久消息身份登记](2026-10-08-持久消息身份登记.md)、[提交单飞租约](2026-10-08-warm-prefix接入压缩提交单飞租约.md)
- 方案 §3：[首 Token 等待与上下文指示器修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)
