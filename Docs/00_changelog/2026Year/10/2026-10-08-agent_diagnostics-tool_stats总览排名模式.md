---
title: 2026-10-08 agent_diagnostics 的 tool_stats 新增总览/排名模式（tool_name 可缺省）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "agent_diagnostics 的 tool_stats 原先必须传 tool_name，否则直接回 error —— 想回答「哪个工具在失败」只能逐个工具试探。现在 tool_name 缺省、传 all 或 * 即进入总览模式：把样本内全部带 tool_name 元数据的活动按工具聚合，按「失败数降序 → 调用数降序 → 工具名升序」稳定排名，随附 tools_in_sample / tool_count_returned / activities_without_tool_name 与既有 sample 口径块，不把未标注活动静默并入任何工具。"
categories: [docs, changelog]
tags: [agent_diagnostics, tool_stats, overview-ranking, diagnostics-instrument, observability]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-agent_diagnostics-tool_stats统计口径修复.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs, Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs]
slug: changelog-agent-diagnostics-tool-stats-overview-2026-10-08
draft: false
---

# 2026-10-08 agent_diagnostics 的 tool_stats 新增总览/排名模式（tool_name 可缺省）

> 范围：`Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs`、`Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs`
> 前置：同日 `Docs/00_changelog/2026Year/10/2026-10-08-agent_diagnostics-tool_stats统计口径修复.md`（统计样本不再被 `limit` 截断）

## 问题（改动前）

`tool_stats` 的 `tool_name` 是必填项，缺省直接返回
`{"error":"tool_name is required for tool_stats action."}`。

于是「**哪个工具在失败**」这个自诊断里最常见的问题，只能靠逐个工具名试探；而 `tool_name` 又是**子串匹配**，猜错还会命中一批无关工具。等于手里有统计能力，却没有入口看全局。

## 改动

`GetToolStatsAsync` 开头先判模式：

| 入参 | 行为 |
|---|---|
| `tool_name` 为空 / `all` / `*` | **总览模式**（新增）：`BuildToolStatsOverview` |
| 其它 | 单工具统计（既有路径，语义未改） |

总览输出：

```json
{
  "mode": "overview",
  "tools": [ { "tool_name": "...", "total_calls": 5, "success_count": 3,
               "failure_count": 2, "success_rate": 0.6,
               "avg_duration_ms": 30.0, "max_duration_ms": 50 } ],
  "tool_count_returned": 3,
  "tools_in_sample": 3,
  "activities_without_tool_name": 1,
  "sample": { "activities_queried": 13, "sample_limit_requested": 500,
              "sample_limit_effective": 500, "sample_truncated_by_sink_limit": false }
}
```

要点：

- **排序稳定**：`failure_count` 降序 → `total_calls` 降序 → `tool_name`（OrdinalIgnoreCase）升序。失败最多的排最前，而不是调用最多的 —— 「哪个工具在出错」一眼可见。
- **`limit` 只约束返回的工具条数**，不改变聚合口径：`tool_count_returned` 与 `tools_in_sample` 分开上报，避免「截断了却像全量」。
- **未标注活动如实计数**：没有 `tool_name` 元数据的活动不入排名（不猜、不并入某个工具），但计入 `activities_without_tool_name`。
- 复用同日修复引入的 `sample` 口径块（含 sink 的 500 条硬上限），采样边界语义与单工具路径一致。
- 抽取为 `private static BuildToolStatsOverview(...)`，单工具路径与既有早退分支未动。

同一改动还更新了 `ToolName` 的 `[ToolParam]` 描述（说明可缺省/`all`）与工具类注释中 `tool_stats` 一行。

## 兼容性

`tool_name` 缺省由「返回 error 字符串」变为「返回总览数据」—— 属行为变更，但全仓 `search_grep` 检索 `tool_name is required` 仅命中**实现处 1 行**（`Source/PuddingRuntimeTests` 内 0 命中），无调用方或测试依赖该错误文本。

## 验证（原始证据）

命令：`dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter FullyQualifiedName~AgentDiagnosticsToolTests --no-restore --nologo -v q`
全文见 `temp/diag-toolstats-overview-evidence.md`。

| 阶段 | 原始输出 |
|---|---|
| 修复后跑 | `已通过! - 失败: 0，通过: 16，已跳过: 0，总计: 16，持续时间: 246 ms`（exit=0） |
| **变异取红**（第一排序键 `failure_count` 改为 `total_calls`） | `失败! - 失败: 2，通过: 14，已跳过: 0，总计: 16，持续时间: 251 ms`（exit=1），精确失败在两个新增用例 |
| **复原复跑** | `已通过! - 失败: 0，通过: 16，已跳过: 0，总计: 16，持续时间: 187 ms`（exit=0）；`git diff` 复核无变异残留 |

新增用例（`AgentDiagnosticsToolTests`）：

1. `ToolStats_OverviewRanksByFailuresThenCallsAndSkipsUntaggedActivities` —— 13 条活动（`code_outline` 6 次全成功、`file_patch` 5 次含 2 次失败、`search_grep` 1 次、1 条无 `tool_name`），断言：`mode == "overview"`；`tools[0]` 为 `file_patch`（失败 2）而**不是**调用数最多的 `code_outline`（这条同时锁住第一、第二排序键）；`tools[1]` 为 `code_outline`；`activities_without_tool_name == 1`。
2. `ToolStats_OverviewAcceptsAllAliasAndHonoursLimit` —— `tool_name="all"` + `limit=2`，断言 `tool_count_returned == 2` 而 `tools_in_sample == 3`（`limit` 不改变聚合口径）。

测试桩复用同日引入的 `FakeActivitySink` 与 `Activity` 助手，新增 `UntaggedActivity`（`Metadata = null`）。

## 影响面与遗留

- 单工具路径的统计口径与 `sample` 块未变；采样上限（最近 500 条活动）仍受 sink 约束并已显式上报。
- `code_map` 未改：`AgentDiagnosticsTool.cs` 条目的描述与约束讲的是工具职责/边界，本次为同工具内的能力补齐。
- 宿主未部署：与既有 `file_patch` P0/P1 修复一样，本改动目前只在源码与测试中生效，运行中的 Core 仍加载 2026-10-06 22:57 的 `PuddingRuntime.dll`。
