---
title: 2026-10-02 源维护可观测性与 reconcile 一致化
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 协调器每轮结果写入 scope 状态（供外部四场景复核读事实）；Coordinator 模式下 reconcile 批次改走协调器完整扫描，不再升级为不更新 manifest 的旧 scope 级运行；并修掉新测试的墙钟耦合。
categories: [docs, changelog]
tags: [code-index, observability, reconcile]
related_docs: [Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md]
related_files: [Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs, Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs, Source/PuddingCodeIndexTests/Services/CodeIndex/CodeSourceMaintenanceDriverSwitchTests.cs]
slug: changelog-code-source-observability-reconcile-2026-10-02
draft: false
---

# 2026-10-02 源维护可观测性与 reconcile 一致化

## 改动

- **可观测性**（`ICodeIndexMaintenance.cs` + `CodeIndexMaintenanceService.cs`）：
  `CodeIndexMaintenanceScopeStatus` 新增 D4 字段 —— `SourceMaintenanceMode`、
  `SourceMaintenanceRunCount`、`SourceMaintenanceExtractedFileCount`、`SourceMaintenanceReusedFileCount`、
  `SourceMaintenanceOrphanFileCount`、`SourceMaintenanceUnresolvedPathCount`、
  `SourceMaintenanceDeletedFileCount`、`LastSourceMaintenanceCommitOutcome`、
  `LastSourceMaintenanceSessionKey`。每轮协调器结果写进 scope 状态，
  **外部四场景复核可以直接读事实**（走了哪条链路、提取/复用了多少、清了多少孤儿、水位是否前进、
  语言侧是否复用了同一个工程快照），而不是只能翻日志。
- **reconcile 一致化**：`ReconcileRequired` 的批次（队列溢出等）意味着细粒度捕获已不可信，必须重新对齐。
  `Coordinator` 模式下现在由协调器的**完整扫描**承担（`Targeted:false`、不传提示、不预先置升级位）；
  旧的 scope 级运行仍保留给 `Legacy` 模式。这样处理的原因：旧路径直接写索引却**不更新 manifest**，
  两条链路会对同一路径给出不同结论；协调器的完整扫描把索引、manifest 与账本一起对齐。
- **测试墙钟耦合修复**（真实缺陷）：新增的 I/O 画像/复核/开关用例此前用固定的假时钟
  （`2026-10-02 09:00`）。一旦**真实时间越过该时刻**，新建文件的 mtime 就落进 racy 重叠窗口，
  检测器于是把「未变」文件也当成需要核验的候选 —— 实测在会话中途从 330/330 变成 3 条失败。
  现在这些用例的时钟一律取 `DateTimeOffset.UtcNow.AddHours(1)`（永远走在真实文件时间之后），
  断言与墙钟解耦、可复现。

## 验证

- `dotnet test Source\PuddingCodeIndexTests` → **332 通过 0 失败**（本次 +2）。
- `dotnet test Source\PuddingCodeIntelligenceTests` → **132 通过 0 失败**；
  `Tests\PuddingHost.Tests --filter CodeIndexMaintenanceHostCompositionTests` → **4 通过 0 失败**。
- 新增用例：
  1. **reconcile 批次走完整枚举**（`FullScanCount ≥ 1`、`ProbeCount == 0`）、
     **`ScopeEscalationCount == 0`**（不再升级成旧的 scope 级运行）、索引仍被写入；
  2. **状态字段可读**：跑一轮后 `SourceMaintenanceRunCount == 1`、提取计数 1、会话键可读；
     再跑一轮同提示（内容未变）⇒ 运行数 2、**提取计数仍为 1**、**复用计数为 1**。
- 另：过程中出现一次与本笔无关的既有用例瞬时文件锁失败
  （`CodeIndexMaintenanceServiceTests.Stop_Returns_Within_The_Timeout...`，SQLite 文件被占用），
  重跑即通过，判定为并发下的清理竞争，非本笔引入。

## 未完成与影响

- 默认 `Legacy` 已由产品宿主覆盖为 `Coordinator`（上一笔），因此本笔的观测字段在真实运行中可读；
  仍待**进程外控制器重启 + 四场景受控复核**。
- 已登记的剩余小刀（未做）：`ChangedDuringScan` 的生产来源（watcher 在合并批次时记录二次观测）；
  真实提取器（node/python）端到端批量接缝测试；跨批次工程快照缓存。

## 关联

- 诊断报告：`Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md` §D（2026-10-02 修订版）。
- 前序切片：`2026-10-02-接入产品宿主启用协调器.md`、`2026-10-02-索引孤儿行清理.md`。
