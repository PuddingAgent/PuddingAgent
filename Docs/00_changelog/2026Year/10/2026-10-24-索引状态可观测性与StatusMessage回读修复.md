---
title: 2026-10-24 索引状态可观测性与 StatusMessage 回读修复
author: hyfree
date: 2026-10-24
last_reviewed: 2026-10-24
status: archived
description: "code_index_status 的 status 只是注册记录，会长期停在 Pending 而索引其实可用；补 index_freshness 并修掉 StatusMessage 只写不读，让调用方不再被误导回慢速 scan。"
categories: [docs, changelog]
tags: [code-index, retrieval, observability, diagnostics]
related_docs: [Docs/14_reports/2026-10-24-DeepSeek-tokenizer调研.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/CodeIntelligence/CodeQueryTools.cs, Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs, Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs, Source/PuddingCodeIntelligence/Services/CodeQueryService.cs, Source/PuddingCodeIndexTests/Storage/SqliteCodeIndexStoreTests.cs, Source/PuddingRuntimeTests/Tools/CodeIndexStatusRegistryGateTests.cs]
slug: changelog-2026-10-24-index-status-observability
draft: false
---

# 2026-10-24 索引状态可观测性与 StatusMessage 回读修复

## 起因：同一个 scope，两个后端给出不同完整度

对 `Source` 全目录搜同一个符号时，`search_grep`（scan）返回 6 条且 `complete=false`（只扫 983/2000 文件），`code_symbol_search`（index）返回 7 条且 `complete=true`。**被 scan 漏掉的那条正是唯一真实实现**，其余是测试替身。

量化后确认上限本身合理：`Source` 下 13,480 文件、其中 `.cs` 3,282 个，而 `SearchGrepTool` 枚举上限 2,000 —— scan 面向窄范围、有 10 秒兜底，截断时也确实提示改用索引工具。**问题不在 scan，在于没人愿意用索引工具。**

## 根因：`status` 是注册记录，不是索引可用性

```
code_index_status   → status: "Pending", completed_at: 18 天前
code_symbol_search  → index_freshness.state: "idle", watcher 已附着, 当天校准
```

`CodeProjectStatus` 枚举里**没有 `Pending`**（`Unknown/Registering/Active/Removing/Removed/Failed`），而 `ToIndexStatus` 把 `Registering` 映射成 `Pending`。

`Registering` **不是脏数据**：`CodeIndexScheduler` 在作业开始时写它（L204）、结束写 `Active`/`Failed`（L226），而 `MarkInterruptedAsync`（L292-315）**故意**不把被取消的行移出 `Registering` —— 注释原文「the scope still owes a full run - the status semantics are unchanged」。所以它表示**「欠一次完整索引运行」**。

**关键判断**：`Registering` 语义正当，**不应自动推进到 `Active`**（那会抹掉该标记要传递的事实）。需要修正的是**「它意味着什么」的说明**，不是它本身。工具描述原文「status 为 Pending/Indexing 时查询类工具结果不完整，等 Completed 再查」**在原理上就是错的**：欠一次完整运行 ≠ 结果不可用，watcher 与校准一直在工作。

## 改动

- **抽 `CodeQueryToolHelper.ReadIndexFreshness`**：~56 行新鲜度逻辑原本在 `code_symbol_search` / `code_callers` / `code_impact` 各复制一份；给 `code_index_status` 加第四份会把重复变成 4，且判定一旦分叉，不同工具会对同一索引给出不同结论。`IndexFreshnessView.ToWire()` 保持 wire 字段名逐字不变，跨 scope 聚合（布尔取或、计数留 null）只在一处。`CodeQueryTools.cs` 净减 45 行。
- **`code_index_status` 增加 `index_freshness`**：注册状态与运行时状态并列呈现，矛盾可读而非误导。
- **修工具描述**：`status` = 注册记录；运行时真相看 `index_freshness.state`。原「返回体逐字不变」收窄为「既有六字段不变，新信息只允许末尾追加」。
- **`StatusMessage` 从只写变可读**：`CodeProjects` 有该列、`UpdateProjectStatusAsync` 写它、`MarkInterruptedAsync` 靠它区分「被中断」与「从未开始」，但两处 SELECT 用**字面量 `NULL`** 占位、`CodeProjectRecord` 无此成员、`ReadProject` 跳过列 7、query service 不传 `Message` ⇒ `message` 恒 null，诊断写入即蒸发。现 record 末尾加该字段、两处 SELECT 换真列、`ReadProject` 读列 7、`GetProjectIndexStatusAsync` 传入。

## 验证

- `PuddingCodeIndexTests` **373/373**；`PuddingRuntimeTests` 全量 **2276 通过 / 6 失败 / 7 跳过**，6 个失败与改动前完全相同（python/terminal×4/DirectLlm，均与本改动无关）。
- 受影响 freshness/registry 测试类 **36/36**，含新增两例：复现 `status=Pending` 且 `freshness=idle` 必须双报；无维护服务 ⇒ `unknown`，绝不猜 `idle`。
- **变异取红**（两处）：helper 忽略 maintenance 恒返 unknown ⇒ 24 个里 14 红（含跨 scope 聚合与空列表边界）；`StatusMessage` 的 SELECT 改回 `NULL` ⇒ 1 红 / 9 绿，断言原文 `应为: <indexed>，实际为: <>`。两次复原均逐位相同（SHA-256 实测），`-t:Rebuild` 后恢复全绿。
- 冻结契约 `Registered_Project_Output_Is_Frozen_Byte_For_Byte` 如期变红，**未绕过**——更新冻结常量并把理由写在常量旁，六字段一字未动、新字段末尾追加。

## 一个被推翻的假说（记录以免复发）

我一度认为 `unresolved_source_path_count = 6580`（约占 `Source` 文件数一半）说明索引只覆盖了一半。该字段实为 `RetryableFileCount`：**失败/待重试 + 仍在退避窗口内被跳过**，不是失败计数。用「今天刚写的符号能否被索引找到」做决定性探针——`ReadIndexFreshness` 13:00 写入、13:15 命中且路径行号正确 ⇒ 扫描水位在前进、索引跟得上工作树。**信任问题已回答**；失败与退避的具体构成需逐轮源维护日志，未继续挖。

## 未做 / 边界

- 状态机未动（`Registering` 语义正当，见上）。
- 本改动**尚未部署**：`index_freshness` 的实际效果与 S5 tokenizer 的运行时比值（1.83× → 预期 ≈1.0）都必须重启宿主才能验证，属破坏性操作，未执行。
- `code_index_status` 的 `message` 现在能带回中断原因，但**真实库中既有的历史消息**未回填验证。
