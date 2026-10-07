---
title: file_patch 归因复核的独立验证（2026-10-07）
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: active
description: "对 3de3844 交付的独立复核：提交面、时点源码行号、字节数字与三条机制归因全部复现；同时发现该复核指出的三条机制已在同日下午的五个修复提交中解决并有 D4/D5/D9 回归测试守护，而运行实例仍是 10-06 构建，缺陷在生产仍活跃。"
categories: [docs, reports]
tags: [file_patch, attribution, independent-verification, deployment-gap]
related_docs: [Docs/14_reports/file-patch归因复核-2026-10-07.md, Docs/12_features/file-patch文本边界修复方案-2026-10-07.md, Docs/14_reports/工具调用归因分析-2026-10-07.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs, Source/PuddingRuntimeTests/Tools/FilePatchToolTests.cs]
slug: reports-file-patch-attribution-independent-verification-2026-10-07
draft: false
---

# file_patch 归因复核的独立验证（2026-10-07）

## 复核范围与方法

对象是提交 `3de3844`（`docs: review file_patch attribution and specify text boundary fixes`）交付的两份文档与其自述。复核不采信自述，逐项重建证据：

- 提交面：`git show --name-status --stat 3de3844`、`git status --porcelain -- Source`。
- 时点源码：`git show 3de3844:Source/PuddingRuntime/Tools/BuiltIns/Files/FilePatchTool.cs` 导出后逐符号定位行号。
- 字节数字：独立 Python 复算（`temp/rev-probe.py`，输出 `temp/rev-probe-out.txt`），只依赖被测时点的字符串拼接契约，不读被测实现的注释。
- 行为：`dotnet test --filter FullyQualifiedName~FilePatch`（`temp/rev-filepatch-tests.txt`）。
- 运行身份：进程路径 / 启动时间 + 部署目录 `PuddingRuntime.dll` 的 mtime 与 SHA-256。

## 一、属实项

| 自述断言 | 独立证据 | 判定 |
|---|---|---|
| 提交只含文档，生产代码未修改 | `3de3844` 为 3×A + 1×M，全部落在 `Docs/`；`git status --porcelain -- Source` 仅列 `Source/PuddingHost/**` 外部 WIP | 属实 |
| 行号引用准确（423 / 468 / 519 / 849 / 1117） | `3de3844` 时点文件（sha256 `963ca191…`，1579 行）：`FindReplacementCandidates` 423、`ExpandLeadingWhitespace` 468、`FindLiteralMatches` 519、`GenerateSimpleDiff` 849 与 1117 | 逐条命中 |
| 精确匹配只替换 CRLF 中的 LF，留下孤立 CR | 时点 `FindLiteralMatches` 以 `original.IndexOf(oldText, Ordinal)` 逐字查找、不做 EOL 归一，且 exact 命中即返回（先于任何归一化策略） | 归因成立 |
| 容忍匹配删除完整换行 → 两行粘连 | 时点比对路径无边界校验（`eol-equivalent`、`IndexOfLine`、`WidenToWholeLineBreak` 在该时点均不存在） | 归因成立 |
| diff 按行序号比较、两侧 `TrimEnd('\r')` | 时点 `TrimEnd('\r')` 位于 857/858 与 1125/1126；两份 diff 生成实现按同一下标比较 | 归因成立 |
| 六组探针数字 | 独立复算四组精确路径全部吻合：T-exact-LF `87 B / 5 CRLF / 1 孤立 CR`、T-exact-CRLF `86 / 5 / 0`、C-no-leading `88 / 6 / 0`、C-leading-preserved `89 / 6 / 1`；基准 fixture 亦为 `74 B / 5 CRLF` | 数字可信 |
| 「自动补回换行」不足以修复且会改变用户意图 | 该判断被同日 `2f0d5d5`（`honour the requested newline instead of silently restoring it`）与 D4 测试注释独立采纳 | 判断成立 |
| 「未验证」清单诚实 | raw payload、历史运行程序集、原调用统计确未取；本报告同样无法从仓库取证 | 属实 |

## 二、时效性发现（复核的主要补充）

报告与方案写于 11:29:40；**同日 14:00–16:45 已有五个提交逐条实施了方案条目**，因此「本方案尚未实施」的状态在当前工作树已过期：

