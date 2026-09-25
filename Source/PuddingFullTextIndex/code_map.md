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

`Source/PuddingFullTextIndexTests/` — 全文索引测试（146 项：通过 142 / 跳过 4）

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

## 变更（2026-09-25，A22a 暂存切换的「回归闸门」· 组件内 · 未接宿主）

**目标**：坏结果不再能被提升为 live。体积合规**不等于**内容可信 —— 2026-09-25 生产事故：仓库根 scope 的 ~98 MB
live 满索引被一次只含 **0/99 文档**的构建通过 A2a 的原子切换静默替换（同命令 4 次中 2 次得 99/0 文件却全报 `Succeeded`），
原有两道预算闸门**只看字节**。本刀在「实测体积硬限」与「原子切换」之间插入一道**只看文档数**的 fail-closed 闸门。

**新增成员（一句话一条）**

| 位置 | 新增 | 一句话 |
|---|---|---|
| `Contracts/IndexDocumentProbe.cs` | `readonly record struct IndexDocumentProbe(bool Exists, long? Documents)` | 文档数探针的三态结果：目录不存在 `Exists=false`、存在且可读给出真实篇数（`0` 合法）、存在但读不出为 `Documents=null`（**不得伪报 0**）。 |
| `Contracts/IFullTextIndexRootedEngine.cs` | `IndexDocumentProbe ProbeDocuments(string corpusRootPath)` | 新增只读文档数探针接缝（路径复用 `ResolveIndexDirectory` 单一真源，不复刻哈希规则）；**不动** `IFullTextSearchEngine`（CLI 组合根冻结）。 |
| `Infrastructure/Supply/SwapRegressionGate.cs` | `SwapRegressionGate.Evaluate` / `DescribeFacts` | 回归闸门的**唯一判定函数**（纯函数、不抛异常、不触盘）：G1 staging 不可读 / G2 staging 0 文档 / G3 `stagingDocs < liveDocs × 阈值` / G4 live 存在但读不出 —— 任一条命中即拒绝。 |
| `Contracts/SupplySwapReport.cs` | `RejectedSuspiciousRegression`（outcome = 4） | 新终态：回归闸门拒绝 ⇒ 未切 live（live 一字节未动），staging 已清。 |
| `Contracts/SupplySwapReport.cs` | `LiveDocsBefore` / `StagingDocs` / `RegressionRatio` / `RegressionVerdict` | 对外可见的文档数事实（`Describe()` 一律写进终态消息；不可计算时写 `<null>`，不把「无意义」伪装成 0）。 |
| `SupplyCoordinatorOptions.cs` | `MinStagingToLiveDocRatio`（默认 `0.5`） | 回归闸门阈值；非法值（`NaN`/`≤0`/`>1`）由 builder **回落默认值并告警**（`Trace`），**绝不**按 0 放行。 |
| `Infrastructure/Supply/StagedFullTextIndexBuilder.cs` | 步骤 **⑥-bis** | 回归闸门插入点：实测体积硬限之后、原子切换之前；拒绝路径 = 清 staging、**不**建 `.trash`、**不**失效 reader 缓存、live 一字节不动。 |
| `Infrastructure/Search/LuceneSearchEngine.cs` | `IFullTextIndexRootedEngine.ProbeDocuments` 实现 | `DirectoryReader.Open(FSDirectory.Open(indexDir)).NumDocs`；目录不存在与「存在但读不出」严格区分。 |

**闸门口径**：`live 不存在`（首次构建）⇒ **放行**（G2 仍生效）；`describe()` 与终态消息均携带
`stagingDocs / liveDocs / ratio / 判定式` 四个可判定量。

**实测（本刀）**：组件构建 **0 警告 0 错误**；`PuddingFullTextIndexTests` **134 项（通过 130 / 跳过 4 / 失败 0）**（基线 123 ⇒ **+11**）：
`StagedSupplyRegressionGateTests` 11 项（A1 / A1b / A2 / A3 / A4 / **A5 真 Lucene** / A6 / A7 / G1 / G1b / G4）；
`PuddingFullTextIndex.Cli.Tests` **41/41**（未改 CLI）；M1（删 G2）/ M2（G3 比较写反）/ M3（live 不存在也拒）分别取红 **A1+A1b+A5+A6 / A2+A3+A6+A7 / A4**，复原后 blob hash 逐位相同，`MUTATION` 残留 0。
详见 `temp/A22A-REPORT.md` 与 `temp/a22a-evidence/`。

