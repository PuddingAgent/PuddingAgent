---
title: 2026-10-08 F2 首批：warm-prefix 唯一应用边界（真实重算 after + 严格 CAS）并接入策略组件
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "warm-prefix 提交前统一走一个应用边界：做成 checkpoint → 严格版本/身份 CAS（源 revision、被替换跨度、跨度之后的保留后缀、路由与工具版本）→ 用真实摘要重新完整计量整请求 → 收益与硬上限准入；只有真实测量的 after 才允许进日志与事件，计划估算不再冒充 after。同时完成 PuddingContextPolicy 的 S5 首个接入（Runtime 消费严格适用校验与净收益准入），并修掉组件里「保留身份误用全量列表」的契约缺陷。"
categories: [docs, changelog]
tags: [context, compaction, f2, adr-095, cas, s5, after-tokens]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/10_conventions/组件化交付规程.md, Docs/00_changelog/2026Year/10/2026-10-08-新增独立策略组件PuddingContextPolicy-S1-S4.md, Docs/00_changelog/2026Year/10/2026-10-08-软硬分类回放与393216容量来源核对.md]
related_files: [Source/PuddingRuntime/Services/WarmPrefixCompaction.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingContextPolicy/ContextCandidateApplicability.cs, Source/PuddingContextPolicy/ContextCandidateSelector.cs, Source/PuddingRuntimeTests/Services/WarmPrefixCompactionApplyTests.cs, Source/PuddingRuntime/PuddingRuntime.csproj, PuddingAgentNetwork.slnx]
slug: changelog-f2-warm-prefix-single-apply-boundary-2026-10-08
draft: false
---

# 2026-10-08 F2 首批：warm-prefix 唯一应用边界（真实重算 after + 严格 CAS）并接入策略组件

> 范围：`Source/PuddingRuntime/Services/WarmPrefixCompaction.cs`、`Source/PuddingRuntime/Services/AgentExecutionService.cs`、`Source/PuddingContextPolicy/`、`Source/PuddingRuntimeTests/Services/WarmPrefixCompactionApplyTests.cs`、`Source/PuddingContextPolicyTests/`、`Source/PuddingRuntime/PuddingRuntime.csproj`、`PuddingAgentNetwork.slnx`
> 依据：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D3/D4/D5、[修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) §3/§5（提交协议第 1–6 条）、[组件化交付规程](../../../10_conventions/组件化交付规程.md) S5
> 承接：T1 的 §5.1 归因缺陷、T2 的 S1–S4 交付

## 1. 改动

### 1.1 唯一应用边界 `WarmPrefixCompaction.VerifyApplication`

warm-prefix 提交内存历史之前，统一走一个纯判定入口（可脱离宿主单测）：

| 步 | 判定 | 不过时的理由码 |
|---|---|---|
| ① 摘要真实缩小被替换跨度 | `TryCreateCheckpoint` 非空 | `summary_no_gain` |
| ② 严格 CAS | 源 revision、被替换跨度、**跨度之后的保留后缀**、路由版本、工具版本逐项一致 | `history_revision_changed` / `retained_context_changed` / `selected_span_not_intact` / `route_changed` / `tool_spec_changed` / `policy_changed` |
| ③ 加入真实摘要后**重新完整计量**（只计量不发布） | 得到真实 `AfterSnapshot` | — |
| ④ 收益与硬上限准入 | `ContextNetGainAdmission` 允许提交 **且** 实测 after ≤ 有效输入上限 | `no_gain` / `after_summary_exceeds_hard_limit` |

关键纪律落地：

- **计划估算不再冒充 after**：`EstimatedRetainedSnapshot.UsedTokens`（移除后、未加摘要）只作为诊断字段
  `planEstimateExcludingSummaryTokens` 出现；日志与事件的 `afterTokens` 一律取重算后的实测值；
  拒绝且未计量时 `afterTokens = null`，**不用计划值顶替**。
