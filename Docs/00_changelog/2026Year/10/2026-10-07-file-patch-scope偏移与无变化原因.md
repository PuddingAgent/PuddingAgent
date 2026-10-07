---
title: 2026-10-07 file_patch scope 行范围偏移与无变化原因（P1-②）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "file_patch 的 scope_start_line/scope_end_line 偏移改为按原始文本计算（CRLF 文件不再少算每行一个 \\r）；写入路径在文件未变化时不再吞掉问题原因。"
categories: [docs, changelog]
tags: [file_patch, scope, crlf, observability, regression]
related_docs: [Docs/12_features/file-patch文本边界修复方案-2026-10-07.md, Docs/14_reports/工具调用归因分析-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs]
slug: changelog-file-patch-scope-offset-and-unchanged-reason-2026-10-07
draft: false
---

# 2026-10-07 file_patch scope 行范围偏移与无变化原因（P1-②）

## 改动

- `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`
  - `CollectReplacements`：scope 偏移的行切分由 `original.Replace("\r\n", "\n").Split('\n')` 改为 `original.Split('\n')`。
  - 写入路径的未变化分支（`current == original`）改为把 `errors` 附到 `"{relPath}: unchanged"` 之后，与 dry-run 分支的既有行为对齐。
- `Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs`：新增 D8 三例（CRLF 文件 scope 内必须应用 / scope 外必须拒绝 / LF 文件对照）。
- `Source/PuddingRuntime/code_map.md`：`FilePatchTool.cs` 行约束列追加「scope 行范围按原文计偏移；无变化时也报出问题原因」。

## 为什么

**缺陷一（功能失效）**：`scopeStart/scopeEnd` 是 `original` 的**字符偏移**——候选匹配的 `match.Index` 就来自同一份 `original`——但偏移量却在一份**行尾已规范化**（CRLF→LF）的副本上累加。CRLF 文件每有一条前置行，窗口就被少算 1 个字符：`scope_end_line` 覆盖到的那一行若匹配自带行尾换行，`match.Index + match.Length <= scopeEnd` 随即不成立，落在 scope 内的替换被当成「找不到」。这属**假阴性**，工具把用户的越界误判成自己的匹配失败。

**缺陷二（可观测性）**：同一份原因列表在两条路径上待遇不同——dry-run 的未变化分支会附 `errors`，真实写入路径却只输出 `"{relPath}: unchanged"`。于是「scope 把候选全部滤掉」「old_string 根本不存在」这类硬失败，与「本来就无需改动」在回显上完全无法区分；调用方拿不到任何可执行的下一步。

## 验证（先红后绿，同一门禁）

- 红（改实现前，`temp/p1b-red.txt`）：`dotnet test --filter FullyQualifiedName~FilePatch` ⇒ `Failed: 2, Passed: 40, Total: 42`。
  - `Replace_ScopeLineRangeOnCrlfFile_CountsTheCarriageReturnsInTheOffset` 失败于文件仍为 `alpha␍␊target␍␊beta␍␊`（D8-1 抓到缺陷一）。
  - `Replace_ScopeLineRange_StillRejectsMatchesOutsideTheScope` 失败于回显只有 `scoped.txt: unchanged`、不含 `not found`（D8-2 顺带抓到缺陷二）。
  - 对照组 `Replace_ScopeLineRangeOnLfFile_KeepsWorking` 通过 ⇒ 偏差只发生在 CRLF 上，与推断一致。
- 绿（改实现后，`temp/p1b-green.txt`）：同过滤 ⇒ `Failed: 0, Passed: 42, Total: 42`。
- 回归（`temp/p1b-regress.txt`）：`--filter FullyQualifiedName~Patch` ⇒ `Failed: 0, Passed: 129, Total: 129`（126 旧 + 3 新）。
- 落盘核对（`temp/p1b-impl-out.txt`）：脚本断言两处锚点各命中 1 次；`FilePatchTool.cs` sha256 `ba1498cb…ea2a` → `02e2bcf…753ee`，1774 → 1785 行（+975 字符），行尾前后一致（LF，`crlf=0`）。
- code_map 单文件检查（`temp/p1b-codemap-check.txt`，tool sha `2d1ae2a1…2759d`）：`error 0 · warn 1 · gate PASS`，唯一 warn 为 `stale-fingerprint`（提交前按既定策略不刷新）。
- code_map 行更新（`temp/p1b-codemap-row.txt`）：断言命中 1 次、**行数 414 → 414、管道符 2178 → 2178** 不变。

## 边界与遗留

- scope 语义不变：仍是「1-based、闭区间、以行首/行尾含换行为界的字符窗口」；本次只修偏移基准，不放宽或收紧范围判定。
- 未变化分支新增的 `errors` 文本是**追加**信息，不改变成功/失败判定（失败仍由 `refusals` 走 `ToolExecutionResult.Fail`）。
- 运行中宿主仍是 2026-10-06 22:57 构建 ⇒ 本修复**未生效**，需 Core 部署；file_patch 余项仅剩 P0-3（结构化结果字段）与预览 diff `MaxChangeGroups` 截断语义的缺测。
