---
title: 2026-10-08 agent_diagnostics 的 tool_stats 统计口径不再被 limit 截断
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "agent_diagnostics 的 tool_stats 原先把调用方的 limit 用在「过滤后、聚合前」，于是 total_calls / success_rate / avg_duration_ms / max_duration_ms / common_errors 全部只统计最近 limit 条活动，而返回值看起来像「该工具的全量统计」——同一份数据换个 limit 就换一个结论，诊断仪器自身失真。现改为统计覆盖样本内全部匹配活动，limit 只决定采样量，并随附 sample 块如实上报采样边界（含 sink 的 500 条硬上限），common_errors 补稳定次序。"
categories: [docs, changelog]
tags: [agent_diagnostics, tool_stats, diagnostics-instrument, sample-accounting, observability]
related_docs: [Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md]
related_files: [Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs, Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs]
slug: changelog-agent-diagnostics-tool-stats-sample-2026-10-08
draft: false
---

# 2026-10-08 agent_diagnostics 的 tool_stats 统计口径不再被 limit 截断

> 范围：`Source/PuddingRuntime/Tools/BuiltIns/Diagnostics/AgentDiagnosticsTool.cs`、`Source/PuddingRuntimeTests/Tools/AgentDiagnosticsToolTests.cs`

## 问题（改动前）

`GetToolStatsAsync` 的三段式是：向 sink 取 `Limit = Math.Max(limit * 10, 500)` 条活动 → 按 `tool_name` 过滤 → **`.Take(limit)`** → 聚合。

第三段的 `.Take(limit)` 让统计样本被调用方的 `limit` 截断，于是：

- `tool_stats(tool_name="file_patch", limit=2)` 报出的 `total_calls` 恒为 2、`success_rate` 是「最近 2 次」的成功率、`avg_duration_ms` / `max_duration_ms` / `common_errors` 同理；
- 而字段名与工具描述都在暗示这是**该工具的全量统计**。同一份数据换个 `limit` 就换一个结论；
- 这是**诊断仪器自身失真**：我用 `agent_diagnostics` 做自我诊断时，读到的「成功率 100%」可能只是最近 2 次恰好成功。

另外两点同源缺陷：

1. 采样边界不可见。`IRuntimeActivitySink` 的实现（`RuntimeActivitySink.QueryAsync`）在查询层把 `Limit` 夹到 `[1, 500]`，所以无论 `limit` 传多大样本都 ≤ 500 条；返回值对此**一字不提**，让截断后的统计看起来像全量。
2. `common_errors` 只有 `OrderByDescending(g => g.Count())`，同计数时无 tie-breaker，同一份数据多次查询的次序可能漂移。

## 改动

| 项 | 改动前 | 改动后 |
|---|---|---|
| 统计样本 | 过滤后 `.Take(limit)` | **去掉截断**，覆盖样本内全部匹配活动 |
| `limit` 语义 | 同时决定采样量与统计口径 | 只决定每次向 sink 的采样量（`Math.Max(limit * 10, 500)`，与原值一致） |
| 采样边界 | 无 | 新增 `sample` 块：`activities_queried` / `sample_limit_requested` / `sample_limit_effective`(≤500) / `tool_activities_matched` / `sample_truncated_by_sink_limit` |
| `common_errors` | `OrderByDescending(count)` | 追加 `.ThenBy(g => g.Key, StringComparer.Ordinal)` 稳定次序 |

未改：`tool_name` 的子串匹配语义、`limit` 的 clamp 区间 `[1,200]`、`activities.Count == 0` 与 `toolActivities.Count == 0` 两条早退分支、`slowest_tools` 等其它 action、`AgentDiagnosticsArgs` 参数表（无新增参数，`sample` 为**只增字段**）。

## 验证（原始证据）

三段原始输出的命令均为
`dotnet test Source/PuddingRuntimeTests/PuddingRuntimeTests.csproj --filter FullyQualifiedName~AgentDiagnosticsToolTests --nologo -v q`
（变异/复原两轮加 `--no-restore`）。全文见 `temp/diag-toolstats-evidence.md`。

| 阶段 | 原始输出 | 说明 |
|---|---|---|
| 修复后跑 | `已通过! - 失败: 0，通过: 14，已跳过: 0，总计: 14，持续时间: 224 ms`（exit=0） | 该测试类原有 12 例 + 新增 2 例 |
| **变异取红** | `失败! - 失败: 2，通过: 12，已跳过: 0，总计: 14，持续时间: 243 ms`（exit=1） | 变异 = 恢复修复前的 `.Take(limit)`；精确失败在新加的 2 例，旧 12 例仍绿 |
| **复原复跑** | `已通过! - 失败: 0，通过: 14，已跳过: 0，总计: 14，持续时间: 190 ms`（exit=0） | `git diff` 复核：只剩预期改动，无变异残留 |

新增用例（`AgentDiagnosticsToolTests`）：

1. `ToolStats_StatisticsCoverEveryMatchedActivity_NotJustTheFirstLimit` —— 喂 6 条 `file_patch` 活动（4 成功 / 2 失败，含重复错误串），以 `limit=2` 查询，断言 `total_calls == 6`（修复前为 2）、`success_count == 4`、`failure_count == 2`、`success_rate == 0.667`、`common_errors` 聚成 1 条且 `count == 2`、`sample.sample_truncated_by_sink_limit == false`。
2. `ToolStats_SampleBlockReportsSinkLimitAndStableErrorOrdering` —— 502 条活动 + `limit=200`，断言 `sample_limit_effective == 500`（sink 硬上限如实上报）、`sample_truncated_by_sink_limit == true`；两条同计数错误按字典序输出（`alpha` 在前）。
3. 测试桩 `FakeActivitySink : IRuntimeActivitySink`（记录 `LastQuery`，同步返回预设活动），活动构造走既有 `RuntimeTraceContext.CreateNew`。

## 影响面与遗留

- **行为变更**：`tool_stats` 的 `total_calls` / `success_rate` / `avg_duration_ms` / `max_duration_ms` / `common_errors` 从「最近 limit 条」变为「样本内全部匹配活动」。这是本次修复的目的；因该字段语义原先就自相矛盾，判定为纠错而非破坏性变更。
- 样本仍受 sink 上限（最近 500 条活动）约束，此约束现已在 `sample` 块显式暴露，不再伪装成全量。
- `code_map` 未改动：`Source/PuddingRuntime/code_map.md` 中 `AgentDiagnosticsTool.cs` 条目的描述（`agent_diagnostics`：Agent 自我诊断）与约束列讲的是工具职责与边界，本次为同职责内的口径纠错，未变；工具内的 action 说明注释已同步更新。
- 宿主未部署：与既有 `file_patch` P0/P1 修复一样，本修复目前只在源码与测试中生效，运行中的 Core 仍加载 2026-10-06 22:57 的 `PuddingRuntime.dll`。