- **真实测量优先于算式**：算式说能过、实测仍越界 ⇒ 一律拒绝提交。
- **拒绝即保持原历史**：不拼接新尾部、不自动 rebase；拒绝结果带 `AppliedMessages = null`。
- **`StaleCandidate` 成为显式处置**：新增 `WarmPrefixCompactionDisposition.StaleCandidate`，
  与 `NoGain`/`Failed` 分开（丢弃陈旧候选不是收益性失败，也不是「已压缩」）。

### 1.2 冻结候选带出身份与版本

`WarmPrefixCompactionPlan` 新增 `RemovedIdentities`、`RetainedIdentities`、`SourceRevisionHash`、
`RouteVersion`、`ToolSpecVersion`、`TargetRatio`、`SourceGeneration`。身份是
`SHA-256(role ‖ content ‖ toolCallId ‖ toolName ‖ toolCalls)`（`ComputeMessageIdentity`），
revision 是身份序列的 SHA-256（`ComputeRevisionHash`）——**不含密钥展开**，`ChatMessage` 无 MessageId，
故采用内容身份。

### 1.3 S5 首个接入（组件 → Runtime）

- `PuddingRuntime.csproj` 增加对 `PuddingContextPolicy` 的 `ProjectReference`；
  `PuddingAgentNetwork.slnx` 登记组件与组件测试工程（各一行）。
- Runtime 在提交边界消费 `ContextCandidateApplicability.Validate` 与 `ContextNetGainAdmission.Evaluate`；
  组件仍是 BCL-only 叶子，**没有**反向依赖被打破（S4 边界断言仍全绿）。

### 1.4 修掉组件里的契约缺陷（由本批测试发现）

`FrozenCandidateDescriptor.RetainedIdentities` 原文档写「保留后缀」，但**调用方容易误用全量保留列表**
（含跨度之前的前导 system）。而 `Validate` 的 `afterSpan` 只覆盖「跨度之后」的部分 ⇒ 误用会稳定退化成
`retained_context_changed`。修正：

- 文档明确：`RetainedIdentities` = **被替换跨度之后**的完整后缀身份；
- `CandidateSelectionResult` 新增 `RetainedSuffixIdentities`，供调用方直接构造描述符；
- 指纹同样改用「跨度之后的后缀」；
- 新增契约回归 `Validate_RoundTripFromSelection_IsApplicable`，并带**反向控制**：误用全量列表必须失效。

## 2. 验证（本机真实执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Source\PuddingRuntime\PuddingRuntime.csproj`（含 S5 引用） | **exit 0**，0 错误 |
| `dotnet test Source\PuddingContextPolicyTests` | **exit 0**，失败 0 / 通过 **43** |
| `dotnet test Source\PuddingRuntimeTests --filter "…WarmPrefixCompactionApplyTests\|…LlmRequestBudgetGuardTests"` | **exit 0**，失败 0 / 通过 **27** |
| 全量 `PuddingRuntimeTests` / `PuddingCoreTests` / `PuddingMemoryEngineTests` / `PuddingPlatformTests` | 见 §2.1 |

新增用例（`WarmPrefixCompactionApplyTests`，5）：

1. `VerifyApplication_AppliesAndReportsReMeasuredAfter`：通过校验时 after 是**重算值**，且**大于**未含摘要的
   计划估算；验证过程只计量不发布（session 快照仍为空）。
2. `VerifyApplication_RejectsStaleCandidate`：冻结后历史追加消息 ⇒ `history_revision_changed`，
   且 `AppliedMessages`/`AfterSnapshot` 都为 null。
3. `VerifyApplication_RejectsNonShrinkingSummary`：超大摘要 ⇒ `summary_no_gain`。
4. `VerifyApplication_RejectsWhenMeasuredAfterStillExceedsHardLimit`：把有效上限压到 1,000，
   摘要确实缩小了跨度、但实测 after 仍越界 ⇒ `after_summary_exceeds_hard_limit`，
   并断言「必须先真实计量过」。
5. `MessageIdentityAndRevision_AreStableAndContentSensitive`：身份与 revision 稳定且对内容敏感。

组件侧新增：`Validate_RoundTripFromSelection_IsApplicable`（含误用全量身份的取红控制）。

### 2.1 全量回归

