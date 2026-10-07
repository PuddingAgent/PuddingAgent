---
title: 2026-10-07 file_patch 成功结果补结构化 status（no_match）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "给 file_patch 的 4 个成功返回点接上结构化 status：目标范围已完整处理却没有任何文件被改动时上报 no_match（成功态），与 ok 和失败严格区分；用双向变异取红证明 5 个新测试各自守护一个方向。"
categories: [docs, changelog]
tags: [file_patch, tool-contract, structured-status, mutation-testing]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-07-file-patch预览diff截断语义补测.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs, Source/PuddingCore/Tools/PuddingToolContracts.cs]
slug: changelog-file-patch-structured-status-2026-10-07
draft: false
---

# 2026-10-07 file_patch 成功结果补结构化 status（no_match）

## 背景

`ToolExecutionResult`（`Source/PuddingCore/Tools/PuddingToolContracts.cs`）用 `Status` 承载结构化分类，约定值由 `ToolResultStatuses` 定义（`ok` / `no_match` / `truncated` / `timeout` / `contract_error` / `dependency_wait` / `human_decision_required` / `exact_retry_suppressed`）。同层工具都已接上：`file_search`、`search_grep`、`list_llm_providers`、`host_shell`。

**`file_patch` 是唯一的缺口**：它的 4 个成功返回点全部写作 `ToolExecutionResult.Ok(文本)`，`Status` 恒为 `null`。于是在结构化层面，「补丁匹配不到任何文本、磁盘一个字节都没动」与「真的改写了文件」**完全一样**，调用方只能从自由文本里猜——与本文件此前的 scope 偏移缺陷同一根因。

## 改动

1. `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`
   - 新增文件级 `internal static class FilePatchStatus`（置于同文件的 `UnifiedDiffPatchRunner` 之前：它也在同一状态判定中使用，而 `FilePatchTool` 的私有成员对它不可见——首次编译在 L1297/L1325 报了 CS0103，这是实测校正后的位置）。
   - 判定规则：`anyFileChanged ? ToolResultStatuses.Ok : ToolResultStatuses.NoMatch`。
   - 4 个成功返回点全部接上：string-replace 的 dry-run 与写盘用 `batchResults.Any(r => r.Original != r.Current)`；unified-diff 的 dry-run 与写盘用 `touchedFiles.Any(f => f.Original != f.Current)`。
   - 失败路径不动：`Fail` 已携带 `Error`，把「缺 new_text / 未知 op / 歧义拒绝」归类为 `contract_error` 属另一件事，留作后续。
2. `Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs`：新增 **D10** 五个测试，覆盖两条写入路径 × 两个状态方向。
   - `Patch_AppliedChange_ReportsStatusOk` —— 真写盘 ⇒ `ok`。
   - `Patch_MatchedNothing_ReportsNoMatchWithoutFailing` —— 匹配不到 ⇒ `Success=true` + `no_match`（显式断言：这是成功态，不是失败也不是普通 ok），且原因仍留在 `Output` 里、文件字节不变。
   - `DryRun_PreviewWithChange_ReportsStatusOk` / `DryRun_PreviewWithoutChange_ReportsStatusNoMatch` —— 预览路径同规则。
   - `UnifiedDiff_AppliedPatch_ReportsStatusOk` —— `patch_text` 路径共用同一状态契约。

## 影响面（实测，不是推断）

- `ToolResultStatuses.NoMatch` 的**语义消费点**全仓只有两处：`search_grep` 自己生成提示（`SearchGrepTool.cs:245`）与 `FailedToolCallTracker`（只看 `dependency_wait` / `human_decision_required`）。`AgentExecutionService.{Buffered,Streaming}.cs` 对 `Status` 仅**透传**给事件与模型上下文。
- 因此本次只新增事实、不引入行为分支；`no_match` 在此前的 `file_search` 已有同义用法（覆盖完整且零命中），语义一致。

## 验证（先绿后红 + 双向变异取红）

| 阶段 | 证据文件 | 结果 |
|---|---|---|
| 接线 + 新增 D10 后 | `temp/hb14-test-green.txt` | 失败 **0**，通过 **49**，总计 49（D1–D9 原为 44） |
| 变异 A：恒返回 `ok` | `temp/hb14-mut-a-summary.txt` | **取红**：`Patch_MatchedNothing_ReportsNoMatchWithoutFailing`、`DryRun_PreviewWithoutChange_ReportsStatusNoMatch` 失败（**2 失败 / 47 通过**） |
| 变异 B：恒返回 `no_match` | `temp/hb14-mut-b-summary.txt` | **取红**：三个 `ReportsStatusOk` 用例失败（**3 失败 / 46 通过**） |
| 复原（bit-identical） | `temp/hb14-mut-a-log.txt` / `temp/hb14-mut-b-log.txt` | `restored_sha256=6bdc003d60c8b0c43919f8171c306eec901d1648671a86b47b47760a9a67b053`，`matches_pristine=True` |
| 复原后复跑 | `temp/hb14-test-regreen.txt` | 失败 **0**，通过 **49**，总计 49 |

两个变异方向各自只打红对应的用例集 ⇒ 五个测试均非空转，且分别守护「有改动必须是 ok」与「无改动必须是 no_match」。变异与复原由 `temp/hb14-mutate.py` 脚本化执行（正则锚点 + 备份字节写回 + sha256 校验），全部断言在写盘之前。

## 边界与遗留

- `no_match` 的判据是「**一个文件都没被改写**」，不是「有文件没匹配上」：批量补丁里只要有一处真正落盘，整体仍是 `ok`（更细的逐文件状态需要 `ToolContentParts` 之类的结构化通道，属另一项设计）。
- `Fail` 路径的 `contract_error` 归类未做（缺 `new_text`、未知 op、歧义边界拒绝等目前仍是不带 status 的失败）。
- 宿主仍运行 2026-10-06 22:57 构建的程序集，本次改动需部署后才在生产 Agent 上生效。
