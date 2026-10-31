---
title: R2-A PuddingRetrieval 独立组件（S1–S4）
author: 鲸鲸
date: 2026-10-31
last_reviewed: 2026-10-31
status: delivered
---

# R2-A PuddingRetrieval 独立组件（S1–S4）

> 施工计划 R2-A 四组件之三。前置：R2-01 契约迁移 + 记录类型下沉（`a82301d`）。

## 1. 做了什么

新建 `Source/PuddingRetrieval/`（实现层）+ `Source/PuddingRetrievalTests/`（S2），
把「检索端口的数据面抽象」从 `PuddingCodeIndex` 搬出：

| 类型 | 说明 |
|---|---|
| `IRetrievalSymbolSource` | 符号面窄端口（列项目 / 搜符号 / 取符号 / 列文件 / 入边 / 出边） |
| `IRetrievalTextSource` | 文本面窄端口（scope + 查询词 → 文件行） |
| `RetrievalTextMatch` | 文本命中值类型 |
| `RetrievalTextSourceUnavailableException` | 「没法查」与「查了没有」的区分标记 |

从 `RetrievalPortV1.cs` 抽出 L14–L96，原文件 1537 → 1454 行；`using PuddingRetrieval;` 补入。

**没搬** `RetrievalPortV1`（编排逻辑）与 `CodeIndexStoreSymbolSource`（适配器）——
它们依赖 `ICodeIndexStore`，需要 `PuddingRetrieval.Adapters` 才能迁出。

## 2. 依赖方向（过渡态，已写进 csproj 注释）

```
当前：PuddingCodeIndex → PuddingRetrieval   （反的，因为适配器还在 CodeIndex）
目标：PuddingRetrieval  ←  PuddingRetrieval.Adapters → PuddingCodeIndex
```

这条反向边是**临时**的：适配器一迁出就应删除。csproj 里已写明「届时本引用应改为 Adapters → CodeIndex」，
并在 `ComponentBoundaryTests` 里断言 `PuddingRetrieval` 本身不引用存储/抽取/运行时。

## 3. 边界断言（两层，都验证过能取红）

| 层 | 断言 | 变异表现 |
|---|---|---|
| 运行时反射 | 引用的 `Pudding*` 程序集 ⊆ {`Retrieval.Contracts`, `Domain.Contracts`} | 加一条**未使用**的 `PuddingGrep` 引用 → **不红** |
| csproj 声明 | 恰好 2 条 `ProjectReference` 且不含禁项 | 同一变异 → **恰好 1 红** |

第一层抓不到「引用但没用」—— 未被使用的 `ProjectReference` 不进入产物引用表。
这正是补第二层的原因，本轮用变异实测确认了两层的分工。

## 4. 三态证据

```
基线    PuddingRetrievalTests 4/4
变异    csproj 加 PuddingGrep 引用 → 失败 1 / 通过 3
        失败项 = RetrievalCsproj_DeclaresOnlyAllowedProjectReferences
        报错 "Assert.HasCount 失败。大小 2 的预期集合。实际：3"
还原    SHA 位级一致 EDE6B4C09A88C794… + -t:Rebuild → 4/4 复绿
```

## 5. 回归

```
PuddingCodeIndexTests          425/425
PuddingCodeIntelligenceTests   151/151
PuddingRuntimeTests          2504 通过 + 7 跳过
PuddingRetrieval.ContractsTests 51/51
PuddingDomain.ContractsTests     6/6
PuddingRetrievalTests            4/4
```

各工程与上一轮逐项一致，无新增回归。

## 6. 搬迁波及（第一次跑回归时暴露）

`PuddingRuntime` 的 `FullTextRetrievalTextSource` / `RetrievalServiceCollectionExtensions` 与两个测试工程
也在用这些类型 —— 第一次回归 `r1`/`r2`/`r4` 全部编译失败（`CS0246 IRetrievalTextSource`）。
补 `using PuddingRetrieval;` 后恢复。

**教训**：搬类型前应先 grep「谁用了这些类型」，而不只是「谁在同一个文件里」。

## 7. 未做

- `RetrievalPortV1`（约 1340 行编排逻辑）未迁 —— 需 `Adapters` 先行
- `PuddingRetrieval.Adapters` 未建
- 未注册 DI、未接入宿主（S5）
- `PuddingRuntime` 构建有 88 个警告，**未核实是否全为预存**（本轮只确认 0 错误）
