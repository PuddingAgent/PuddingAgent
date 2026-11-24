---
title: 2026-11-25 goal_update 静默写坏 goal.md 的根因与修复验证
author: hyfree
date: 2026-11-25
last_reviewed: 2026-11-25
status: active
description: "记录一次真实的 goal.md 数据损坏：8180 字节、带 BOM、1747 处 U+FFFD 并混入二进制垃圾，而工具回报成功。根因定位到 GoalUpdateTool 四条「把损坏当成成功」的通道（宽松 UTF-8 解码 / 不校验既有 U+FFFD / Encoding.UTF8 写 BOM / 内部 catch 返回 error 但外层仍 Success）。修复后部署到 PID 91516，并用合法 base64 + 非法 UTF-8 字节做负对照、append 做正对照、按备份逐字节复原，三态全部取证。"
categories: [platform, reports]
tags: [goal-md, utf8, data-corruption, tool-contract, fail-closed, verification]
related_docs: [Docs/10_conventions/code-map-规范-v2.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Agents/GoalUpdateTool.cs, Source/PuddingRuntimeTests/Tools/GoalUpdateToolTests.cs]
slug: reports-2026-11-25-goal-update-corruption-fix
draft: false
---

# goal_update 静默写坏 goal.md

## 1. 症状

2026-11-25 01:30 读取自身 `goal.md`：

| 指标 | 实测 |
|---|---|
| 字节数 | 8180（同日前一完好备份 5217） |
| BOM | 有 |
| U+FFFD | 1747 处 |
| 可读边界 | 自第 485 字符起混入二进制垃圾，后半段不可读 |

而写入方 `goal_update` 从未报错——它一直返回 `status: ok`。

坏件未删除，存于 agent 私有 `archives/goal-md/goal-20261125-0130-CORRUPT.md`；恢复源为同一目录的 `goal-2026-11-24-2152.md`（5217 B / 0 FFFD）。

## 2. 根因：四条「把损坏当成成功」的通道

读 `Source/PuddingRuntime/Tools/BuiltIns/Agents/GoalUpdateTool.cs`：

| # | 通道 | 后果 |
|---|---|---|
| 1 | `Encoding.UTF8.GetString` 宽松解码 | base64 解出的非法 UTF-8 字节被静默替换成 U+FFFD 后照写 |
| 2 | 内容本身已含 U+FFFD 也照写 | 坏数据可以被再次喂回并放大 |
| 3 | `File.WriteAllText(..., Encoding.UTF8)` | 每次写入都追加 UTF-8 BOM |
| 4 | 内部 `catch` 返回 `{"status":"error"}`，外层仍 `Success=true` | **写失败也回「成功」** |

第 4 条最致命：它让前三条的后果永远无法从工具回执里被发现。前三条中任意一条单独存在，都会被第 4 条掩盖。

## 3. 修复（`e8dfd12`）

- 严格解码：`new UTF8Encoding(false, throwOnInvalidBytes: true)`
- 写前统计并拒绝含 U+FFFD 的内容
- 统一 `NoBomUtf8` 编码
- 失败路径重新抛出，不再返回「假成功」
- **写后按字节读回自检**，通过才 `Move` 落位

测试 8/8。三处变异各取 1 红 / 7 绿，恢复后 SHA `d6218f04` 与备份逐字节相同。

一处方法性教训：首次「写 BOM」变异**没有让测试失败**——因为改的是 `Encoding.UTF8.GetBytes`，而 `GetBytes` 本身不加 BOM（加 BOM 的是 `WriteAllText` / `StreamWriter`）。这是等价变异。换成显式拼 `EF BB BF` 前缀后才取红。**变异本身也要能取红**，否则它只是个假门禁。

## 4. 部署后的三态取证

部署核对（三者均晚于提交 `e8dfd12` @ 01:34:34）：

```
PID 91516  StartTime 01:37:30
live PuddingAgent.exe  01:36:49
live PuddingRuntime.dll 01:36:46
```

| 态 | 输入 | 结果 |
|---|---|---|
| 负对照 | `content_base64=Qf9C`（合法 base64，解出 `0x41 0xFF 0x42`，非法 UTF-8） | 工具 **exit=1**，回执明确「解出的字节流不是有效 UTF-8…已拒绝写入」；`goal.md` SHA `2B7F7D5E…` 前后相同、2625 B、0 FFFD |
| 正对照 | `append=SELFTEST-…` | 成功，2625→2679 B，无 BOM / 无 CR / 0 FFFD，标记命中 |
| 复原 | 按事先字节备份回写 | 2625 B，SHA 与备份相同 |

正对照不可省：一个「拒绝一切写入」的实现同样能通过负对照。

## 5. 遗留

- 修复只覆盖 `goal_update` 一个写入方。其他写 agent 私有文档的路径（`agent_state` 等）是否也走宽松解码，未审。
- 「工具内部 catch 后外层仍报成功」这类模式是否在其它工具里复现，未普查。