## 变更（2026-09-25，A22b 扫描期逐文件隔离 · 组件内 · 未接宿主）

**目标**：消除「**整轮枚举被静默丢弃且 `Success=true`**」这条路径。缺陷本体：`LuceneSearchEngine.BuildIndexInternalAsync`
的扫描块把 `catch (DirectoryNotFoundException)` / `catch (UnauthorizedAccessException)` 挂在 **foreach 之外**（连同仅为
承载它的 `foreach (var _ in new[] { "*" })` 单次包装）⇒ 扫描途中任一文件抛这两类异常，被丢弃的是**整轮枚举的剩余部分**
（生产形态 99/4510、另一轮 0/4510），而构建照常返回 `Success=true`；`IOException` 不在 catch 列表里 ⇒ 冒到方法级 catch 后
`IndexedFileCount` 被硬写成 `0`（丢失「已经收集了多少」）。A22a 的闸门只能拦住这种结果被提升为 live，**不能消除结果本身**。

**新增文件**（`Infrastructure/Search/FileCandidateCollector.cs`）

| 成员 | 一句话 |
|---|---|
| `FileCandidateCollector.TryCollectCandidate(file, scanRoot, options, patterns, out CandidateEntry, out string? skipReason)` | 逐文件判定体（扩展名 → 调用方 pattern → 排除路径 → 空文件/超限）抽成 **internal 纯函数**：文件系统异常（`IOException` / `UnauthorizedAccessException` / `SecurityException`）**就地吃掉**并返回 `false` + `error:` 前缀原因，`OperationCanceledException` **原样外抛**。 |
| `TryCollectCandidate(..., CancellationToken, out ..., out ...)` 重载 | 引擎扫描循环的入口：取消检查落在本类边界内、且**在吞异常的 catch 之外**（A4 锁死）。 |
| `CandidateEntry` / `CandidateSkipReasons` / `IsErrorSkip` | 候选条目；策略原因常量（`not-indexable-extension` / `pattern-mismatch` / `excluded-path` / `empty-file` / `too-large`）+ 异常原因前缀 `error:`；`IsErrorSkip` 让调用方区分「策略拒绝」与「坏文件」。 |

**改动**

| 文件 | 改动 |
|---|---|
| `Infrastructure/Search/LuceneSearchEngine.cs` | 扫描块改用 helper，**删除**整轮级别的两个 catch 与 `foreach (var _ in ...)` 包装；新增 `enumeratedCount` / `skippedByError`；扫描期非取消异常 ⇒ 扫描级 catch **明确失败**（`Success=false` + 异常类型 + 「已枚举/已收集」计数 + 「扫描被中断」字样）；方法级 catch 同样带上计数（不再硬写 0）；写入循环两个既有逐文件 catch 各 `skippedByError++`；`MatchesAnyPattern` 由 `private` 提升为 `internal`（供 helper 复用，glob 保持单一实现）。 |
| `Contracts/IFullTextSearchEngine.cs` | `FullTextIndexResult` **末尾追加** `int SkippedByError = 0`（带默认值 ⇒ CLI 与既有构造点不改一行）；`Success=true && SkippedByError>0` 时 `Error` 给出「部分构建」文本，终态不表现成一切正常。 |

**实测（本刀）**：组件构建 **0 警告 0 错误**；`PuddingFullTextIndexTests` **142 项（通过 138 / 跳过 4 / 失败 0）**（基线 134 ⇒ **+8**，
全部在 `ScanPerFileIsolationTests`：A1~A8）；`PuddingFullTextIndex.Cli.Tests` **41/41**（**未改 CLI 一行** ⇒ R3 未被撤回）；
M1（去掉 helper 的 `IOException` catch）/ M2（取消检查被吞）/ M3（`SkippedByError` 恒 0）分别取红 **A2+A8 / A4 / A6+A8**；
复原后 `LuceneSearchEngine.cs` blob `eb448a554c855fec2bf20b0e71f5b02d539c4903`、`FileCandidateCollector.cs` blob
`c4996a8f6df380d0237357358e24bd6eef7e22b2` **逐位相同**，`MUTATION` 残留 **0**。
详见 `temp/A22B-REPORT.md` 与 `temp/a22b-evidence/`。

