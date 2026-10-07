---
title: 2026-10-07 file_patch 预览 diff 行对齐（P1-①）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "把 file_patch 的预览 diff 从逐行序号比较改为行对齐，单行插入不再被报成整段替换；同时消除两份重复渲染器。"
categories: [docs, changelog]
tags: [file_patch, diff, regression]
related_docs: [Docs/12_features/file-patch文本边界修复方案-2026-10-07.md, Docs/14_reports/工具调用归因分析-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs]
slug: changelog-file-patch-preview-diff-alignment-2026-10-07
draft: false
---

# 2026-10-07 file_patch 预览 diff 行对齐（P1-①）

## 改动

- `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`
  - 新增 `internal static class SimpleLineDiff`：行对齐的预览 diff 渲染器，含 `Render` / `SplitLines` / `BuildScript` / `IndexOfLine`，`MaxChangeGroups = 10`、`MaxLookahead = 200`。
  - 删除 `FilePatchTool` 与 `UnifiedDiffPatchRunner` 内**两份逐字重复**的私有 `GenerateSimpleDiff`（各 18 行），消除同一逻辑的双真相源。
  - 3 处调用点改指 `SimpleLineDiff.Render`：`ExecuteCoreAsync` 的 dry-run 与写盘两条路径传 `original/current`，批量路径（`UnifiedDiffPatchRunner`）传 `file.Original/file.Current`。
- `Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs`：新增 D7 四例（单行插入 / 单行删除 / 多行插入 / 同位替换对照）与 `DiffLines` 助手（只取预览自身的 `- ` / `+ ` 行，避免摘要文字掩盖漏判）。
- `Source/PuddingRuntime/code_map.md`：`FilePatchTool.cs` 行约束列追加「预览 diff 按行对齐」。

## 为什么

旧实现按下标比较 `oldLines[i]` 与 `newLines[i]`：插入一行会让其后每一行都不相等，预览把**一次插入**报成整段替换；且 10 行预算被虚假差异耗尽，真实编辑被截断成 `... (more changes)`。这是此前把「补丁后回显错乱」归因为「工具改写、只能人工复核」的根因之一——属**仪表缺陷**（工具自述与磁盘字节不符），不是内容变更。

## 验证（先红后绿，同一门禁）

- 红（改实现前，`temp/p1-red.txt`）：`dotnet test --filter FullyQualifiedName~FilePatch` ⇒ `Failed: 3, Passed: 36, Total: 39`；三条红恰为 D7 的插入 / 删除 / 多行插入，对照组同位替换通过。
- 绿（改实现后，`temp/p1-green.txt`）：同过滤 ⇒ `Failed: 0, Passed: 39, Total: 39`。
- 回归（`temp/p1-regress.txt`）：`--filter FullyQualifiedName~Patch`（FilePatch + ApplyPatch 等）⇒ `Failed: 0, Passed: 126, Total: 126`。
- 落盘核对（`temp/p1-impl-out.txt`）：脚本断言内联调用点 2、批量调用点 1、重复定义 2，替换后 `GenerateSimpleDiff` 残留 **0**、`SimpleLineDiff` 引用 **4**；文件行尾前后一致（LF，`crlf=0`）。首次运行因断言算错引用数（少算定义体）而**中止且未写盘**，修正断言后才落盘——断言确实拦住了。

## 边界与遗留

- 预览是只读文本，不参与写入，不改变补丁语义。
- 对齐用「较短跳过者即真实编辑」的游标法，不是最优编辑距离；重复行密集的文件可能给出次优分组，仅影响预览可读性。
- 运行中宿主仍是 2026-10-06 22:57 构建 ⇒ 本修复**未生效**，需 Core 部署；其后 P0-3（结构化结果字段）与 `CollectReplacements` scope 坐标缺陷仍待做。
