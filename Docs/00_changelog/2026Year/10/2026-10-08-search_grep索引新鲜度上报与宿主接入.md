---
title: 2026-10-08 search_grep 的 backend=index 上报索引新鲜度（A23 S5 接入）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "backend=index 查的是索引快照：索引缺失、或构建时刻读不出时，0 命中与「语料里确实没有」在输出上完全同形（假否定）。本轮把 A23 的只读探针 IFullTextIndexFreshnessProbe 接进 SearchGrepTool（可选构造参数，未注册 ⇒ 输出逐字不变），摘要追加 indexFreshness/lastIndexed/indexAge/pattern/indexDirMtime，0 命中且新鲜度不可核实时显式声明「0 命中不是不存在的证据」；宿主把查询侧同一个 LuceneSearchEngine 实例注册为该接口并 fail-closed 转型。"
categories: [docs, changelog]
tags: [search_grep, fulltext-index, freshness, adr-089, false-negative, s5-integration]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-全文索引A23-per-scope新鲜度探针.md, Docs/00_changelog/2026Year/10/2026-10-08-search_grep索引后端状态分报.md, Docs/13_runbooks/Core部署决策包与验收清单-2026-10-03.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs, Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs, Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs]
slug: changelog-search-grep-index-freshness-2026-10-08
draft: false
---

# 2026-10-08 search_grep 的 backend=index 上报索引新鲜度（A23 S5 接入）

> 范围：`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs`、`Source/PuddingRuntimeTests/Tools/SearchGrepToolTests.cs`、`Source/PuddingHost/Extensions/PuddingServiceCollectionExtensions.Runtime.cs`
> 前置（组件侧 S1–S4）：`Source/PuddingFullTextIndex/Contracts/IFullTextIndexFreshnessProbe.cs` + `FullTextIndexFreshness.cs` + `LuceneSearchEngine` 的显式实现 + `PuddingFullTextIndexTests` 3 例（同一工作日内完成）

## 问题（改动前）

`backend=index` 是**快照查询**：它答的是「**索引里**有没有」，而调用方要的是「**语料里**有没有」。两者在两种情形下会被读成同一个答案：

| 真实情形 | 改动前输出 | 调用方的读法 |
|---|---|---|
| scope 从未建索引 | 已有防护：`dependency_wait` + 明写 "nothing was searched" | 正确（不会误读） |
| 索引存在、但已很旧 / `.last_indexed` 读不出 | `(no matches)` + 摘要 | **「这里没有」** ← 假否定，ADR-089 §8 明禁 |

摘要里当时只有 `scope/lines/engineMatches/engineMs/totalMs/complete/limit_reason`，**没有任何关于索引年龄的事实**。于是「索引是三天前建的、文件昨天才改」与「语料里真的没有」在输出上不可区分。

## 改动

### 1. 组件侧（前置，已交付）

`IFullTextIndexFreshnessProbe`（只读接缝，**刻意不给既有 `IFullTextSearchEngine` 加成员** —— 它被 CLI 与多处替身共同实现）+ 三态读数 `FullTextIndexFreshness{State, LastIndexedAtUtc, PatternFingerprint, IndexDirectoryLastWriteUtc}`，由 `LuceneSearchEngine` 显式实现（只读 `.last_indexed` 与**该 scope 自己的**索引目录 mtime，不打开 reader）。

### 2. 工具接入（本轮）

- 构造参数新增**可选** `IFullTextIndexFreshnessProbe? freshnessProbe = null`（末位 ⇒ 既有构造调用与桩**零改动**）。
- 摘要追加事实块：

  ```
  ... , indexFreshness=available, lastIndexed=2026-10-06T22:57:38.0000000Z, indexAge=3.2h, pattern=abc123def456, indexDirMtime=2026-10-06T23:02:38.0000000Z; single Lucene query-parser call, ...
  ```

  三态如实分别上报 `available` / `missing` / `stamp-unreadable`；读不出就是 `<null>`，**不伪报**时间。
- **0 命中且新鲜度不可核实**时，输出不再是空洞的 `(no matches)`，而是显式声明：

  > `(no matches — this scope's index freshness could NOT be verified for this query (see indexFreshness= in the summary line below), so 0 hits are NOT evidence that the pattern is absent. Rebuild this scope's index, or omit 'backend' to use the managed scan path, which reads the working tree directly)`

- 探针是**附属事实**：抛异常不让检索失败，但也不静默 —— 开日志并如实标 `indexFreshness=probe-error`（`Verified=false` ⇒ 同样触发上面的提示）。

### 3. 宿主注册（本轮）

`PuddingServiceCollectionExtensions.Runtime.cs` 紧随引擎注册之后：

```csharp
builder.Services.AddSingleton<IFullTextIndexFreshnessProbe>(sp =>
{
    if (sp.GetRequiredService<IFullTextSearchEngine>() is not IFullTextIndexFreshnessProbe probe)
        throw new InvalidOperationException("A23 S5：...");
    return probe;
});
```

