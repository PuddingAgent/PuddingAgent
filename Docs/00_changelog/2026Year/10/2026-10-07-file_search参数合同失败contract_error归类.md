---
title: 2026-10-07 file_search 参数合同失败归类 contract_error
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "给 file_search 的 4 个参数合同类失败点接上结构化 status=contract_error（provider 名不存在、BuiltIn 缺 directory、Everything 缺 directory、pattern 含通配 '**'），宿主可用性与运行时失败保持 status=null；新增 5 个测试并用双向变异取红证明各断言独立生效。至此 file_patch / file_search / search_grep 三个同族工具的状态语义完全对齐。"
categories: [docs, changelog]
tags: [file_search, tool-contract, structured-status, contract-error, mutation-testing]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-07-file_patch失败路径contract_error归类.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FileSearchTool.cs, Source/PuddingRuntimeTests/Tools/FileSearchToolTests.cs]
slug: changelog-file-search-contract-error-2026-10-07
draft: false
---

# 2026-10-07 file_search 参数合同失败归类 contract_error

## 背景

上一轮把 `file_patch` 的失败路径归类为 `contract_error`（见 `2026-10-07-file_patch失败路径contract_error归类.md`），当时记下的下一步就是"按同一模式补齐 `file_search`"。本文件即那次收口。

`FileSearchTool` 改造前有 **10 个 `ToolExecutionResult.Fail` 点，全部不带 `Status`**；成功路径却早已带结构化状态（`BuildResultStatus`：`truncated` / `no_match` / `ok`）。于是「调用方参数写错」与「宿主没有可用 provider」「目标目录不存在」在结构化层面完全同形。

## 改动

`Source/PuddingRuntime/Tools/BuiltIns/Files/FileSearchTool.cs`（sha256 `9143bd3d…` → `059514ce…`，824 → 830 行）：**4 个**失败点接上 `status: ToolResultStatuses.ContractError`。

| # | 失败语义 | 归因 |
|---|---|---|
| 1 | `File search provider not found: {providerId}` | provider 名拼错/不存在 |
| 2 | `Directory is required for provider BuiltInRecursiveFileSearch.` | 缺少必需参数 `directory` |
| 3 | `Everything requires an absolute directory.`（经 `BuildEverythingDirectoryGuidance` 包装） | 缺少必需参数 `directory` |
| 4 | `Pattern '{pattern}' contains '**' which is not supported by Windows file search.` | `pattern` 使用了不受支持的 glob 语法 |

**明确不归类**（保持 `Status = null`，6 个点）：

| 失败语义 | 为什么不归类 |
|---|---|
| `Provider {providerId} is not available on this host.`（`require_provider=true`） | 宿主可用性；同一参数换个环境就能过 |
| `Provider … is not available on this host and no fallback provider is available.` | 同上 |
| `No file search provider available.` | 宿主未注册任何可用 provider |
| `Directory not found: {directory}` | 目标状态（不存在），与 `file_patch` 的 `File not found` 同判 |
| `File search failed using provider …: {ex.Message}`（含 fallback 也失败的那条） | 执行期异常（IO / SDK 故障） |

`Source/PuddingRuntimeTests/Tools/FileSearchToolTests.cs`（606 → 711 行）：新增 5 个测试。

- 四个正向：`UnknownProvider` / `BuiltInProviderWithoutDirectory` / `EverythingWithoutDirectory` / `UnsupportedDoubleStarPattern` + `_ReportsContractError`，各自锁定一条参数合同路径。
- 一个反向：`MissingDirectoryTarget_IsNotReportedAsContractError` —— 守护「目录不存在不得被误报为参数问题」。

## 影响面

`contract_error` 的语义消费点此前已在同一工作树上实测：**全仓无消费点**（读 `Status` 分支的只有 `ToolInvocationService.cs:158-159` 与 `FailedToolCallTracker.cs:21`，均只看 `dependency_wait` / `human_decision_required`；`SearchGrepTool.cs:245-249` 只看 `no_match` / `timeout` / `truncated`）。`FailedToolCallTracker.ComputeFailureSignature` 由 `ExitCode` + `Output` + `Error` 计算、不含 `Status` ⇒ 本次仍是**只新增事实、零行为变更**。

## 验证（先绿后红 + 双向变异取红）

| 阶段 | 证据文件 | 结果 |
|---|---|---|
| 接线 + 新增 5 例后 | `temp/hb16-test-green.txt` | 失败 **0**，通过 **37**，总计 37（此前 32） |
| 变异 A：4 处 `ContractError` → `NoMatch` | `temp/hb16-mut-a-summary.txt` | **取红 4 例**（四个正向用例全部变红），失败 **4** / 通过 33 |
| 变异 B：仅 Everything-缺目录一处摘掉 `status` | `temp/hb16-mut-b-summary.txt` | **取红恰好 1 例**（`EverythingWithoutDirectory_ReportsContractError`，实测 `<contract_error>` vs `<>`），失败 **1** / 通过 36 |
| 复原（bit-identical） | `temp/hb16-mut-a-log.txt` / `temp/hb16-mut-b-log.txt` | `pristine_sha256=restored_sha256=059514ce5863786111fbf5f75999cc3c8251c814d27c1b7c7b57823af8d8d4c0`，`matches_pristine=True`（两次变异各自独立复原） |
| 复原后复跑 | `temp/hb16-test-restored.txt` | 失败 **0**，通过 **37**，总计 37 |

脚本：`temp/hb16-contract.py`（4 点接线，断言 `fail_sites=10` / `status_lines=4`）、`temp/hb16-d12-insert.py`（5 例插入，`wide_lines=0`）、`temp/hb16-mutate.py A|B`（变异 + 复原 + sha 校验）。所有断言都在写盘之前，`temp/` 已在 `.gitignore` 内。

## 边界与遗留

- 「provider 不可用」三条**刻意不归类**：它们由宿主环境决定，报成 `contract_error` 会诱导调用方反复改参数。这是判断口径，不是遗漏。
- 同族三工具（`search_grep` / `file_patch` / `file_search`）状态语义至此对齐：成功 `ok`/`no_match`、截断 `truncated`、超时 `timeout`、参数错 `contract_error`。
- `contract_error` 仍是「只写不读」的事实字段；产生行为差异需要消费端（例如把"这是参数问题"作为提示注入模型上下文），属另一项设计。
- `file_patch` 的 `applyResult.Error`（hunk 上下文不匹配）仍未归类（既可能是 `patch_text` 写错，也可能是文件已被改），继续留作观察项。
- 宿主仍运行此前构建的程序集，本次改动需部署后才在生产 Agent 上生效。
