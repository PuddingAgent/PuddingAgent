# PuddingFullTextIndex CodeMAP

> 通用全文索引引擎 | 文件内容提取 · 搜索

## 契约（Contracts/）

| 文件 | 用途 |
|------|------|
| `IFullTextSearchEngine.cs` | 搜索引擎接口 |
| `IFileContentExtractor.cs` | 文件内容提取接口 |

## 基础设施（Infrastructure/）

| 目录 | 用途 |
|------|------|
| `Search/` | 搜索实现 |
| `Text/` | 文本处理 |

## 配置

| 文件 | 用途 |
|------|------|
| `FullTextIndexOptions.cs` | 索引选项 |

## 测试

`Source/PuddingFullTextIndexTests/` — 全文索引测试（60 项：通过 56 / 跳过 4）

## 变更（2026-09-24，ADR-089 U4-6：索引构建遍历改造）

**旧行为**：`LuceneSearchEngine.BuildIndexInternalAsync` 把白名单里 **77 个扩展名**逐个当成一个 glob 交给
`Directory.EnumerateFiles(..., SearchOption.AllDirectories)` ⇒ **同一棵树被完整遍历 77 次**；而
`FullTextIndexOptions.IsExcludedPath` 只在枚举**之后**过滤 ⇒ 被排除目录（本仓库 `.pudding` 2 万+ 文件、`bin`/`obj` 等）
仍被完整走一遍。探针实测：`Source/` 索引 462 s ≈ 77 × 单次遍历 6.6 s；根 scope 77 × 195 s ≈ 4.2 h。

**新行为**：
- `LuceneSearchEngine.EnumerateFilesPruned`（新增）：显式栈 DFS **单次遍历** + **噪声目录剪枝**
  （`ExcludedDirectoryNames` 命中即不再进入）；`DirectoryNotFoundException` / `UnauthorizedAccessException` / `IOException`
  局部化到单个目录，不再中断整次扫描；**不跳过重解析点**（与旧 `AllDirectories` 一致，登记为既有风险）。
- `LuceneSearchEngine.MatchesAnyPattern`（新增）：调用方 `filePatterns` 改用 `FileSystemName.MatchesSimpleExpression`
  在枚举后按文件名过滤（语义不变：`".cs"` / `"*.cs"` 一律按 `*.cs` 解释）；不再靠 77 次 glob 枚举表达白名单。
- 接受的文件集合与旧实现相同（配对实测：`Source/PuddingRuntime` 两侧 `filesIndexed=347` / `totalBytes=3,868,333` 完全一致）。

**实测**：`Source/` scope `BuildIndexAsync` **462,192 ms → 68,971 ms（≈6.7×）**；根 scope 由「小时级不可行」进入单次遍历量级。
⚠️ **面积提醒**：剪枝**没有语义可观测面**（落在被排除目录下的路径本来就会被 `IsExcludedPath` 拒绝），
因此它没有可变异取红的单测，证据是上面的配对与计时实测 —— 不用时间断言伪装成测试。
⚠️ Lucene **indexBytes 不作为等价性判据**（同一输入 4,374,903 vs 4,368,078；段合并/文档字段含时间戳）。

## 变更（2026-09-25，A1 全文供给协调器 · 组件内 · 未接宿主）

**目标**：组件内建立**唯一供给入口** —— 所有供给入口（宿主 bootstrap / 显式工具 / 离线 CLI）只提交任务；
协调器统一负责 **scope 规范化 / 同任务幂等合并 / 进程内单写者 / Windows 跨进程文件租约 / job 状态机 /
Plan 干跑估算（零写入）**。

**新增文件**