| 套件 | 结果 |
|---|---|
| `PuddingRuntimeTests` | **exit 0**，失败 **0** / 通过 **2000** / 跳过 6（总计 2006） |
| `PuddingPlatformTests` | **exit 0**，失败 **0** / 通过 **1435** |
| `PuddingCoreTests` | 失败 **1** / 通过 **1065** —— 既有用例隔离缺陷（`LoggingFoundationTests`，单独跑通过） |
| `PuddingMemoryEngineTests` | 失败 **3** / 通过 **286** —— 既有环境依赖的 ContextPipeline 分层用例（干净基线已复现 4 例失败） |
| `dotnet build PuddingAgentNetwork.slnx`（S5 登记后的全解决方案构建门禁） | 见 §2.2 |

### 2.2 全解决方案构建（组件化交付规程 §4.2 门禁）

本批在 `PuddingAgentNetwork.slnx` 登记了两个新工程，因此按规程跑一次覆盖全部工程的构建：

| 项 | 实测 |
|---|---|
| `dotnet build PuddingAgentNetwork.slnx` | **exit 1**，118 条 error |
| 错误种类 | `MSB3021` × 56 + `MSB3027` × 56（**全部落在 `Source/PuddingDesktop`**）+ `CS8604` × 6（**全部落在 `Source/Pudding.Rpc.IpcProbe`**） |
| **我的工程是否有 CS 错误** | **0**：`PuddingContextPolicy`、`PuddingContextPolicyTests`、`PuddingRuntime` 均无 error |
| 归因 | ① `MSB3027/MSB3021` 是**输出文件被运行中进程锁定**：本机 `PuddingAgent`(PID 30228) 与 `PuddingDesktop`(PID 28816) 正在运行 —— 这正是规程 §1.3 记录的基线类失败（原文写的是 `PuddingAgent`，本次落在 `PuddingDesktop`）；② `Pudding.Rpc.IpcProbe` 自 2026-10-01 起未被本批触碰（`git diff 2e0823f..HEAD -- Source/Pudding.Rpc.IpcProbe` 为空），其 6 条 `CS8604` 是该工程把可空警告当错误导致的既有红 |

⇒ 「构建失败工程数 = 0」在本机**不可达**（受运行中进程与既有 IpcProbe 影响），但这**不是**本批引入的。
另：另一位并行写入方的提交 `74ca0e5` 已把本批的 `slnx` 两行登记一并带入（该文件现相对 HEAD 无差异），
所以本提交不再重复包含它；本节保留登记证据。



## 3. 未完成（F2 的剩余部分，仍是最大缺口）

| 项 | 状态 |
|---|---|
| **持久 checkpoint / coverage / generation 与事务提交** | **未实施**：warm-prefix 仍只改内存历史，`durableCheckpoint=false`；`SourceGeneration` 恒为 0，因此「仅代次变化」这一路 CAS 抓不到 |
| **重启恢复**（已提交代次水合、未提交候选标 interrupted） | **未实施** |
| **事件失败补投（同一事务或持久清单幂等补投）** | **未实施**：事件仍可能丢失；本批只保证「applied 才发 completed」与 payload 用真实 after |
| **事务失败回滚测试** | **未实施**（依赖上表第一项） |
| 进程内锁序文档化与竞争/死锁门禁（D4） | **未实施**：本批未新增锁；提交仍在单写者线程内完成 |

⇒ 本批把 F2 的「**唯一应用边界**」与「**after 用真实值**」两件事做实了（这正是用户点名的缺陷），
但 F2 的**持久化与恢复**部分仍未动。交付状态仍为 **ready-for-external-deploy**。

## 4. 关联

- ADR：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D3（一个候选应用边界）、D5（提交与恢复以持久 checkpoint 为准）
- 方案 §3/§5：[首 Token 等待与上下文指示器修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)
- 规程：[组件化交付规程](../../../10_conventions/组件化交付规程.md) S5
- 前序：[策略组件 S1–S4](2026-10-08-新增独立策略组件PuddingContextPolicy-S1-S4.md)、[分类回放](2026-10-08-软硬分类回放与393216容量来源核对.md)
