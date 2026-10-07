---
title: 2026-10-07 file_patch 预览 diff 截断语义补测（SimpleLineDiff）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "为 file_patch 预览 diff 的 10 变更组预算补两个可失败的测试（10 组不截断 / 11 组截断并标记），并用双向变异取红证明测试非空转；顺带纠正将其称为 10-row budget 的错误注释。"
categories: [docs, changelog]
tags: [file_patch, testing, mutation-testing, preview-diff]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-07-file-patch-scope偏移与无变化原因.md]
related_files: [Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs, Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs]
slug: changelog-file-patch-preview-truncation-tests-2026-10-07
draft: false
---

# 2026-10-07 file_patch 预览 diff 截断语义补测

## 背景

`file_patch` 回显的预览 diff 由 `SimpleLineDiff.Render` 渲染，它有一个 `MaxChangeGroups = 10` 的预算：超过 10 个变更组就停止渲染并追加 `... (more changes)`，且这个上限**只影响预览**、绝不影响真正写入的内容。此前这条语义**没有任何测试**——既有 D1–D8 覆盖的是参数绑定、缩进、行尾、歧义边界与 scope 偏移，谁都没有锁住预览的截断行为。

顺带发现 `SimpleLineDiff` 的类注释把它写成 "the whole **10-row** budget"——预算单位是**变更组**而非行（一组可以是 1 行插入或 1 行替换的 2 行）。

## 改动

1. `Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs`：新增 **D9** 两个测试 + 两个私有构造 helper（`NumberedLines` / `NumberedReplacements`）。
   - `Preview_ExactlyTenChangeGroups_IsNotMarkedAsTruncated`：20 行文件做 10 处分散替换 ⇒ 输出**不得**含 `... (more changes)`，且必须渲染到第 10 组（`ROW-10`）。
   - `Preview_ElevenChangeGroups_IsTruncatedAfterTenWithMarker`：11 处替换 ⇒ 必须含 `... (more changes)`、**不得**泄漏第 11 组内容（`ROW-11`），且第 11 处改动**必须已写入磁盘**（截断仅是预览行为）。
2. `Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs`：仅改类注释（`10-row budget` → `10-change-group budget`，并补一句说明截断标记与「不影响写入」的边界）。无行为改动。

## 验证（先红后绿 + 双向变异）

| 阶段 | 证据文件 | 结果 |
|---|---|---|
| 新增测试后 | `temp/hb13-d9-green.txt` | 失败 **0**，通过 **44**，总计 44（D1–D8 原为 42） |
| 变异 A：`MaxChangeGroups = 100`（等价不截断） | `temp/hb13-mut-a-summary.txt` | **取红**：`Preview_ElevenChangeGroups_IsTruncatedAfterTenWithMarker` 失败（1 失败 / 11 通过） |
| 变异 B：`MaxChangeGroups = 5`（预算不足） | `temp/hb13-mut-b-summary.txt` | **取红**：`Preview_ExactlyTenChangeGroups_IsNotMarkedAsTruncated` 失败（1 失败 / 11 通过） |
| 复原（bit-identical） | `temp/hb13-restore.txt` | `restored_sha256=02e2bcfebb8851c52ec9b3df9033e1c8551541171208e8055a79cd871bb3753e`，`matches_pristine=True` |
| 复原后复跑 | `temp/hb13-restore-summary.txt` | 失败 **0**，通过 **44**，总计 44 |

两个变异方向都取红 ⇒ 两个测试分别守护「不得误报截断」与「超限必须截断并标记」，均非空转。变异与复原均由 `temp/hb13-mutate.py` 脚本化执行，复原走备份字节写回并校验 sha256。

## 边界与遗留

- 只锁定 `MaxChangeGroups`；`MaxLookahead = 200`（游标对齐时向前搜索共同行的窗口）仍无测试——超出窗口会把「移动的行」降级为「删除+插入」，属启发式行为，待后续按需补测。
- 预览截断是纯展示语义：写盘结果不受预算影响（已由第 11 处改动落盘断言覆盖）。
- 宿主仍运行 2026-10-06 22:57 构建的程序集，本次与既有的 file_patch 修复一样，需部署后才在运行中的 Agent 上生效。
