---
title: "2026-10-03 · `code_symbol_search` 增加索引新鲜度（index_freshness）诚实信号"
author: hyfree
date: 2026-10-03
last_reviewed: 2026-10-03
status: archived
description: 本会话反复踩到同一个坑：索引重建期间的「0 命中」被误读成「代码不存在」。
categories: [docs, changelog]
tags: [code, symbol, 索引新鲜度信]
related_docs: []
related_files: [Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs, Source/PuddingRuntimeTests/Tools/CodeSymbolSearchIndexFreshnessTests.cs, Docs/12_features/Index-Retrieval-Known-Defects-2026-10-01.md, Docs/13_runbooks/Core部署决策包与验收清单-2026-10-03.md]
slug: changelog-2026-10-03-code-symbol-search索引新鲜度信号
draft: false
---

# 2026-10-03 · `code_symbol_search` 增加索引新鲜度（index_freshness）诚实信号

## 背景 / 动机

本会话反复踩到同一个坑：**索引重建期间的「0 命中」被误读成「代码不存在」**。

- 根项目 `Registering` 期间，查询会**静默返回空**；肉眼看着就像"覆盖被截断"。
- 我因此一度误判出 D2（陈旧 `E:` 路径）与 D5（覆盖被截断）两条"缺陷"，随后被实测推翻。
- 更早还有三处陈旧投影（"23 字段"）把连续两轮心跳导向已完成的工作。

结论：**「空结果」本身不含信息，必须附带「索引此刻是否可信」的事实**，否则调用方（含 Agent 自己）会持续把未知当结论。

## 改动（只增不改）

### 1. `Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs`（+127 / −2）

- `CodeSymbolSearchTool` 构造器追加**第 4 个可选参数** `ICodeIndexMaintenance? maintenance = null`（既有位置参数调用继续编译通过，**无新增 DI 注册** —— 容器内已注册）。
- 返回体**新增** `index_freshness` 字段（既有字段名/语义一字未动）：

  ```json
  "index_freshness": {
    "state": "unknown",
    "index_pending": null,
    "index_in_flight": null,
    "needs_reconcile": null,
    "reconcile_reason": null,
    "watcher_attached": null,
    "last_calibration_at_utc": null,
    "unresolved_source_path_count": null,
    "reason": "no scope attached to the maintenance driver"
  }
  ```

- 状态映射（单 scope，新私有方法 `MapScopeFreshnessState`）：

  | 记录事实 | state |
  |---|---|
  `IndexInFlight` | `rebuilding` |
  `IndexPending` | `pending` |
  `NeedsReconcile` | `needs-reconcile` |
  `WatcherAttached` | `idle` |
  以上皆非 | `unknown` + `reason` |

  跨 scope（未指定 `project_id`）取**保守聚合**：布尔位 OR（任一为真即为真），计数类字段留 `null`（跨 scope 求和会误导），`reconcile_reason` 取首个非空。
- **诚实优先**：`maintenance` 缺失 ⇒ `unknown` / `"maintenance service not registered"`；scope 未附着 ⇒ `unknown` / `"scope not attached to the maintenance driver"`（**不回落到全量聚合去凑状态**）。**绝不猜成 `idle`**。
- 文本层：仅当 `list.Count == 0` 且 state ∈ {rebuilding, pending, needs-reconcile} 时追加一行「空结果可能只是暂时的」；state == unknown 时追加「空结果不能证明符号不存在」；**有命中或 idle 时保持寂静**（不喊狼来了）。
- `description` 末尾**追加**一句说明（纯追加，未改动原文）。

### 2. `Source/PuddingRuntimeTests/Tools/CodeSymbolSearchIndexFreshnessTests.cs`（新增，343 行 / 8 用例）

手写 `ICodeIndexMaintenance` 桩（`MaintenanceStub`，不依赖 mock 框架），不触 SQLite，可与运行中的 Core 并行执行。

## 验收证据（父级独立复算，不采信自述）

| 证据 | 取值 |
|---|---|
改动面 | **恰为预期**：`CodeQueryTools.cs` M（+127 / −2）+ 新测试文件 `??` |
源码 SHA（改动前后） | `a9cf23a1b1c24b0a6f8f8829b3b1562df19d78ee6bd44ab4dc65618813d0041b`（**逐位一致**） |
亲跑新增测试（复原态） | **失败 0 / 通过 8 / 总计 8**（exit 0） |
**父级自做变异取红** | 禁用 (A) `IndexInFlight ⇒ rebuilding` 与 (B) unknown 诚实说明两处锚点 ⇒ **失败 4 / 通过 4**，红的恰是依赖该两锚点的 4 条（`:49` / `:114` / `:135` / `:204`） |
产物 DLL SHA | 变异 `424cd1882897e74a84e5514389b9f300a24ae4f023f9e3e0af0de4fcc8b8b425` → 复原 `b54cdb32a3f75f92bfbb1c3bf7ffe6b5942b317e0699b6501da4332b8a8d74f8`（**不同** ⇒ `-t:Rebuild` 确认真重编译） |
残留扫描 | `MUTATION-T1V` **0 命中**；正对照 `index_freshness` 命中 2 行（证明仪器有效，非假阴性） |

> ⚠️ 纪律提示：复原源码后必须 `dotnet build -t:Rebuild`。`Copy-Item` 复原会把 mtime 回退，MSBuild 增量判定会**跳过重编译**，跑的还是**变异版 DLL** —— 本会话已实测踩过。

## 已知取舍 / 未做

- `index_freshness` 是**追加**字段；若调用方对返回 JSON 做严格字段全集断言或字节快照，会因多出该字段而失败（本仓全量测试已验证无新增失败）。
- `search_grep` 侧同款信号**本切片不做**（留作下一刀）。
- 全量 `PuddingRuntimeTests` 唯一失败 `HostShellExecutor_WslMode_UsesWindowsWorkingDirectoryMapping` 为**本机未安装 WSL** 的环境性失败，与本次改动无因果。
- 改动在**进程内工具** ⇒ 需部署 Core 后才在运行时生效。

## 关联

- 缺陷册：`Docs/12_features/Index-Retrieval-Known-Defects-2026-10-01.md`（D1 / D2 / D5 / D8 / D10 等）
- 部署决策包：`Docs/13_runbooks/Core部署决策包与验收清单-2026-10-03.md`
