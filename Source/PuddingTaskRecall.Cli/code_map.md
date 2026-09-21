# PuddingTaskRecall.Cli CodeMAP

> 历史脏数据一次性诊断/修复 CLI（看板卡 4ed930e7 原子任务③） | .NET 10 · Microsoft.Data.Sqlite · 只读优先的直连库审计

## 入口 & 配置

| 文件 | 用途 |
|------|------|
| `Program.cs` | 🔑 CLI 入口（160 行）。参数：`--apply`（**默认 dry-run**，只读诊断并落盘报告）、`--db <path>`、`--out <path>`、`--backup-dir <dir>`、`--help`/`-h`；未知参数 → stderr + 退出码 2。仅显式 `--apply` 才写库：写前先把 db 文件复制到 `temp\`（含时间戳）备份，全部修复包在**单事务**内，失败整体回滚 |
| `PuddingTaskRecall.Cli.csproj` | 目标框架 net10.0；代码 `using` 显示依赖 `PuddingPlatform.Data.Entities`（平台实体/SQLite 事实表）与 `PuddingCode.Scheduling`、`PuddingCode.Tasks`（Core 契约） |

## 核心功能

| 文件 | 用途 |
|------|------|
| `TaskRecallAuditEngine.cs` | 🔑 审计与修复引擎（922 行），两个静态类：`TaskRecallAuditEngine` 与 `TaskRecallReportWriter` |
| ├ `TaskRecallAuditEngine.Analyze(...)`（:173） | 只读侦查：返回 `TaskRecallResult` 报告（状态分布、binding/projection 快照、Completed 事件样本、A 类回填清单），不写库 |
| ├ `TaskRecallAuditEngine.ApplyAsync(...)`（:225） | 显式 `--apply` 才执行；按报告修复并返回 `ApplyOutcome`，单事务提交 |
| ├ 侦查查询区（:332） | task / attempt / binding / projection 事实查询（SQLite 直查） |
| ├ 辅助区（:660） | 查询与修复的公共辅助 |
| └ `TaskRecallReportWriter`（:751） | 报告落盘（Markdown/JSON） |

### 结果模型（`TaskRecallAuditEngine.cs:10` 起）

| 模型 | 含义 |
|------|------|
| `TaskStatusCount` / `AttemptStatusCount` | 任务 / 尝试的状态分布；`UnreleasedCount` 标出未释放的尝试 |
| `BindingSnapshot` | 执行绑定健康度：`ExecutionId`/`SessionId` 为空计数、终态任务上的可修复数、活跃任务上的 pending claim |
| `ProjectionSnapshot` | 投影过期清理口径：`Total` / `Expired` / `WouldDelete` / `SkippedLiveTask` |
| `TaskCompletedBackfill` | **A 类修复**：`Completed`（status=8）但缺 `TaskCompleted`（event_type=10）事实的任务回填 |
| `CompletedEventSample` | 已完成事件抽样（含 `decisionCode`/`correlationId`/`causationId` 归因字段） |

## 测试

无对应测试项目。验证方式是 `--db` 指向真实库先跑默认 dry-run 看报告，再按需 `--apply`（写前有 db 备份，写库为单事务可整体回滚）。
