---
title: 2026-11-26 工具失败三次排除与 runtime_activity 取证通道
author: hyfree
date: 2026-11-26
last_reviewed: 2026-11-26
status: active
description: "起因是 tool_stats 报 list_dir 成功率 61.5%（13 次调用 5 次失败）。逐条取证后三条假设全部排除，本轮无代码改动：① list_dir 的 9 条失败全是相对路径落错根，最后一次 2026-11-22，且现行 MissingPathDiagnostics 已在运行时给出解析后绝对路径与缺失层数；② search_grep 目录不存在时已明确报错并带解析路径；③ shell 的 12 条失败全是 exit code，属命令语义而非工具缺陷，且既有 FailureSuggestionTests 明确要求这类『无法归因』回落通用建议。另记录一条可复用取证通道：runtime_activity 表逐条保存工具调用的状态与错误文本。"
categories: [platform, reports]
tags: [tool-failure, triage, sqlite, observability, negative-result]
related_docs: [Docs/14_reports/2026-11-25-子代理运行失败归因普查.md, Docs/14_reports/2026-11-25-文件写入族拒绝损失性解码与运行时验收.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Files/MissingPathDiagnostics.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs, Source/PuddingRuntimeTests/Services/FailureSuggestionTests.cs]
slug: reports-2026-11-26-tool-failure-triage-three-exclusions
draft: false
---

# 工具失败三次排除与 runtime_activity 取证通道

## 1. 动机

`agent_diagnostics(tool_stats)` 在 500 条活动样本里报 `list_dir` 13 次调用、5 次失败（成功率 61.5%），是所有工具中最低的。在动手改代码之前，先确认它是不是已知问题——**避免把已经修好的历史失败当成现存缺陷**（同日已有一次同类误判：`file_write not found` 查完发现无法复现）。

## 2. 取证通道：`runtime_activity`

结论先行：逐条工具调用的状态与错误文本在 `D:\Data\databases\pudding_platform.db` 的 `runtime_activity` 表（本次只读打开，382 894 行）。

```sql
-- 工具名不在 operation 里，必须从 metadata_json 过滤
select Id, started_at_utc, error_message
from runtime_activity
where component='tool_runner' and status='failed'
  and metadata_json like '%"list_dir"%'
order by Id desc;
```

两个实测细节，写错就查不到东西：

- `operation` 只有 `execute_tool`（成功 8747 / 失败 333 两条），**工具名不在其中**。按 `operation like '%list_dir%'` 查询恒得 0 行。
- 失败行的 `error_message` 才是工具返回的错误文本；`summary` 只是 `Tool 'list_dir' execution failed.` 这类包装语。

## 3. 三次排除

### 3.1 `list_dir`：相对路径落错根，且现行诊断已修好

9 条失败记录的 `error_message` 全部是 `Directory not found: <相对路径>`：

```
Tests/PuddingRecovery.SchemaProbe          Source/PuddingSsh.Probe
Tests/PuddingBrowser.WebView2.Smoke        Source/PuddingHomeAssistant.Probe
Tests/PuddingBrowser.TestSite              temp/subagent-clamp-audit.md
Source/PuddingPlatformAdmin                hourglass
Source/PuddingPlatformAdmin/src/pages/storage
```

最后一次发生在 2026-11-22，早于 `MissingPathDiagnostics` 落地（2026-11-25）。运行时复测（2026-11-26 02:2x，真实调用）：

```
Directory not found: Source/PuddingPlatformAdmin
  (resolved to D:\Data\workspaces\default\Source\PuddingPlatformAdmin;
   deepest existing ancestor: D:\Data\workspaces\default (2 missing level(s)))
```

解析后的绝对路径、最深已存在祖先、缺失层数三项齐全 —— 根因（**工作区根 ≠ 仓库根**）在错误文本里已经可以当场看出，不需要试错。**判定：不是现存缺陷。**

### 3.2 `search_grep`：目录不存在时已明确报错

```
Directory 'D:\Data\workspaces\default\Source\PuddingPlatformAdmin' not found.
Provide an existing 'directory', or omit it to search the workspace root.
```

带解析后的绝对路径，并给了两个可选动作。与普查报告 §2.3 的自我纠正一致（该工具的诊断语已合格，19 条属模型误用而非诊断不足）。**判定：不是缺陷。**

### 3.3 `shell`：退出码属于命令语义，不该由工具猜

12 条失败记录的 `error_message` 只有两种：`exit code 1`（11 条）与 `exit code 255`（1 条）。

考虑过「给 exit≠0 专门建议」的改法（现状回落到「检查参数约束」，而改参数并不会改变命令退出码）。放弃的理由有两条：

1. **会推翻一条明确撰写的既有测试意图**：`FailureSuggestionTests.UnknownError_FallsBackToGenericSuggestion_Verbatim` 就是拿 `"exit code 1"` 当「无法归因」的样例，断言逐字回落。那条测试表达的原则是「无法归因就不要猜」。
2. **退出码的语义在命令侧，不在工具侧**：`findstr` 无匹配、`git diff --exit-code` 有差异、`dotnet build` 编译失败都是 1。工具若替命令解释，在第三种情形下会遮蔽真实原因。

正确做法留在调用方纪律里：见到 `exit code N` 先读回执的 `Output` 段，再判断是否属预期。**判定：不改。**

## 4. 取不到的事实（如实标注）

- `metadata_json` 里**没有** `command` / `arguments`（解析为 `None`），因此无法从活动表复现「当时跑的具体命令」。要复现只能回到 `run.json`。
- `tool_stats` 的样本被截断（`sample_truncated_by_sink_limit: true`，500 条上限），成功率是**窗口值**，不是全量历史值。

## 5. 结论

本轮无代码改动、无部署。三条假设全部排除，`list_dir` 的 61.5% 是**历史累积指标**，不反映当前行为。

索引提示：下轮再看到 `tool_stats` 里 `list_dir` / `search_grep` 成功率偏低时，先读本报告与 `Docs/14_reports/2026-11-25-子代理运行失败归因普查.md` §2.3，不要重新普查。
