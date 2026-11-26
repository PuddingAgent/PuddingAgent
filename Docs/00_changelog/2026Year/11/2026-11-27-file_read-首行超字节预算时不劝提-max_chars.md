---
title: 2026-11-27 file_read 首行超字节预算时不劝「Raise max_chars」
author: 鲸鲸
date: 2026-11-27
last_reviewed: 2026-11-27
status: active
description: 首行本身就超过 inline 字节预算时，MaxChars 说明仍劝「Raise max_chars to at least the length of one line」——而调用方已把预算给到等于行长，任何 max_chars 都取不到那一行；改为陈述「该行用本工具任何参数都读不全」。
categories: [tools, runtime]
tags: [file_read, receipt-contract, mutation-test]
related_docs: [Docs/00_changelog/2026Year/11/2026-11-27-file_read-续读位置只按实际交付算.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FileTools.cs, Source/PuddingRuntimeTests/Tools/FileReadToolTests.cs]
slug: changelog-2026-11-27-fileread-first-line-over-byte-budget
draft: false
---

# 2026-11-27 file_read 首行超字节预算时不劝「Raise max_chars」

## 现象（运行时取证）

文件：118605 字节 / 100 行，首行 20000 字节，其余 99 行各 999 字节。

| 入参 | 回执末尾 |
|---|---|
| `max_chars=100000`（大于行长） | `... but no complete line fits in that budget ... Raise max_chars to at least the length of one line.` |
| `max_chars=20000`（**等于**行长） | 同一句，一字不差 |

第二行是矛盾的：`max_chars` 已经等于行长，再「提高」也取不到那一行——真正绑定的是 7680 字节的 inline 预算，而它不受 `max_chars` 影响。同一份回执里上一行已经说了「not addressable」，两句指向不同结论。

## 根因

`DescribeMaxCharsTruncation` 的 `consumedLines == 0` 分支不分情况一律返回「调大预算」的建议，而它在判断上属于两种互斥情形：

| 情形 | 提高 `max_chars` 有效？ | 旧文案 |
|---|---|---|
| 首行长 ≤ 7680 字节，仅预算不够 | 有效 | 正确 |
| 首行长 > 7680 字节（字节闸门绑定） | **永远无效** | 误导 |

调用点当时把 `FirstLineTruncated ? 0 : EmittedLines` 作为「已交付行数」传入，把「为 0」本身的原因丢掉了——而原因正是建议真假的判据。

## 改法

`DescribeMaxCharsTruncation` 新增 `bool firstLineTruncated = false`，把 `consumedLines == 0` 拆两个分支：

- `firstLineTruncated` 为真：返回 `... the first line of this window alone exceeds the {InlineBudgetBytes}-byte inline budget: no max_chars value can deliver it, and no argument of this tool resumes inside a line.`
- 否则：保留原句（调大预算是有效的）

两个调用点（小文件 20 空格缩进、大文件 16 空格缩进）各自传入 `firstLineTruncated: boundedX.FirstLineTruncated`。第一次锚点匹配 `count=0` 就是因为按「刚改过那两行」推断缩进——脚本在写盘前断言失败，文件未被修改。

## 取红（三次变异，互不代偿）

| 变异 | 结果 | 红点 |
|---|---|---|
| M1 小文件调用点传 `false` | 2 例中 1 红 | `MaxChars_FirstLineExceedsInlineBudget_DoesNotAdviseRaisingMaxChars` |
| M2 大文件调用点传 `false` | 2 例中 1 红 | `MaxChars_OnLargeFilePath_FirstLineExceedsInlineBudget_...` |
| M3 新分支整体禁用 | 39 例中 **2 红 / 37 绿** | 上述两条 |

M1 与 M2 的红点互为镜像：只修一个调用点时另一条保持绿——这正是两个独立防御的判据。M3 同时证明：旧用例 `MaxChars_ShorterThanFirstLine_DoesNotAdviseNonAdvancingOffset`（6 行文件、`max_chars=5`）在分支被删后仍绿，说明新分支没有偷走它的职责。

每轮变异前从修复后的基线复制；恢复后 `SHA AF061F90FE82E91C…` 逐字节相同，`dotnet build -t:Rebuild` 后 39/39。

## 局限

- 两条新用例只断言「不再劝提预算」与「给出新结论」，不断言原因分支；不同情形的分派全靠 M1/M2 取红守住。
- 运行时取证只用 ASCII 构造样本（CJK 下字节 ≠ 字符会让绑定更早发生，逻辑相同但未取样）。
- 新消息不告知「怎么取回那一行」，因为当前工具确实没有任何参数能做到；写一个假方案比不写更糟。

## 关系

延续「续读位置只能描述已经交付的内容」这条不变量。上一轮解决的是「位置指错」，本轮解决的是「建议本身不可行」——同一不变量对「给出的下一步」也成立：**建议只能指向真的可达状态**。
