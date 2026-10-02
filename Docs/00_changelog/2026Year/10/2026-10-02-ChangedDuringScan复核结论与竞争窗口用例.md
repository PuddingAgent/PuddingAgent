---
title: 2026-10-02 ChangedDuringScan 复核结论与竞争窗口用例
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 复核后不为 ChangedDuringScan 增加无观测效果的队列管线，改为把真实竞争窗口（提取期间被删/被重建）固化成用例，并在合同上写明三条安全网与将来接线的条件。
categories: [docs, changelog]
tags: [code-index, race, hardening]
related_docs: [Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md]
related_files: [Source/PuddingCodeIndex/Contracts/CodeSourceManifestContracts.cs, Source/PuddingCodeIndexTests/Services/CodeIndex/CodeSourceMaintenanceReviewFixesTests.cs, Source/PuddingCodeIndexTests/Services/CodeIndexFixture.cs]
slug: changelog-changed-during-scan-conclusion-2026-10-02
draft: false
---

# 2026-10-02 ChangedDuringScan 复核结论与竞争窗口用例

## 结论（先说不做什么，以及为什么）

`CodeSourceScanOptions.ChangedDuringScan` 在生产路径上没有来源。本笔**没有**为它接一条队列管线，
理由是复核后证明那条管线在可观测窗口里恒为空、且真实风险已由更强的规则覆盖：

- **恒为空**：watcher 事件先被合并器折叠进批次（同一路径只保留最新序列），
  而批次一旦产出、驱动就立刻施用，因此「批次捕获之后、协调器开始之前」被发布的事件窗口极小；
  真正长的窗口是**施用期间**（读盘/提取），而此时的事实对已经算好的变更集没有意义。
- **覆盖它的三条规则**（各有独立用例）：
  ① 稳定读发现 stat 前后不一致 ⇒ 弃用本轮；
  ② **提交前复核 stat**（提取可能耗时）⇒ 不一致就不提交；
  ③ 删除只能由「完整 + 根可用」的扫描得出，观察到的消失只作提示。
- **将来接线的条件**：若改成「一次很长的全枚举」，应把合并器里尚未折叠的路径作为该事实注入；
  这一点已写进 `CodeSourceManifestContracts` 的常量文档，避免下一个人重复调研。

## 改动

- `Source/PuddingCodeIndex/Contracts/CodeSourceManifestContracts.cs`：`ScanIncomplete` 相关常量旁补上
  上述现状、覆盖规则与接线条件（就地说明，不新增机制）。
- `Source/PuddingCodeIndexTests/Services/CodeIndex/CodeSourceMaintenanceReviewFixesTests.cs` 新增 2 条
  **竞争窗口**用例（把「安全网」变成可证伪的断言）：
  1. **提取期间文件被删**（内容先变掉以保证这一轮确实会去提取）：这一轮不写索引、不写指纹，
     **旧的 manifest 记录与指纹原样保留**，该路径记进持久待办，水位不前进；
  2. **确认删除后文件被重建**：下一轮必须把它当作新文件重新索引回来（指纹与符号都回来），
     即「删了又建」不会静默丢失。
- `Source/PuddingCodeIndexTests/Services/CodeIndexFixture.cs`（独立提交 `a0f3125`）：夹具清理改为重试，
  消除并发下 SQLite 句柄释放竞争导致的偶发 teardown 失败。

## 验证

- `dotnet test Source\PuddingCodeIndexTests` → **334 通过 0 失败**（本次 +2；且此前那条偶发失败已由
  夹具重试消除）。
- `dotnet test Source\PuddingCodeIntelligenceTests` → **136 通过 0 失败**。
- 用例要点：用例 1 断言 `RetryableFileCount == 1`、`Manifest[file]` 仍存在且 hash 未变、
  `PendingRetries` 含该路径、`ScanWatermarkAdvanced == false`；用例 2 断言下一轮
  `ExtractedFileCount == 1`、指纹重新落地、符号回来。

## 未完成与影响

- 仍待**进程外控制器重启 + 四场景受控复核**（唯一剩下的验收环节）。
- 其余已登记小刀：C# 真实 MSBuild 工作区端到端；跨批次工程快照缓存。两者都不影响四项修复的交付。

## 关联

- 诊断报告：`Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md` §D（2026-10-02 修订版）。
- 前序切片：`2026-10-02-启用后链路组件级端到端门禁.md`、`2026-10-02-独立复核发现的处置.md`。