| 目录 | 文件 | 作用 |
|------|------|------|
| `Contracts/` | `IFullTextIndexSupplyCoordinator.cs` | 唯一供给入口：`PlanAsync` / `BuildAsync` / `GetStatusAsync` / `ListStatusAsync` / `CancelAsync` |
| `Contracts/` | `SupplyScopeRequest.cs` | 供给请求（只给**原始路径**）+ 规范化 `SupplyScope` + `SupplyRejectionReason` / `SupplyScopeRejection` |
| `Contracts/` | `SupplyRequestOutcome.cs` | 提交结果 `Started/Merged/Busy/Rejected` + 逐 scope 明细（不折叠信息） |
| `Contracts/` | `SupplyJobStatus.cs` | job 状态 + `SupplyJobState` 状态机枚举 + `SupplyJobPhases` 阶段常量 |
| `Contracts/` | `SupplyPlanResult.cs` | 干跑估算结果（预测体积 + 所用系数 + 预算判定） |
| `Contracts/` | `SupplyLeaseHolder.cs` / `SupplyLeaseDocument.cs` | 租约持有者快照 / 租约**落盘模式**（跨进程可读） |
| `Contracts/` | `IFullTextIndexSupplyInventory.cs` | 语料清点端口（+ `SupplyInventory`） |
| `Contracts/` | `IFullTextIndexBuilder.cs` | 构建端口（+ `SupplyBuildResult`，与 `FullTextIndexResult` 字段对齐） |
| `Contracts/` | `IFullTextSupplyLease.cs` | 跨进程租约端口（`SupplyLeaseOwner` / `SupplyLease` / acquire 结果） |
| 根 | `SupplyCoordinatorOptions.cs` | 可注入策略：预算 1 GiB / 终态保留 32 / 最小重建间隔 12h / 续期间隔 40s |
| `Infrastructure/Supply/` | `FullTextIndexSupplyCoordinator.cs` | 协调器实现：每 scope 闸门 + 幂等合并 + 租约 + 状态机 + 后台执行与续期 |
| `Infrastructure/Supply/` | `SupplyJobStore.cs` | job 存储：非法流转留痕（不静默）、终态历史有界淘汰（默认 32） |
| `Infrastructure/Supply/` | `SupplyJobStateMachine.cs` | **唯一**合法流转表 |
| `Infrastructure/Supply/` | `SupplyScopeNormalizer.cs` | 规范化 + 逐条拒绝（空/相对/不存在/非目录/重复/嵌套两方向） |
| `Infrastructure/Supply/` | `FileSupplyLease.cs` | 文件租约 `<IndexRoot>/.supply-leases/<sha256(scopeKey)>.json`；`FileShare.None` 临界区 + 心跳过期接管（默认 2 分钟，可注入） |
| `Infrastructure/Supply/` | `FileSystemSupplyInventory.cs` | 只读清点，复用 `IsIndexableExtension` / `IsExcludedPath` / `PathNoiseRules` 剪枝（**单次 DFS**，与引擎口径一致） |
| `Infrastructure/Supply/` | `FullTextSearchEngineIndexBuilder.cs` | 薄适配器：转发 `IFullTextSearchEngine.BuildIndexAsync`（**staging / 预算硬限 / 原子切换属 A2**） |

**实测**：`dotnet build Source/PuddingFullTextIndex -c Release` **0 警告 0 错误**；`PuddingFullTextIndexTests`
**104 项（通过 100 / 跳过 4 / 失败 0）**，其中新增 44 项；M1（去进程内单写者锁）/ M2（永不判租约过期）/
M3（Plan 顺带写租约）三个变异均**先红后绿**（详见 `temp/A1-REPORT.md` 与 `temp/a1-evidence/`）。

**边界**：本刀**未接宿主**（S5/A4）、**未调真实 Lucene**（测试全用替身）、不引用 Host/Agent/Runtime/Platform
（引用仍只有 `PuddingPathFiltering` 一条）；测试全部使用 `Path.GetTempPath()` 下的临时目录，
**未触碰** `D:\data\fulltext-index`。仍缺 O(1) 准确预算执行与原子切换（A2）。

## 变更（2026-09-25，A2a staged 构建 + 预算硬限 + 原子切换 + reader 失效 · 组件内 · 未接宿主）

**目标**：把「供给」从**直接写 live 索引**升级为**安全供给** —— 先构建到 **staging**，用**配置集合总预算**做**硬限**，
通过后**原子切换**到 live；预算不足或切换失败 ⇒ **live 一字节不动、旧索引仍可查**；并修掉「切换后 reader 陈旧」这个已知缺陷。

**新增文件**

| 目录 | 文件 | 作用 |
|------|------|------|
| `Contracts/` | `SupplySwapReport.cs` | 切换终局快照：`SupplySwapOutcome`(`None`/`Swapped`/`RejectedOverBudget`/`RolledBack`) + R6 五项事实（`stagingBytes`/`liveBytesBefore`/`liveBytesAfter`/`budgetBytes`/`outcome`）+ 清理条目与清理错误 |
| `Contracts/` | `IFullTextIndexRootedEngine.cs` | 「绑定单一索引根」的引擎接缝：`ResolveIndexDirectory`（目录名哈希的**单一真源**，禁止在供给层复刻）+ `InvalidateScope`（reader 缓存显式失效） |
| `Contracts/` | `IFullTextIndexLiveUsage.cs` | live 用量实测数据源（Plan 与构建**同口径**的 live 侧） |
| `Infrastructure/Supply/` | `StagedFullTextIndexBuilder.cs` | **安全供给 builder**：残留清理 → live 基线 → 预检 → staging 构建 → 实测硬限 → 原子切换（含回滚）→ 切换后失效与残留清理；实现 `IFullTextIndexLiveUsage` |
| `Infrastructure/Supply/` | `SupplyIndexDirectoryLayout.cs` | 磁盘布局**单一真源**：`.staging`/`.trash`/保留名、live 目录识别（64 位小写 hex）、字节实测、尽力删除（不抛） |
| `Infrastructure/Supply/` | `SupplyBudgetCalculator.cs` | 「配置集合总预算」**唯一判定函数** `live + incoming ≤ budget`（用减法避免 long 溢出把超限折成合规） |
| `Infrastructure/Supply/` | `IndexDirectorySwapper.cs` | 目录移动原语接缝（默认 `Directory.Move`；内部构造可注入失败以复现回滚与断言步骤顺序） |

