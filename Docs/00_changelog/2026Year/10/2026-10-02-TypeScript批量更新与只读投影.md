---
title: 2026-10-02 TypeScript 批量更新与只读投影
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: TypeScriptIndexer 实现批量接缝：一批只跑一次项目级提取进程、只读投影、payload 交调用方原子提交；SessionKey 在退化为逐文件时置空。
categories: [docs, changelog]
tags: [code-index, typescript, batch]
related_docs: [Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md, Docs/12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md]
related_files: [Source/PuddingCodeIntelligence/TypeScript/TypeScriptIndexer.cs, Source/PuddingCodeIntelligenceTests/TypeScript/TypeScriptIndexerBatchTests.cs]
slug: changelog-typescript-batch-updater-2026-10-02
draft: false
---

# 2026-10-02 TypeScript 批量更新与只读投影

## 改动

- `Source/PuddingCodeIntelligence/TypeScript/TypeScriptIndexer.cs`：
  - 实现 `ICodeIndexFileBatchUpdater.UpdateFilesAsync`：**一个批次只跑一次项目级提取进程**
    （ts-morph 装载整个工程一次），逐文件产出 `CodeFileIndexPayload`，**不写任何索引**；
    原子提交由调用方经 `ReplaceFilesAsync` 完成。
  - 路由：非 TS/JS 扩展名、噪声路径 ⇒ `NotApplicable`；工程根缺失 ⇒ 全部 `Retryable`（**不启动提取器**）；
    Node 不可用 / 提取器资产缺失 ⇒ 全部 `Retryable`（带资产路径原因，按退避重试，不升级整仓）；
    项目模式没覆盖到的请求路径 ⇒ **退化逐文件提取**，并把这批的 `SessionKey` **置空**。
  - `SessionKey` 只在真的用了项目模式时才有值：它如实区分「批次复用了一次工程快照」与
    「退化成逐文件提取」，供调用方与验收判断。
  - **投影改为只读**：`ProcessProjectExtraction` 里原先内嵌的 `ClearSymbolsForFileAsync`
    （`.GetAwaiter().GetResult()`）移到调用方新增的 `ClearProjectModeFilesAsync`。
    语义等价（清理仍发生在写入之前、每次只作用于该文件自己），使批量接缝能复用同一投影而不写库。
  - 全量 `IndexWorkspaceAsync` 与逐文件 `IndexFileAsync` 的行为未改动。
- 新增 `Source/PuddingCodeIntelligenceTests/TypeScript/TypeScriptIndexerBatchTests.cs`（7 条）：
  用**真实 Node 子进程 + 可控桩提取器**（每次 `--project` 调用追加一行标记）断言批内只跑一次项目级提取。
- `Source/PuddingCodeIntelligence/code_map.md`：索引器表登记 TS 批量能力与只读投影，消费侧登记
  `TypeScript/TypeScriptIndexer.cs` 条目。

## 验证

- `dotnet test Source\PuddingCodeIntelligenceTests\PuddingCodeIntelligenceTests.csproj` → **124 通过 0 失败**
  （本次 +7）；`Source\PuddingCodeIndexTests` → **280 通过 0 失败**（未受影响）。
- 关键用例：**两个文件的项目级提取调用次数 = 1**（桩标记计数）；批后 `ListFilesAsync` 与
  `GetSymbolsByFileAsync` 均为空（不写索引）；非 TS/JS ⇒ `NotApplicable` 且提取器一次都不跑；
  工程根缺失 ⇒ `Retryable` 且提取器一次都不跑；资产缺失 ⇒ `Retryable` 且原因含脚本路径；
  **项目模式没覆盖的路径**退化逐文件提取（标记里出现 `file:ghost.ts`）且 `SessionKey` 为空；
  重复/大小写变体/空白路径去重后只跑一次；空批不跑任何东西。
- 既有 `TypeScriptIndexerAssetTests`（真实 Node 提取、NODE_PATH 隔离、资产缺失 fail-closed）
  **未修改且全部通过** —— 这是「投影只读化」没有改变全量行为的外部证据。
- 组件边界不变（`PuddingCodeIntelligence` → `PuddingCodeIndex` 单向引用未变）。

## 未完成与影响

- **Python 侧批量实现未做**（下一轮）：`PythonIndexer` 仍逐文件启动 python 提取器；
  它与 TS 结构相同（同样有 `RunProjectExtractionAsync` + `ProcessProjectExtraction`，且投影里同样内嵌
  `ClearSymbolsForFileAsync`），可沿用本轮的「只读投影 + 一次项目级提取 + SessionKey 说实话」模式。
- **仍未接入维护链路**：批量接缝没有调用方，`CodeIndexMaintenanceService` 仍走逐文件旧接缝；
  运行中实例仍是零行为变化。
- 未实现跨批次快照缓存（同上一轮说明：需要明确生命周期所有者、失效条件与内存上限，属独立任务）。

## 关联

- 诊断报告：`Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md` §D4（2026-10-02 修订版）。
- 前序切片：`2026-10-02-语言批量接缝与能力路由.md`、`2026-10-02-Roslyn批量更新接缝实现.md`。
