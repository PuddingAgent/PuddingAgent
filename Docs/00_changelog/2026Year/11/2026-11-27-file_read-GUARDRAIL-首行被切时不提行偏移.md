---
title: 2026-11-27 file_read GUARDRAIL 首行被切时不提行偏移
author: 鲸鲸
date: 2026-11-27
last_reviewed: 2026-11-27
status: active
description: 上一次修复（7b37f55）声称覆盖「单行超字节闸门」与「首行被切」两形态，实调发现两者实际都走 GUARDRAIL 分支，而两处 GUARDRAIL 调用点直接把 firstLineTruncated 丢成 out _，回执仍让人从第 2 行续读。
categories: [tools, runtime]
tags: [file_read, receipt-contract, mutation-test, guardrail]
related_docs: [Docs/00_changelog/2026Year/11/2026-11-27-file_read-续读位置只按实际交付算.md, Docs/14_reports/2026-11-27-file_read-双重预算续读位置修复.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FileTools.cs, Source/PuddingRuntimeTests/Tools/FileReadToolTests.cs]
slug: changelog-2026-11-27-fileread-guardrail-firstline
draft: false
---

# 2026-11-27 file_read GUARDRAIL 首行被切时不提行偏移

`7b37f55` 修了三处「续读位置与实际交付脱节」，其中两条（单行超闸门、首行被切）当时只做了源码判读与单测。本轮补齐运行时验收，结果与预期不符。

## 实测：两条形态都没修到

| # | 入参 | 期望 | 实测 |
|---|------|------|------|
| P1 | 40 行文件 `max_chars=10` | `no complete line fits in that budget` | ✅ 符合 |
| P2 | 20002 字节单行文件，无参数 | `[TRUNCATED: line 1 alone exceeds` | ❌ 走 GUARDRAIL：`Use OffsetLines=1+LimitLines to continue from line 2` |
| P3 | 20000 字节首行 + 后续 19 行，无参数 | `not addressable` | ❌ 同一句 `continue from line 2` |

P2 的文件只有 1 行，`continue from line 2` 指向一个不存在的行；P3 的第 1 行余下 12320 字节无法用任何行偏移取回。

## 根因

`FileTools.cs` 有两条读取路径（小文件整篇读 / 大文件 chunk 读），各自有一处 GUARDRAIL 分支：

```csharp
var preview = TakeLinesWithinByteBudget(lines, GuardrailPreviewLines, out var taken, out _);
//                                                                                       ^^^^
return ToolExecutionResult.Ok($"... Use OffsetLines={taken}+LimitLines ...");
```

`TakeLinesWithinByteBudget` 在上一轮已经能给出 `firstLineTruncated`，但两处 GUARDRAIL 都把它丢成 `out _`，再无条件写行偏移。`taken` 在首行被切成半行时已把该行算进「已交付」，所以偏移量看起来自洽。

单行超预算且文件 > 8 KiB 时 `hasExplicitSlice` 为假、`fileInfo.Length > 8_192` 为真，直接落进 GUARDRAIL —— 这正是 P2/P3 的路径。

## 修法

两处调用点接收 `firstLineTruncated`，并共用一个尾串生成器：

```csharp
private static string DescribeGuardrailSuffix(int takenLines, bool firstLineTruncated)
{
    if (firstLineTruncated)
        return $"Only the first {InlineBudgetBytes} bytes of line 1 were delivered; the rest of that line is not addressable — no argument of this tool resumes inside a line. Use TailLines for the end.";

    return $"Use OffsetLines={takenLines}+LimitLines to continue from line {takenLines + 1}, or TailLines for the end.";
}
```

共用一份判据而不是逐处打补丁：两处 GUARDRAIL 的文案此前已经同形同义地重复过一次，再各写一遍就一定会再次分叉。

## 取红

| 变异 | 结果 |
|------|------|
| M1 小文件路径退回 `out _` + 无条件偏移 | 37 例 **2 红**：`Guardrail_SingleLineLongerThanInlineBudget_DoesNotAdviseLineOffset`、`Guardrail_LongFirstLineWithFollowingLines_DoesNotAdviseLineOffset` |
| M2 大文件路径退回 | 37 例 **1 红**：`Guardrail_LargeFileSingleLineLongerThanInlineBudget_DoesNotAdviseLineOffset` |

两个变异的红点集合**不相交**：M1 只打红走小文件路径的两条，M2 只打红 121000 字节走 chunk 路径的一条。这证明两处调用点各自被独立守护，而不是一处改动同时满足全部断言。

M1 打红两条是如实记录的局限——这两条共用同一处调用点，不是独立双保险。

正向对照 `Guardrail_FittingFirstLine_StillAdvisesLineOffset`（500 行 × 短行，首行不超预算）在两次变异下都保持绿：没有它，一个「一律不给偏移」的实现也能让上面三条全过。

每轮变异前从备份恢复（`SHA 78B3296DD2804FEC…`），复原后逐字节相同，`-t:Rebuild` 后 37/37。

## 未验证

- P2/P3 是构造样本，没有生产流量里出现过同类文件的证据。
- 「一行到底能有多长」没有定论；本轮只保证回执不说假话，不改交付量。

## 关系

延续 `2026-11-27-file_read-续读位置只按实际交付算` 的同一条不变量：**续读位置只能描述已经交付的内容**。上一轮把它实施在 MaxChars 与 BoundInlineBody 两条路径上，本轮补上 GUARDRAIL 两条路径——同一不变量的第四个、第五个落点。
