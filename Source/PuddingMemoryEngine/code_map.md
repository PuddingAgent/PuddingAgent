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
| `Entities/` | 实体定义（含 `CompactionCoverageManifestEntity` 压缩覆盖清单） |
| `Schema/` | 数据库 Schema（`CompactionCoverageManifests` 表、`Sessions.CompactionGeneration` 列） |

## 服务

| 文件 | 用途 |
|------|------|
| `Services/FactMemoryService.cs` | 事实记忆服务（23KB） |
| `Services/MemoryRecallService.cs` | 记忆召回服务（19KB） |
| `Services/MemoryLibrarian.cs` | 记忆图书馆员 |
| `Services/SkillEvolutionDeduplicationService.cs` | 🔑 Skill 进化去重（26KB） |
| `Services/SubconsciousOrchestrator.cs` | 潜意识编排（75KB，核心） |
| `Services/SubconsciousJobQueue.cs` | 潜意识任务队列（27KB） |

目标演进：保留持久 Job 的 lease/retry/dead-letter，把 Pre-Compaction Flush、后台提取、Auto-Dream、经验转 Skill、Skill Self-Improvement 拆为事件驱动 learning stage plugins；统一经过 signal → candidate → immutable proposal → evaluation → approval/canary → activation → monitoring/rollback，详见 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`。

## 基础设施

| 文件 | 用途 |
|------|------|
| `Infrastructure/` | 基础设施（索引、存储实现） |

## 测试

`../PuddingMemoryEngineTests/` — Library/Book/Chapter、FTS5、Skill 进化去重（21/21 ✅）
`../PuddingMemoryEngineBenchmarks/` — BenchmarkDotNet 基准测试
