# PuddingTaskRecall.Cli CodeMAP
> 源指纹: *.cs=2-03abc8c90916, *.csproj=1-c8e6a72a856e · 条目数: 3 · 最近整理: 2026-10-14

> 历史脏数据一次性诊断/修复 CLI（看板卡 4ed930e7 原子任务③） | .NET 10 · Microsoft.Data.Sqlite · 只读优先的直连库审计

## 入口 & 配置

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | CLI 入口：参数解析与 dry-run/apply 分流 | `—` | `TaskRecallAuditEngine.cs` | 默认 dry-run，只有显式 `--apply` 才写库；写前先备份且全部修复包在单事务里 |
| `PuddingTaskRecall.Cli.csproj` | net10.0 一次性诊断 CLI 工程 | `—` | `Program.cs` | 依赖平台实体事实表与 Core 契约，不得反向引用宿主执行链 |

## 核心功能

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `TaskRecallAuditEngine.cs` | 只读侦查与按报告修复的引擎，并负责报告落盘 | `TaskRecallAuditEngine`、`TaskRecallReportWriter`、`TaskRecallResult`、`ApplyOutcome` | `Program.cs` | 侦查阶段绝不写库；A 类回填只处理已完成但缺 `TaskCompleted` 事实的任务 |

### 结果模型（`TaskRecallAuditEngine.cs:10` 起）

结果类型集中在 `TaskRecallAuditEngine.cs`（`TaskStatusCount` / `BindingSnapshot` / `ProjectionSnapshot` / `TaskCompletedBackfill` / `CompletedEventSample`），逐型含义见源码同名类型。

## 测试

无对应测试项目。验证方式是 `--db` 指向真实库先跑默认 dry-run 看报告，再按需 `--apply`（写前有 db 备份，写库为单事务可整体回滚）。

## 表「用途」列·关键语义（逐字原文）

> 下列为 `## 入口 & 配置` 与 `## 核心功能` 两表「用途」列的**逐字原文**（行序与两表一致）；表内已缩为语义摘要，并以 `（见下「关键语义」）` 指回本段。

- `Program.cs`
  🔑 CLI 入口（160 行）。参数：`--apply`（**默认 dry-run**，只读诊断并落盘报告）、`--db <path>`、`--out <path>`、`--backup-dir <dir>`、`--help`/`-h`；未知参数 → stderr + 退出码 2。仅显式 `--apply` 才写库：写前先把 db 文件复制到 `temp\`（含时间戳）备份，全部修复包在**单事务**内，失败整体回滚
- `PuddingTaskRecall.Cli.csproj`
  目标框架 net10.0；代码 `using` 显示依赖 `PuddingPlatform.Data.Entities`（平台实体/SQLite 事实表）与 `PuddingCode.Scheduling`、`PuddingCode.Tasks`（Core 契约）
- `TaskRecallAuditEngine.Analyze(...)`（:173）
  只读侦查：返回 `TaskRecallResult` 报告（状态分布、binding/projection 快照、Completed 事件样本、A 类回填清单），不写库
