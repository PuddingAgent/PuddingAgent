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
| `Services/SkillEvolutionDeduplicationService.cs` | 🔑 Skill 进化去重（26KB）。🆕 G4-D6b 起 `CalculateTextSimilarity` 与 `IsDeterministicallyEligible` 为 **`public static`**（原私有实现**原样提为公有、行为逐字不变** ⇒ 合并判据与既有闸门共享同一份口径，⛔ 不得另写第二套）；`ExtractSourceSessions` 供 C1/C2 复用 |
| `Services/SkillFamilyCapJudge.cs` | 🆕 G4-D3 家族内上限判据：输入 = 分簇 + `PerFamilyCap`；输出 = 超限家族 + **评审请求**（**零写盘**：超限后果只有评审请求，⛔ 不得在超限分支禁用/删除技能） |
| `Services/SkillKeywordOwnershipProbe.cs` | 🆕 G4-D4 关键词归属**只读**事实探针：每个关键词的**全部**竞争者（不止第一个）+ 共享关键词数 / 被挤掉次数（须与 G1 报告逐数一致） |
| `Services/SkillKeywordOwnershipJudge.cs` | 🆕 G4-D5 归属**裁决**判据：冲突 ⇒ 待裁决 + reason code（⛔ 默认**不得**是先到先得，也不得是后来者一律拒绝）；归一谓词复用 `Skills/Retrieval/SkillKeywordNormalization` |
| `Services/SkillMergeEligibilityJudge.cs` | 🆕 G4-D6 合并**四条件**判据（纯判定层：零 IO）：同族 / 关键词重叠 / 程序性文本相似度 ≥ 策略阈值 / 证据可归并；`SkillMergeVerdict.IsEligible` **派生自** `FailedConditions`（⛔ 不单独存储 ⇒ 不会自相矛盾）；畸形事实 fail-closed（自配对/重复配对/空字段/非有限相似度/空白关键词一律抛，⛔ 不静默跳过）。C2 用 `SkillKeywordNormalization.KeywordComparer`（D6c）。用例：`PuddingMemoryEngineTests/SkillMergeEligibilityJudgeTests.cs`；真实语料探针：`PuddingRuntimeTests/Services/SkillMergeEligibilityRealIndexProbeTests.cs` |
| `Services/SubconsciousOrchestrator.cs` | 潜意识编排（75KB，核心）；🆕 G7 起 `SkillCurateAsync` 把 `ISkillDistillationSource` 产物逐条过 `SkillCurationGate`，三计数（products/shadow/rejected）写入 `SkillCurationReport`，且**零写盘**（未注入 source/policy ⇒ 恒定 0/0/0） |
| `Services/SkillPortfolioAdmissionJudge.cs` | 🆕 G3 规则层：组合预算判定器（**纯函数**）。判定序 fail-closed；**no-upgrade**（非 create 永不变 create）；冷启动 Defer 禁 Displace；阈值全来自策略对象 |
| `Services/SkillPortfolioAdmissionExecutor.cs` | 🆕 G3 副作用层：置换 = 先禁用“价值最低者”（**禁用而非删除 ⇒ 可回滚**）再物化候选；候选建不出来 ⇒ 回滚恢复；merge/skip/defer **零写盘**；无置换目标 ⇒ fail-closed defer 且零写盘 |
| `Services/SkillCurationGate.cs` | 🆕 G7 提炼契约门禁（**纯判定层**：零 IO / 零 LLM / 零裸阈值，阈值全来自 `SkillCurationPolicy`）：C1 证据不丢；C2 一般性不降（口径 = **max(单个被取代者的 `source-session:` 去重数)**，**不是并集**）；C3 关键词唯一（判定域**排除本次被取代者**，否则合并自身关键词必交集 ⇒ 静默过度阻断）；C4 价值不降（复用 G3 `SkillScoreSnapshot` 两态，`Unavailable` **不得**当 0）；C5 可回滚（`Apply` 缺 `RollbackHandle` ⇒ 构造期拒绝）。理由码 10 条 + 降级码 3 条（`curation:*` / `cold_start:*` / `score_scale_mismatch:*`，见 `SkillCurationGate.cs:126-162`）。⚠️ 本层**无写盘职权**（落点属 L3-b）；用例见 `PuddingMemoryEngineTests/SkillCurationGateTests.cs` |
| `Services/SubconsciousJobQueue.cs` | 潜意识任务队列（~29KB）；schedule_skip 按 (workspace, 5 分钟窗口) 内存聚合，窗口滚动时只写一条 `subconscious_job.schedule_skip.summary`（telemetry 为唯一 authoritative owner，不再逐事件双写 activity+metric）；明细仅保留派发/错误/状态变化。可控时钟 TimeProvider 可注入 |

目标演进：保留持久 Job 的 lease/retry/dead-letter，把 Pre-Compaction Flush、后台提取、Auto-Dream、经验转 Skill、Skill Self-Improvement 拆为事件驱动 learning stage plugins；统一经过 signal → candidate → immutable proposal → evaluation → approval/canary → activation → monitoring/rollback，详见 `Docs/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`。

## 基础设施

| 文件 | 用途 |
|------|------|
| `Infrastructure/` | 基础设施（索引、存储实现） |

## 测试

`../PuddingMemoryEngineTests/` — Library/Book/Chapter、FTS5、Skill 进化去重、ContextSegmentLedger（21/21 ✅）
`../PuddingMemoryEngineBenchmarks/` — BenchmarkDotNet 基准测试