解析**既有注册**再转型 ⇒ 与查询侧**同一实例**（不会读到别的索引根/缓存）；转型 fail-closed ⇒ 实现摘掉接口时在**解析时**报错，而不是让新鲜度静默变 `null`（功能静默不生效）。与同文件既有 `IFullTextIndexRootedEngine` 的写法一致。

## 验证（原始输出，全部可复核）

| 阶段 | 原始输出 | 证据文件 |
|---|---|---|
| 聚焦（改后） | `已通过! - 失败: 0，通过: 70，总计: 70` （原 64，新增 6） | `temp/s5-green.txt` |
| **变异 A**（删掉 null 守卫） | `失败: 2，通过: 68，总计: 70` —— 命中 `Backend_Index_Without_Probe_Omits_Freshness_Facts`（意图）+ `Backend_Index_Zero_Hits_Without_Syntax_Chars_Does_Not_Retry`（既有用例同样守住「无探针时输出不变」，属**正当连带**） | `temp/s5-mut-a-test.txt` |
| **变异 B**（`Verified` 恒 true） | `失败: 2，通过: 68，总计: 70` —— 恰为两条假否定用例（`...Unreadable...` / `...Missing...`） | `temp/s5-mut-b-test.txt` |
| 复原 | 逐位 `sha256=a6d5ee849b5bdebd…c2157b`（与已验证构建**逐位一致**） | `temp/s5-final-restore.txt` |
| 复原后复跑 | `已通过! - 失败: 0，通过: 70，总计: 70` | `temp/s5-final-green.txt` |
| 全量回归 | `已通过! - 失败: 0，通过: 1983，已跳过: 6，总计: 1989` | `temp/s5-full-suite.txt` |
| 宿主构建（编译期证据） | `已成功生成。21 个警告`（全部落在**其它**文件；本次改动文件 0 警告 0 错误） | `temp/s5-host-build.txt` |

新增 6 例：三态事实上报（含 scope 传递与调用次数）、两条「0 命中 + 新鲜度不可核实 ⇒ 不得读成不存在」、无探针时输出不含该块、探针抛异常不阻断检索、以及**DI 机制**用例（`ServiceCollection` 注册探针后 `SearchGrepTool` 构造参数真的被填入）。

## 事故记录（本卡真正值得留痕的一条）

**运行中的 `file_patch` 第二次静默改写字节。** 本轮做变异取红时用它删 3 行，得到的却是 `67124 → 58128` 字节（**−9 KB**，远超 3 行），并伴随「whitespace-tolerant matching」提示与级联错位的 diff 渲染。该宿主加载的仍是 2026-10-06 22:57 的 `PuddingRuntime.dll`（不含同日 `e97c6d7` 的 CRLF 修复），即**已修的缺陷在运行实例上仍然活跃**。

处置：`git checkout --` 取回提交基线（`62628 B / 62e930c8…`），再用**带锚点断言 + 写盘前校验 + 读回逐字比对**的脚本重放 ⇒ 得到与已验证构建逐位一致的 `a6d5ee84…`；此后所有变异/复原一律走脚本，**不再用 `file_patch` 触碰源码**。

已取证：事后核对宿主目录 `PuddingRuntime.dll` = `2026-10-06 22:57:38 / c2695c40…`（未部署）；新构建产物 = `2026-10-08 13:03:43 / 76fa17be…`。

## 兼容性与影响面

- 未注册探针（含全部既有替身/桩与其它宿主）⇒ 摘要与原实现**逐字相同**，由 `Backend_Index_Without_Probe_Omits_Freshness_Facts` 与既有 `Backend_Index_*` 用例共同守护。
- 探针返回值只进**输出**，不进 telemetry 维度、不进 `Status` 判定 ⇒ 不改变任何既有分支。
- 宿主重启 ≠ 部署：现场实测宿主 PID 27920 于 2026-10-08 11:56:31 启动，但部署目录里的 `PuddingRuntime.dll` 仍是 10-06 22:57 那份（这是同一条事实在本会话的**第 4 次**重现）。

## 遗留

1. **宿主侧组合测试未跑**：唯一能引用 `PuddingHost` 的测试工程是 `PuddingWebApiTests`（经 `PuddingAgent` 间接引用），而构建它必须写 `Source/PuddingAgent/bin/Debug/net10.0/*.dll` —— 实测被运行中的 Core 锁死（`error MSB3027 / MSB3021`，`文件被 "PuddingAgent (27920)" 锁定`，见 `temp/s5-webapi-build.txt`）。⇒ 该验证与**部署**同源受阻，须先停宿主。
2. 部署后应按 `search_grep(backend=index)` 的输出出现 `indexFreshness=` 作为端到端验收。
3. `search_grep` 在**未建索引**（`dependency_wait`）路径上尚未附新鲜度读数（该路径已明写 "nothing was searched"，风险低，故未扩大本次范围）。