**改动文件**

| 文件 | 改动 |
|---|---|
| `Infrastructure/Search/LuceneSearchEngine.cs` | 实现 `IFullTextIndexRootedEngine`（**显式实现**，不改 `IFullTextSearchEngine` 契约 —— 该接口被 CLI 工程实现）；新增 `internal void InvalidateScope(string)`（R4）；构建收尾处原有的内联缓存清理改为调用同一方法（单实现，行为不变） |
| `Infrastructure/Supply/FullTextIndexSupplyCoordinator.cs` | 受理时**只解析一次预算**并盖章到 scope 载荷；Discovering 后回填 `CorpusBytes`；终态消息折进 `SupplySwapReport.Describe()`；`PlanAsync.WithinBudget` 改用同一判定函数 + live 实测（不再各自比预算） |
| `Infrastructure/Supply/SupplyJobStore.cs` | `Scope` 载荷可写（`SetCorpusBytes`，身份字段 `ScopeKey`/`RootPath` 不变） |
| `Contracts/SupplyScopeRequest.cs` | `SupplyScope` 携带 job 载荷（`BudgetBytes`/`CorpusBytes`/`JobId`）—— 因为 `IFullTextIndexBuilder` 的签名被 CLI 组合根（红线不可改）冻结，无法再加参数 |
| `Contracts/IFullTextIndexBuilder.cs` | `SupplyBuildResult` **尾部追加**可选 `Swap`（CLI 的按位置构造不受影响） |
| `Contracts/SupplyPlanResult.cs` | `SupplyPlanScope` 追加 `LiveIndexBytes`（集合口径的 live 半，报表可解释） |
| `SupplyCoordinatorOptions.cs` | 新增 `UseStaging`（默认 **true** = 安全路径）与 `StaleArtifactMaxAge`（默认 24h） |

**冻结的口径与不变式**

- 判定式只有一条：**`live 全部 scope 索引字节 + 本次索引字节 ≤ 预算`**，由 `SupplyBudgetCalculator.Fits` 给出；
  Plan 报表 / 预检（预测值）/ 实测硬限（staging 实测）三处**同函数同口径**。
- 两道硬限**都在** `StagedFullTextIndexBuilder` 内：协调器不持有 `FullTextIndexOptions`（其组合根 = CLI `SupplyCliHost`，本刀红线不可改），
  live 实测与目录映射只能由**持有索引根**的 builder 提供（`IFullTextIndexLiveUsage` / `IFullTextIndexRootedEngine`）。
- 切换顺序（R3）：**失效 live reader → 旧 live 移入 `.trash` → staging 移入 live → 再失效一次 → 尽力删除 `.trash`**；
  第 3 步失败 ⇒ 把 `.trash` 副本**移回 live** 并报 `RolledBack`；`.trash` 删除失败**不**让 job 失败（live 已就位，只登记 `CleanupError`）。
- 同卷前提（R1）：staging / trash / live 全在 `<IndexRoot>` 之下 ⇒ `Directory.Move` 是重命名语义（跨卷会退化成复制+删除，不原子）。
- 残留清理（R5）只清「条目自身最后写入时间早于阈值」的（默认 24h；实测 Source scope 构建 ≈ 69 s，量级差三个数量级）。
- staging 目录已存在 ⇒ **拒绝复用/覆盖**并报错（不猜、不做破坏性清理）。

**实测（本刀）**：组件构建 **0 警告 0 错误**；`PuddingFullTextIndexTests` **123 项（通过 119 / 跳过 4 / 失败 0）**
（基线 104 ⇒ **+19** = `StagedSupplyBuilderTests` 14 + `StagedSupplyCoordinatorTests` 3 + `StagedSupplyEndToEndTests` 2）；
`PuddingFullTextIndex.Cli.Tests` **41/41**（**未改 CLI、未改任何 `*.slnx`**）；M1（去掉实测硬限）/ M2（切换前不失效 reader）/ M3（切换失败不回滚）
分别取红 **A3 / A5 / A4**，附加 M2b（前后都不失效）⇒ 真实 Lucene 端到端**陈旧 reader 继续服务旧索引**取红；
复原后 `StagedFullTextIndexBuilder.cs` blob hash 逐位相同（`033ac186e68a2aa1778e03128e3319cd2356839f`），`MUTATION_MARKER` 残留 **0**。
详见 `temp/A2a-REPORT.md` 与 `temp/a2a-evidence/`。

**边界**：本刀**未接宿主**（S5/A4）、**未改 CLI**（CLI 仍是 A1 的直写模式）、不引用 Host/Agent/Runtime/Platform（引用仍只有 `PuddingPathFiltering` 一条）；
所有构建/测试只在 `Path.GetTempPath()` 下的临时索引根进行，**未触碰** `D:\data\fulltext-index`（`.staging`/`.trash` 均不存在）。
