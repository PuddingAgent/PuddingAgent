---
title: 2026-11-27 file_read 的 LimitLines 单独传入等于没传
author: 鲸鲸
date: 2026-11-27
last_reviewed: 2026-11-27
status: active
description: LimitLines 只在 OffsetLines 分支里被读取，单独传入时既不参与窗口计算也不出声；而 limit<=0 的回执还写着「body is empty」，正文却是整个文件——回执与正文互相矛盾。
categories: [tools, runtime]
tags: [file_read, receipt-contract, mutation-test]
related_docs: [Docs/00_changelog/2026Year/11/2026-11-27-file_read-头尾参数静默推翻分页参数.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/FileTools.cs, Source/PuddingRuntimeTests/Tools/FileReadToolTests.cs]
slug: changelog-2026-11-27-fileread-limitlines-alone-ignored
draft: false
---

# 2026-11-27 file_read 的 LimitLines 单独传入等于没传

## 现象

10 行文件（`L1`..`L10`），只给 `limit_lines`：

| 入参 | 正文 | 提示 |
|---|---|---|
| `limit_lines=2` | `L1`..`L10`（全部 10 行） | 无 |
| `limit_lines=0` | `L1`..`L10`（全部 10 行） | `LimitLines=0 requests no line (must be >= 1), so the body is empty.` |
| `offset_lines=5`、`limit_lines=3` | `L6 L7 L8` | 无（正确） |

第二行是最糟的形态：回执**断言正文为空**，而正文把整个文件都给了。调用方无法判断该信哪一半。

## 根因

窗口选择是三个 `else if` 分支，`LimitLines` 只在第三支里被用到：

```csharp
else if (args.OffsetLines.HasValue)
{
    var limit = args.LimitLines ?? (lines.Length - offset);   // 只有这里读 LimitLines
    content = string.Join("\n", lines.Skip(offset).Take(Math.Max(0, limit)));
}
// 无 head / tail / offset 时：不进任何分支，content 保持「整个文件」
```

于是「给了 limit 但没给 offset」既不选窗口、也不出声。而 `DescribePagingAnomaly` 里的
`LimitLines<=0 → so the body is empty` 是按「limit 一定参与窗口」写的，在这种入参下就变成假话。

`hasExplicitSlice`（小文件）与 `hasExplicitSliceLarge`（大文件）同样只列了 head/tail/offset/maxchars，
所以大文件上单独给 limit 会先撞护栏，用户给的预算被路过的预览推翻。

## 改法

`LimitLines` 提升为与 head/tail/offset 并列的窗口选择器，两个调用点（小文件切片、大文件 chunk 读）同一条语义：

- 小文件：`else if (args.LimitLines.HasValue) { windowStart = 0; content = Take(max(0, limit)); }`
- 大文件：`else if (args.LimitLines.HasValue) { windowContent = ReadChunkAsync(path, 0, max(0, limit)); }`
- 两个「显式切片」判据都补上 `LimitLines.HasValue`，护栏不再抢先

这样 `limit<=0` 时正文确实为空，那句既有提示变成真话——**修行为让提示变真，而不是改提示去迁就行为**。

## 取红

| 变异 | 结果 | 红点 |
|---|---|---|
| M1 小文件分支体失效（`content = content`） | 7 例 3 红 | `LimitLines_Alone_ReadsFirstLines`、`NonPositiveLimitLines_Alone_EmptyBodyMatchesNotice`、`LimitLines_Alone_BypassesGuardrail` |
| M2 两个显式切片判据去掉 `LimitLines` | 7 例 1 红 | `LimitLines_Alone_BypassesGuardrail` |
| M3 大文件分支改回读全文件 | 7 例 1 红 | `LimitLines_Alone_LargeFileReadsFirstLines` |
| 复原 | — | `FileReadToolTests` **50/50** |

M2 的红集严格包含于 M1，但 M2 的独有红点（护栏被推翻）是 M1 之外的另一处代码；
M3 的红点与两者不相交，证明大文件路径此前**没有任何用例**守护这一点。
正向对照 `LimitLines_Alone_HasNoPagingNotice` 三次变异全绿——没有它，一个恒发提示的实现也能让前几条过。

