---
title: 2026-10-07 file_patch 失败路径归类 contract_error
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "给 file_patch 的 12 个参数合同类失败点接上结构化 status=contract_error，与 search_grep 的既有惯例对齐；运行时状态失败（目标不存在、hunk 不匹配、回滚、IO）保持 status=null。新增 6 个 D11 测试，用双向变异取红证明 5 个正向断言与 1 个反向守护各自独立生效。"
categories: [docs, changelog]
tags: [file_patch, tool-contract, structured-status, contract-error, mutation-testing]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-07-file_patch结构化结果状态.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs]
slug: changelog-file-patch-contract-error-2026-10-07
draft: false
---

# 2026-10-07 file_patch 失败路径归类 contract_error

## 背景

上一轮给 `file_patch` 的 **成功**返回点接上了 `ok` / `no_match`（见 `2026-10-07-file_patch结构化结果状态.md`），当时明确把「失败路径的 `contract_error` 归类」留作后续——本文件即那次遗留的收口。

`fail` 路径此前一律 `ToolExecutionResult.Fail(文本)`，`Status` 恒为 `null`。于是一个**调用方参数写错**（缺 `old_text`、操作类型拼错、`operations` 整段缺失）与一个**运行时状态失败**（目标文件不存在、补丁上下文不匹配、写盘回滚）在结构化层面完全同形。同层工具 `search_grep` 早已区分这两类：参数合同错误一律带 `status: contract_error`（`SearchGrepTool.cs:122/147/342/427/477/486`）。本次让 `file_patch` 与之对齐。

## 改动

`Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`：**12 个**失败点接上 `status: ToolResultStatuses.ContractError`。

| # | 失败语义 | 归因 |
|---|---|---|
| 1 | `At least one patch with operations is required.` | 请求体为空，纯参数缺失 |
| 2–3 | `resolveError`（string-replace 路径与 unified-diff 路径各一处） | 路径解析失败 / 越出工作区 |
| 4–5 | agent private 文件缺 `reason`（两条路径各一处） | 缺少必需参数 |
| 6 | `Unknown operation type '{opType}'` | 操作类型拼错 |
| 7 | `requires 'new_text'` | 缺少必需参数 |
| 8 | `regexReplace ... requires 'replacement'` | 缺少必需参数 |
| 9 | `requires 'old_text'` | 缺少必需参数 |
| 10 | `regexReplace ... requires 'pattern'` | 缺少必需参数 |
| 11 | 歧义边界拒绝（`CollectReplacements` 的 `refusals`，唯一来源 `FilePatchTool.cs:409` 的 `refusals.AddRange(ambiguous)`） | `old_text` 边界无法安全解释，参数不可解 |
| 12 | `parsed.Error ?? "Invalid unified diff."` | `patch_text` 无法解析 |

**明确不归类**（保持 `Status = null`，6 个点）：`File not found`（两条路径）、`Failed to patch file ... {ex.Message}`、`Atomic patch commit failed, rolled back`、`applyResult.Error ?? "Failed to apply patch"`（hunk 上下文不匹配）、`Failed to write unified patch transaction`。理由：这些是目标状态与 IO 的失败，**改参数也过不了**；把它们报成 `contract_error` 会让调用方去"修参数"而错过真正的环境问题。

`Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs`：新增 **D11** 六个测试。

- 五个正向：`ContractError_{NoPatches,UnknownOperationType,ReplaceWithoutOldText,RegexReplaceWithoutReplacement,InvalidUnifiedDiff}_ReportsContractError` —— 逐个锁定一条合同错误路径的 `contract_error`，并断言失败时文件字节未变。
- 一个反向：`FileNotFound_IsNotReportedAsContractError` —— 守护「运行时状态不得被误报为参数问题」。

## 影响面（实测，不是推断）

`ContractError` 在 **PuddingRuntime 全量** grep（`ToolResultStatuses.`）后可见：**没有任何语义消费点**。读取 `Status` 做分支的地方只有三处，且都不看 `contract_error`：

- `ToolInvocationService.cs:158-159` —— 只看 `human_decision_required` / `dependency_wait`；
- `FailedToolCallTracker.cs:21` —— 同上两个值；
- `SearchGrepTool.cs:245-249` —— 只看 `no_match` / `timeout` / `truncated`。

另外 `FailedToolCallTracker` 的**失败签名**由 `ExitCode` + `Output` + `Error` 计算（`ComputeFailureSignature`），**不含 `Status`** —— 因此本次改动不会改变「同一调用重复失败 → `execution_stalled`」的判定，纯新增事实。

## 验证（先绿后红 + 双向变异取红）

| 阶段 | 证据文件 | 结果 |
|---|---|---|
| 接线 + 新增 D11 后 | `temp/hb15-test-green.txt` | 失败 **0**，通过 **55**，总计 55（D1–D10 原为 49） |
| 变异 A：12 处 `ContractError` → `NoMatch` | `temp/hb15-mut-a-summary.txt` | **取红 5 例**（五个正向用例全红），失败 **5** / 通过 50 / 总计 55 |
| 变异 B：仅 `Unknown operation type` 一处摘掉 `status` | `temp/hb15-mut-b-summary.txt` | **取红恰好 1 例**（`ContractError_UnknownOperationType_ReportsContractError`，实测 `<contract_error>` vs `<>`），失败 **1** / 通过 54 |
| 复原（bit-identical） | `temp/hb15-mut-a-log.txt` / `temp/hb15-mut-b-log.txt` | `restored_sha256=b28eb9a96b4dba7d9bc600a9cc40c25285a1dfc7fde9eb6fe1264bcc18d74af6`，`matches_pristine=True` |
| 复原后复跑 | `temp/hb15-test-restored.txt` | 失败 **0**，通过 **55**，总计 55 |

改动前 `FilePatchTool.cs` 的 sha256 = `6bdc003d60c8b0c43919f8171c306eec901d1648671a86b47b47760a9a67b053`，与上一轮 changelog 记录的 `restored_sha256` **逐字一致**——顺带交叉验证了上一轮的复原声明，改动确实是从那份字节开始的。

**测试顺序诚实说明**：本轮是「先接生产、后加测试」（与上一轮相反）。因此取红不来自"先跑红"，而来自变异 A/B；其中**变异 B 精确模拟了"某一点未接线"的状态**（其余 11 点已接线），结果恰好只有对应用例变红——这同时证明了新测试非空转、且各测试守护各自的点、无相互串扰。

变异与复原由 `temp/hb15-contract.py`（12 点接线）、`temp/hb15-d11-insert.py`（D11 插入）、`temp/hb15-mutate.py A|B`（变异 + 复原 + sha 校验）脚本化执行，所有断言都在写盘之前；`temp/` 已在 `.gitignore` 内。

## 边界与遗留

- **`applyResult.Error`（hunk 上下文不匹配）暂不归类**，但它是个真空白：既可能是调用方 `patch_text` 写错（应算合同错误），也可能是文件在读取后被改（应算运行时状态）。当前实现无法区分，留作观察项，不硬塞。
- agent private 缺 `reason` 的两个点（#4/#5）**没有测试覆盖**——构造 agent private 路径需要 `PuddingDataPaths` 与真实 `agents/<id>/` 目录，属另一类测试夹具，本轮不做。
- `contract_error` 目前仍是「只写不读」的事实字段（见影响面），真正产生行为差异需要消费端（如模型提示注入"这是参数问题，请改参数"）来读它，属另一项设计。
- 宿主仍运行此前构建的程序集，本次改动需部署后才在生产 Agent 上生效。
