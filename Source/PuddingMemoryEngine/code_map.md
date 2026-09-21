# PuddingMemoryEngine CodeMAP

> 记忆引擎 | Library/Book/Chapter · FTS5 全文搜索 · 潜意识处理

## 核心引擎

| 文件 | 用途 |
|------|------|
| `MemoryEngine.cs` | 🔑 记忆引擎主入口（25KB） |
| `MemoryEntry.cs` | 记忆条目模型 |
| `MemoryBoundaryService.cs` | 记忆边界服务 |
| `BookRegistry.cs` | Book 注册表 |
| `SessionMemoryStore.cs` | 会话级记忆存储（7KB） |
| `WorkspaceMemoryStore.cs` | 工作区级记忆存储（7KB） |

## 存储层

| 目录/文件 | 用途 |
|------|------|
| `Data/` | 数据访问层（`MemoryDbContext` + `init_memory.sql` 幂等建表 + additive 补列迁移） |
| `Entities/` | 实体定义（含 `CompactionCoverageManifestEntity` 压缩覆盖清单、`ContextSegmentEntity` ContextSegmentLedger 底座、`CompositionSnapshotEntity` P0-5 Composition 快照） |
| `Schema/` | 数据库 Schema（`CompactionCoverageManifests` 表、`Sessions.CompactionGeneration` 列、`ContextSegments` 表、`CompositionSnapshots` 表（P0-5，复合主键 (SessionId, CompositionVersion)）、`CompositionSnapshots.ToolBindings` 列（C01-B-3，工具定义身份 toolId+definitionHash，不存 schema 正文）、`Messages.ContextGeneration/CanonicalContentHash` 列） |

## 服务

| 文件 | 用途 |
|------|------|
| `Services/FactMemoryService.cs` | 事实记忆服务（23KB） |
| `Services/MemoryRecallService.cs` | 记忆召回服务（19KB） |
| `Services/MemoryLibrarian.cs` | 记忆图书馆员 |
| `Services/SkillEvolutionDeduplicationService.cs` | 🔑 Skill 进化去重（26KB） |
| `Services/SubconsciousOrchestrator.cs` | 潜意识编排（75KB，核心） |
| `Services/SkillPortfolioAdmissionJudge.cs` | 🆕 G3 规则层：组合预算判定器（**纯函数**）。判定序 fail-closed；**no-upgrade**（非 create 永不变 create）；冷启动 Defer 禁 Displace；阈值全来自策略对象 |
| `Services/SkillPortfolioAdmissionExecutor.cs` | 🆕 G3 副作用层：置换 = 先禁用“价值最低者”（**禁用而非删除 ⇒ 可回滚**）再物化候选；候选建不出来 ⇒ 回滚恢复；merge/skip/defer **零写盘**；无置换目标 ⇒ fail-closed defer 且零写盘 |
| `Services/SubconsciousJobQueue.cs` | 潜意识任务队列（~29KB）；schedule_skip 按 (workspace, 5 分钟窗口) 内存聚合，窗口滚动时只写一条 `subconscious_job.schedule_skip.summary`（telemetry 为唯一 authoritative owner，不再逐事件双写 activity+metric）；明细仅保留派发/错误/状态变化。可控时钟 TimeProvider 可注入 |

目标演进：保留持久 Job 的 lease/retry/dead-letter，把 Pre-Compaction Flush、后台提取、Auto-Dream、经验转 Skill、Skill Self-Improvement 拆为事件驱动 learning stage plugins；统一经过 signal → candidate → immutable proposal → evaluation → approval/canary → activation → monitoring/rollback，详见 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`。

## 基础设施

| 文件 | 用途 |
|------|------|
| `Infrastructure/` | 基础设施（索引、存储实现） |

## 测试

`../PuddingMemoryEngineTests/` — Library/Book/Chapter、FTS5、Skill 进化去重、ContextSegmentLedger（21/21 ✅）
`../PuddingMemoryEngineBenchmarks/` — BenchmarkDotNet 基准测试