每次变异前从基线备份恢复，最终 `SHA256 ACC548328252613A6B5EC2A86BAD3E7354D1FBD6D1A5C0212069B44647382AD2`
与备份逐字节相同、`MUTATION M` 残留计数 0，`-t:Rebuild` 后全类 50/50。

## 局限

- 三种变异都打在 `FileTools.cs` 同两个方法上，M2 与 M3 的红点各自唯一，但没有独立的第三方实现交叉验证。
- 无生产流量样本：本仓库的调用方从未单独传 `limit_lines`，本次是构造样本 + 现场运行时探测。
- CJK 未取样（本轮探针全 ASCII）；该改动只影响窗口行数，不涉及字节/字符口径。

## 门禁

- `front_matter` / `check_md_links` / `code_map_check` 全绿（详见本轮提交回执）
- `FileTools.cs` 55992 → 57482 字节，CRLF=1075、BARE_LF=0、无 BOM
- `FileReadToolTests.cs` 52649 → 57211 字节，保持 LF（CRLF=0、BARE_LF=1211），无 BOM
- 新增 5 条用例：`LimitLines_Alone_ReadsFirstLines` / `_HasNoPagingNotice` / `_BypassesGuardrail` /
  `_LargeFileReadsFirstLines`、`NonPositiveLimitLines_Alone_EmptyBodyMatchesNotice`

## 运行时验收（2026-11-27 07:14 · 部署 `6fb2f17`）

live 进程 PID 47944 @ 07:12:42；`PuddingRuntime.dll` 07:11:57、`PuddingAgent.dll` 07:12:01，
均晚于提交 07:10:27（路径取自 `Get-Process.Modules`）。

探针：`temp/probe_limit_1127.txt`（10 行 / 41 字节）、`temp/probe_big_1127.txt`（400 行 / 5492 字节，
命中 `totalLines > 300` 的大文件路径）。六条期望值先由 `DescribePagingAnomaly` 与两条读取分支算出，再实调比对：

| # | 入参 | 期望（读码得出） | 实测 |
|---|---|---|---|
| P1 | `limit_lines=2` | 仅 L1..L2，无 `[PAGING:` | 一致 |
| P2 | `limit_lines=0` | 空正文 + `LimitLines=0 requests no line` | 一致 |
| P3 | `head_lines=2` | L1..L2，无 `[PAGING:`（正向对照） | 一致 |
| P4 | `offset_lines=8` + `tail_lines=3` | L8..L10 + `OffsetLines=8 was ignored … TailLines=3` | 一致 |
| P5 | `head_lines=5` + `limit_lines=2` | L1..L5 + `LimitLines=2 was ignored … HeadLines=5` | 一致 |
| P6 | `head_lines=2` + `limit_lines=2` + `tail_lines=3` | L1 L2 + 两条 ignored（Tail 让位 Head、Limit 让位 Head） | 一致 |
| P7 | 400 行文件 `limit_lines=3` | BIG-LINE-1..3，**无** `[GUARDRAIL` | 一致 |
| P8 | 同一文件不加分页参数 | 出 `[GUARDRAIL: 400 lines` 预览 | 一致 |

P7 与 P8 是一对：P8 证明该文件确实触发护栏（`400 > 300`），P7 证明显式切片绕过了它并只读 3 行。
这一对才是「大文件分支新增 `LimitLines` 判据」的行为证据 —— 单看 P7 无法排除「文件根本没触发护栏」。
P2 同时证伪了旧实现那句自相矛盾的提示：旧行为返回整个文件却写着 body is empty。

## 验收中新发现（未修，另立任务）

`LimitLines` 的参数说明仍是「Maximum lines to read. Use with OffsetLines for arbitrary window.」
（`FileTools.cs:711`），`OffsetLines` 的说明也写着「Use with LimitLines」。行为已改成
「`limit_lines` 单独传入 = 从首行起前 N 行」，契约却仍暗示两者必须成对 —— 模型读 schema 时会因此不单独用它，
修复在契约层不可见。另：该 `[ToolParam]` 行缩进为 8 空格（同块其余为 4），属同一处的笔误。