| 报告机制 | 实施提交 | 当前 HEAD（`2377211`）证据 | 测试守护 |
|---|---|---|---|
| ① 精确匹配拆开 CRLF → 孤立 CR | `2f0d5d5` 14:00:46 | `WidenToWholeLineBreak`（定义 L657，调用点 L409 对全部候选策略统一扩宽，起点落在 CR 与 LF 之间时左扩 1 覆盖整对 CRLF） | D4 `Replace_LeadingNewlineAnchoredOldText_NeverSplitsCrlfAndDeletesBreakWhenOmitted` |
| ② 容忍匹配隐式扩大边界 | `18bc233` 14:25:54、`a27112b` 14:52:40 | `DescribeAmbiguousBoundaryChange`（L616）对猜出来的跨度 fail-closed 拒绝，返回 `ambiguous_boundary_change` 且不写盘；并新增 `eol-equivalent` 匹配（L510/L630） | D5 `Replace_TolerantMatchWithAsymmetricLeadingBreak_RefusedWithoutWriting` |
| ③ diff 按序号比较 → 级联错位 | `ebd07df` 15:27:12 | `GenerateSimpleDiff` 已重构为 `SimpleLineDiff`（L1149）+ `BuildScript`（L1186）+ `IndexOfLine`（L1197）按行对齐，`MaxLookahead` 限制前瞻 | D9（`0982a89` 19:09:51 锁定 10 变更组截断语义） |
| 方案 P1「顺带单列」的 scope 坐标缺陷 | `9a6d7b9` 16:45:03 | scope 行范围按原文计偏移 | 已随提交验收 |

行为验证：`dotnet test --filter FullyQualifiedName~FilePatch` → 失败 0，通过 **55**，总计 55。

即：报告的三条机制**在其时点全部真实存在**，且三条都已被后续提交修掉；报告主张的「不能自动补回换行」「模糊跨度必须拒绝而非猜测」「diff 必须序列对齐」正是后续修复所采用的方案。该交付的价值在于绕开了「吞一个换行就补一个」的错误修复方向。

## 三、未证实与存疑项

| 项 | 状态 |
|---|---|
| 原事故 raw payload | 未取回，本次亦无法从会话与临时目录取证 ⇒ 未证实 |
| 六组方法级探针工程 `temp/build/filepatch-attribution` | 报告称「暂留已忽略目录」，但当前磁盘上未找到 ⇒ 该证据**不可复跑**（低危，可能其后被清理） |
| 原调用统计 / 子代理失败事件 | 未重取，报表明示不认证 |
| 混合 EOL、已有孤立 CR、BOM、无末尾换行等矩阵 | 方案列为回归矩阵行，未见逐项证据 |

## 四、运行身份（对「运行程序集仍待核实」的回答）

2026-10-07 22:47 现场实测：

```text
PROC=36064 start=2026-10-07T10:09:44+08:00 path=…\Source\PuddingAgent\bin\Debug\net10.0\PuddingAgent.exe
DLL_WRITE=2026-10-06T22:57:38+08:00 bytes=4560896
DLL_SHA256=C2695C403807867C5B4C8C0FA621708D44C4D4560303FD7A8E9A639EA8B09EB1
```

运行实例的程序集构建于 **10-06 22:57**，早于今日全部 file_patch 修复（14:00 起）与本次交付 ⇒ **三条机制在当前生产实例上仍然活跃**；源码修复必须部署才会生效。这一点与报告的「未部署」自述一致，也与用户「运行程序集仍待核实」的保留态度吻合：需核实的对象是**部署**，而非归因。

## 五、建议下一步

1. **部署**是唯一阻塞：目标提交 `2377211`，部署后按方案「两段式验收」用隔离 fixture 走真实工具入口、读回磁盘字节并核对换行与 diff。
2. 若原事故 raw payload 仍可获取，应在部署后的实例上复跑，闭环责任归因。
3. 方案 P2 各项（`tool_stats` 全量普查、skill 参数失败证据、运行身份诊断字段、写后验收 hash、报告脚本、历史损坏恢复）**仍全部未实施**。
4. 后续方法级探针若要长期复现，应放入受版本控制的位置，而不是依赖 `temp/`。

## 关联

[归因复核](file-patch归因复核-2026-10-07.md) · [修复方案](../12_features/file-patch文本边界修复方案-2026-10-07.md) · [原报告](工具调用归因分析-2026-10-07.md)
