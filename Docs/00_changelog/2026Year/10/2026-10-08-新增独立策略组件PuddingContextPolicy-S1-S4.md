---
title: 2026-10-08 新增独立策略组件 PuddingContextPolicy（S1–S4 交付，未接入）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "按组件化交付规程建 Source/PuddingContextPolicy（BCL-only，ProjectReference 与 PackageReference 均为 0）与其独立测试工程：承接容量算术、候选边界与稳定指纹、严格适用校验、净收益准入、退避判定五类纯策略；含 2026-10-07 事故两条样本的记录值回放。S1–S4 已交付并取得取红证据，S5（slnx 登记 / 消费方引用 / DI 装配）按规程留到下一步，未改动宿主。"
categories: [docs, changelog]
tags: [context, policy, component, adr-095, s1-s4, boundary]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/10_conventions/组件化交付规程.md, Docs/00_changelog/2026Year/10/2026-10-08-软硬分类回放与393216容量来源核对.md]
related_files: [Source/PuddingContextPolicy/PuddingContextPolicy.csproj, Source/PuddingContextPolicy/ContextPolicyContracts.cs, Source/PuddingContextPolicy/ContextCapacityArithmetic.cs, Source/PuddingContextPolicy/ContextCandidateSelector.cs, Source/PuddingContextPolicy/ContextCandidateApplicability.cs, Source/PuddingContextPolicy/ContextNetGainAdmission.cs, Source/PuddingContextPolicy/ContextBackoffPolicy.cs, Source/PuddingContextPolicyTests/ComponentBoundaryTests.cs]
slug: changelog-pudding-context-policy-component-s1-s4-2026-10-08
draft: false
---

# 2026-10-08 新增独立策略组件 PuddingContextPolicy（S1–S4 交付，未接入）

> 依据：[修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md) §6.1、[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) D1/D2/D3/D6、
> [组件化交付规程](../../../10_conventions/组件化交付规程.md) S1–S4（**S5 之前的任何一步都不得改动宿主**）
> 承接：用户在下一批任务里把「独立策略组件 S1–S4」列为第 2 步
> 前置事实：[分类回放与 393,216 核对](2026-10-08-软硬分类回放与393216容量来源核对.md)

## 1. 交付了什么

| 步 | 内容 | 证据 |
|---|---|---|
| **S1** | 新工程 `Source/PuddingContextPolicy/PuddingContextPolicy.csproj` | 叶工程：`ProjectReference` **0**、`PackageReference` **0**（只依赖 BCL）；单独 `dotnet build` exit 0、0 警告 |
| **S2** | 新测试工程 `Source/PuddingContextPolicyTests/PuddingContextPolicyTests.csproj` | `ProjectReference` **恰好 1 条**（只引用本组件）、`PackageReference` 0 |
| **S3** | 无宿主验证 | 组件与测试工程均可独立 build + test；测试进程不加载宿主/上层组件/重依赖（由 S4 断言） |
| **S4** | 边界断言（机器可验） | `ComponentBoundaryTests`：检测器自检、进程加载集、声明依赖闭包、组件元数据引用仅 BCL；**取红证据见 §3** |

**S5 未做**：未登记 `PuddingAgentNetwork.slnx`、未在消费方 csproj 加引用、未进 DI 组合根。宿主一行未改。

## 2. 组件边界（五类纯策略）

| 文件 | 职责 | 关键不变量 |
|---|---|---|
| `ContextPolicyContracts.cs` | 输入/输出合同（最小不可变 DTO，只有整数与稳定身份） | 不带正文；来源取值与 Runtime `ContextEffectiveWindowSources` 逐字对齐；`ContextPolicyVersion` 参与指纹 |
| `ContextCapacityArithmetic.cs` | 容量算术 + 压力分类 | `effectiveInput = min(providerInputLimit, modelWindow − reservedOutput − safetyBuffer)`；**先判硬边界再判软阈值**；回退预留记 `fallback_output_reserve` |
| `ContextCandidateSelector.cs` | 候选边界与稳定指纹 | 前导 System / 最后 N 条 / 当前用户轮不可移除；只按完整会话单元移除（不拆 tool call 与 result）；`ProjectedUsedTokensExcludingSummary` 明确**不是**提交后的值 |
| `ContextCandidateApplicability.cs` | 严格适用校验 | 代次/revision/路由/工具/策略任一变化 ⇒ `stale_candidate`；**保留后缀与当前历史逐位一致**才可提交（新消息追加/中间插入/删改一律失效）；不做尾部拼接与自动 rebase |
| `ContextNetGainAdmission.cs` | 净收益准入 | 必须用**真实摘要长度**重算 after；空/不缩小 ⇒ `no_gain`；加摘要后仍越界 ⇒ 拒绝提交；未达目标允许提交但如实记录 |
| `ContextBackoffPolicy.cs` | 退避/去重 | **硬保护优先于任何退避**；同候选同代次上的 `applied` 记录不可信（成功应用应推进代次） |

事故样本的记录值直接进了组件测试：`583,691 ⇒ 软`、`625,824 ⇒ 硬`（上限 `605,760`、软阈值 `484,608`），
以及净收益的 `583,691 − 257,299 + 4,343 = 330,735`（真实 after，而非计划估算）。

## 3. 验证（本机真实执行）

| 命令 | 结果 |
|---|---|
| `dotnet build Source\PuddingContextPolicy\PuddingContextPolicy.csproj` | **exit 0**，0 错误 / 0 警告 |
| `dotnet test Source\PuddingContextPolicyTests\PuddingContextPolicyTests.csproj` | **exit 0**，失败 0 / 通过 **42** / 跳过 0 |

**S4 取红（可证伪性）**：临时给测试工程加一条 `ProjectReference` 指向 `PuddingRuntime`：

| 状态 | 结果 |
|---|---|
| 变异后 | `dotnet test` **exit 1**：`Test_Process_Must_Not_Load_Forbidden_Assemblies` 与 `Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies` **变红**（2 失败 / 40 通过） |
| 复原后 | csproj `SHA256` 逐位复原（与变异前一致）⇒ 复跑 **42/42 全绿** |

注意 `Component_Assembly_References_Must_Stay_BclOnly` 在变异下**保持绿**，这是正确的：变异加在**测试工程**上，组件程序集本身的元数据引用仍然只有 BCL —— 三条断言各自盯不同的边界面。

## 4. 未完成 / 下一步

- **S5 接入**（`slnx` 登记、消费方 csproj 引用、DI 装配、宿主组合测试）**未做**，按规程必须等 S1–S4 通过之后，且接入前不得改动宿主。
- 组件目前**未被任何生产代码使用**：Runtime 里既有的容量算术（`LlmRequestBudgetGuard.ResolveEffectiveInputLimit`、`ContextHealthEvaluator`）、候选选择（`WarmPrefixCompaction.TryCreatePlan`）、无收益抑制（`CompactionCoordinator`）都还是旧实现，**尚未换成组件**。因此本提交**不改变任何产品行为**。
- F1 请求归因（T3）、F2 唯一提交边界（T4）、F3/F4/F5 接缝（T5）仍未实施；交付状态仍为 **ready-for-external-deploy**。

## 5. 关联

- ADR：[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md)（D1 维护准入、D2 软硬分离、D3 单一提交边界、D6 归因）
- 方案 §6.1：[首 Token 等待与上下文指示器修复方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)
- 规程：[组件化交付规程](../../../10_conventions/组件化交付规程.md) S1–S5
- 组件索引：[`Source/PuddingContextPolicy/code_map.md`](../../../../Source/PuddingContextPolicy/code_map.md)