⚠️ **仪器坑（本刀实证）**：用「pristine 副本 + `Copy-Item` 覆盖」复原源码时，副本保留的是变异**前**的旧时间戳 ⇒ 源文件比已编译
产物更旧，MSBuild 增量判定**跳过重编译**，于是「复原后应绿」的那一次跑其实跑的是变异后的旧 DLL（本刀首次复原即踩中）。
复原必须刷新 `LastWriteTimeUtc`；判据是「blob hash 相同」**且**「重编译确实发生」。

⚠️ **登记留白**：① `Infrastructure/Supply/FileSystemSupplyInventory.cs:68-84` 仍在**自己复刻**同一套判定（扩展名/排除/体积），
本刀按「禁止顺手重构」未动它（它返回的是清点口径而非候选条目）；② 扫描级 catch 覆盖的是「逐文件判定」与「枚举」共用的异常出口，
枚举器内部若抛非 `DirectoryNotFoundException`/`UnauthorizedAccessException`/`IOException` 的异常同样落到它 —— 这条路径有 A7 覆盖，
但**枚举器自身**的注入端口不存在，测试用的是注入到扩展名白名单集合（见报告「仪器与注入点」）。

## 变更（2026-09-25，A19：scope → 索引目录映射单一真源化 · 组件 + CLI）

**目标**：把「语料根 → 索引目录名」的命名哈希收敛到**唯一实现**，让 CLI 不再复刻它，且 **CLI 输出逐字不变**。

**新增文件**

| 目录 | 文件 | 作用 |
|------|------|------|
| `Infrastructure/` | `FullTextIndexPaths.cs` | **命名哈希单一真源**（`public static`）：`NormalizeCorpusRoot` = `GetFullPath` → `TrimEnd(两个分隔符)` → `ToUpperInvariant`；`ResolveIndexDirectory(indexRoot, corpusRoot)` = `Path.Combine(indexRoot, sha256 小写 hex)`。规则与 `GetIndexDirectoryPath` **逐字等价**。 |

**改动文件**

| 文件 | 改动 |
|---|---|
| `Infrastructure/Search/LuceneSearchEngine.cs` | `GetIndexDirectoryPath` 改为**委托** `FullTextIndexPaths.ResolveIndexDirectory`（`internal` + 签名不变，10 处调用点一行未动）；`ResolveIndexDirectory` / `ProbeDocuments` 仍经它 ⇒ 自动统一。 |
| `Contracts/IFullTextIndexRootedEngine.cs` | 文档同步：单一真源指向 `FullTextIndexPaths.ResolveIndexDirectory`；删除「CLI 侧 `SupplyScopeMirror` 登记为后续切片」的旧注（本刀已删该复刻）。 |

⚠️ **与 `SupplyScopeNormalizer` 是两套口径，不可合并**：本规则**不**保住盘根（`C:\` 的哈希输入是 `C:`）、**大写**、不额外归一分隔符；
scope 键相反（保住盘根、不变文化小写、`/`→`\`），且服务租约/去重/幂等键。`FullTextIndexPathsTests` 用 `A3_..._Does_Not_Preserve_The_Drive_Root` 冻结这条差异。

**实测（本刀）**：组件构建 / CLI 构建 **0 警告 0 错误**；`PuddingFullTextIndexTests` **146 项（通过 142 / 跳过 4 / 失败 0）**（基线 142 ⇒ **+4**，全部在新文件 `FullTextIndexPathsTests.cs`）；
`PuddingFullTextIndex.Cli.Tests` **45/45**（基线 41 ⇒ **+4**，全部在新文件 `A19SingleSourceTests.cs`）。
四方（组件 helper / 引擎 `ResolveIndexDirectory` / 引擎 `ProbeDocuments` 解析出的目录 / 旧镜像金标准）在 6 类边界输入下逐字节相同；
CLI `status` 打印的 `scopeKey` / `indexDirectory` 改动前后**逐字节相同**（`DIFF-COUNT=0`；仅剔除 `exe-sha256` 与 `owner=MSI#<pid>` 两类非确定性行）；
M1（`ToUpperInvariant`→`ToLowerInvariant`）/ M2（去掉 `TrimEnd`）/ M3（删调用点但把复刻留回 CLI 侧）分别取红；复原后 blob 逐位相同、`MUTATION` 残留 **0**（含对照组）。
详见 `temp/A19-REPORT.md` 与 `temp/a19-evidence/`。