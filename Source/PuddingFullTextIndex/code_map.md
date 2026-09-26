# PuddingFullTextIndex CodeMAP

> 通用全文索引引擎 | 文件内容提取 · 搜索

## 契约（Contracts/）

| 文件 | 用途 |
|------|------|
| `IFullTextSearchEngine.cs` | 搜索引擎接口 |
| `IFileContentExtractor.cs` | 文件内容提取接口 |
| `FullTextChangeSet.cs` | **三源统一变更集**（`FullTextChangeKind` / `FullTextChangeSource` Flags 1·2·4 / `FullTextFileChange` / `FullTextChangeSet`）—— watcher / mtime 补偿 / 体检共用同一种数据 |
| `IFullTextIndexMaintenanceEngine.cs` | **局部维护执行接缝**（`ApplyChangesAsync` / `EnumerateIndexedPathsAsync` / `ProbeIntegrityAsync`）+ 预算/结果/探针 DTO。**独立于 `IFullTextSearchEngine`**（后者被 CLI 共同实现，加成员会破坏其编译） |
| `IFullTextIndexMaintenance.cs` | **维护生命周期接缝**（`StartAsync` / `StopAsync` / `RequestRecoveryScanAsync` / `GetSnapshot`）+ scope/reason/snapshot DTO。宿主侧只负责起停 |

## 基础设施（Infrastructure/）

| 目录 | 用途 |
|------|------|
| `Search/` | 搜索实现（`LuceneSearchEngine.cs`：**S3a 仅做了最小可见性放宽** —— `AddDocument` / `ExtractContentAsync` 由 `private` 放宽为 `internal`，并新增 2 个 internal 只读访问器 `Analyzer` / `GetScopeGate`；**签名与实现逐字不变、public 成员集未变**，供局部维护内核复用同一提取路径与同一文档结构，避免两套文档结构静默漂移） |
| `Text/` | 文本处理 |
| `Maintenance/` | **局部维护（S3a/S3b/S3c）+ 纯逻辑（零 IO / 零线程）**：`MaintenanceCheckpoint.cs`（checkpoint 模型 + 协议 JSON + 路径解析，**只经 `FullTextIndexPaths`**）· `MTimeComparison.cs`（`>=` 判定 / `effective = watermark - overlap` / `ComputeNextWatermark` 取**扫描开始**时刻 / 时钟回拨判定 / stat 稳定性）· `FullTextChangeCoalescer.cs`（per-path latest-wins / `Sources` 位或 / rename 折叠 / 越界拒绝）· `MaintenanceOptions.cs`（fail-closed 校验，默认全关）· `CheckpointAdvancePolicy.cs`（**决定「本轮要不要推进 checkpoint」的纯策略接缝**：`AllowsAdvance` / `Decide`，规则 `State==Applied && FailedCount==0 && RetainedOldCount==0`；给出可区分的阻止原因；文档注释内登记了**饥饿风险**——永久不可读文件 ⇒ checkpoint 永不推进、每轮重扫但不漏文件）· `QuotaEnforcingDirectory.cs`（**S3b 写入期配额硬限**：`FilterDirectory` 子类 + `IndexOutput` 计数代理，超限抛专用异常）· `IndexSizeReport.cs`（体积增长机器可读报告）· `IndexWriteQuotaExceededException.cs`（越界异常，携带文件名/已写字节/允许增长/预算/越界量）· `IndexRootWriteGate.cs`（**S3c 进程级 index-root 写者闸门**：按索引根规范键分桶，§4.2 末条「多 scope 增量提交默认全局串行」的落地）· `ScopeReaderInvalidation.cs`（**S3c 查询侧 reader 失效接缝** `IScopeReaderInvalidation` + 转调 `LuceneSearchEngine.InvalidateScope`；public 是因为引擎构造函数是 public，C# 不允许 public 成员暴露 internal 类型）· `LuceneFullTextIndexMaintenanceEngine.cs`（**S3a/S3b/S3c 真实 Lucene 局部写内核 + path inventory + 写入期配额硬限 + 跨进程租约/全局串行/commit 后失效 reader**：`ApplyChangesAsync` 单批 `CREATE_OR_APPEND`，**内容先提取→后 delete/add**、提取失败绝不进 delete 集合、单批 `Commit`、取消/Busy/quota 超限一律不提交；`CheckpointAdvanced` 是**产物** = `CheckpointAdvancePolicy.AllowsAdvance` **且** 输入的 `RequiresCheckpointAdvance`（**显式取交集**：策略为唯一真源，输入只能否决、不能强制为真）。`ProbeIntegrityAsync` **未实现**（显式 `NotSupportedException`，属 S3d，绝不伪装 Healthy）） |
| `FullTextPolicyFingerprint.cs` | **`.last_indexed.p` patterns 指纹的唯一真源**（S1b 从 `LuceneSearchEngine` 私有方法收敛而来）：`filePatterns ?? "(default)"` → SHA256(UTF-8) → 小写 hex → **前 12 字符**。**不得**改大写 hex / 改截断长度 / 换哈希（会**静默**让全部现存 `.last_indexed` 判为「patterns 变了」⇒ 触发全量重建）；类内**不含**路径命名哈希 |

## 配置

| 文件 | 用途 |
|------|------|
| `FullTextIndexOptions.cs` | 索引选项 |

## 测试

`Source/PuddingFullTextIndexTests/` — 全文索引测试（**249 项：通过 245 / 跳过 4**；S3c 前为 238，S3b 前为 232，S3a 前为 222，S2b 前为 207，S2a 前为 192，S1b 前为 180，S1a 前基线为 146）

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
## 变更（2026-09-25，S1a：全文索引「局部维护」契约 + 纯逻辑 · 组件内 · 未接宿主）

**动机（用户裁定 2026-09-25）**：**重建 = 小概率 / 手动事件，长周期内不做自动重建**；日常改为**局部更新**，
触发源为「**FileWatcher 低延迟收集 + mtime checkpoint 补偿 + 低优先级体检**」。
**根因**：现有唯一供给路径（`Infrastructure/Supply/StagedFullTextIndexBuilder`）的 staging 根**每 job 唯一且拒绝复用**
⇒ 目标索引目录从不存在 ⇒ `LuceneSearchEngine` 的 `incremental` 判定恒 false ⇒ **每次全量重建（105 MB / ≈115 s）**，
引擎自带的按文件「删旧 + 加新」增量分支**永远走不到**。

**本切片只做契约与纯逻辑**（零 IO、零线程、不接真实 `IndexWriter`），为后续 S1b/S3 定形：

- **新增契约（3）**：`Contracts/FullTextChangeSet.cs`、`Contracts/IFullTextIndexMaintenanceEngine.cs`、
  `Contracts/IFullTextIndexMaintenance.cs`。两个接缝**均独立**，`IFullTextSearchEngine` / `IFullTextIndexRootedEngine`
  **一个成员都没加**（它们被 CLI 工程共同实现，加成员会破坏 CLI 编译）。**本片不实现这两个接口**（属 S3）。
- **新增纯逻辑（4，`Infrastructure/Maintenance/`）**：checkpoint 模型与路径解析 · mtime 比较 · 变更折叠 · options 校验。
- **关键语义（已被测试钉住，不是注释级约定）**：
  - mtime 判定用 **`>=`**（含等号）+ `effective = watermark - mtimeOverlap`（overlap 默认 2s，可注入）；
  - **checkpoint 取「扫描开始时刻」**（绝不取结束时刻 —— 否则「已枚举过之后、扫描结束之前」被写的文件**下轮永久跳过**）；
  - **mtime / watermark 读不到 ⇒ 判「需处理」**（fail-stale，绝不允许"读不到就跳过"）；
  - **时钟回拨**（`scanStart < watermark`，严格小于）⇒ 判需全范围局部校准；
  - 变更折叠：**per-path latest-wins**、`Sources` 位或合并、`rename` ⇒ `Delete(旧)+Upsert(新)`、暂时不可读 ⇒ 不产出动作 + 待重试；
  - **越界路径「拒绝并如实返回」**（`FullTextRejectedPath` + `Reason=OutsideScope`），**不静默过滤**（已用 3 种形态取红）；
  - checkpoint 路径 **只经 `FullTextIndexPaths.ResolveIndexDirectory`** 推导（不得复刻命名哈希）；`Enabled=false` ⇒ **零副作用**。
- **验证（父级独立复跑，不采信自述）**：组件构建 **0 警告 / 0 错误**；CLI 构建 **0 警告 / 0 错误**（证明契约未破坏 CLI）；
  `PuddingFullTextIndexTests` **失败 0 / 通过 176 / 跳过 4 / 总计 180**（基线 146 ⇒ +34）。
  **两条变异取红**（父级脚本自跑）：`>=`→`>` ⇒ `MTime_GreaterOrEqualBoundary_RequiresProcessing` 变红（失败 1）；
  watermark 改为扫描结束时刻 ⇒ `ScanWindow_WatermarkTakesScanStart_SoFileWrittenAfterEnumerationIsStillProcessedNextRound`
  + `CheckpointAdvance_UsesScanStartAsWatermark_AndIncrementsGeneration` 变红（失败 2）；两次复原后 blob **逐位相同**
  （`74ebb46fa0403fb1772211ac058f7c48fefa5dd9`）；`MUTATION` 残留 0（带对照组）。
- **本片未做（诚实登记）**：不实现两个接口；不写真实 checkpoint 写盘/原子替换；不写 FSW / 去抖定时器 / 有界队列 / 体检循环；
  不做时钟回拨的校准动作；**未抽取 patterns fingerprint helper** ⇒ `.last_indexed` golden 断言留 S1b（本片**未碰**
  `LuceneSearchEngine.cs`）；**未实现「状态机」**（需执行层语义才有意义，归 S3）；未做 quota 真实计量与并发/崩溃注入（S3）。
- **设计依据**：`temp/codex-plan-incremental-supply.md`（995 行，已抢救入 memory 侧同名副本）；
  任务书 `temp/s1a-maintenance-contracts-task.md`；报告 `temp/s1a-report.md`；原始证据 `temp/s1a-evidence/`。
- **⚠️ 遗留**：`Infrastructure/Supply/` 与 `Contracts/IFullTextIndexRootedEngine.cs` 等**既有多处未登记进本表**（历史欠账，非本片引入）。

## 变更（2026-09-25，S1b：patterns 指纹收敛为单一真源 + 组件边界断言 · 组件 + 测试）

**做什么**：`.last_indexed.p` 的 patterns 指纹原本是 `LuceneSearchEngine` 的**私有静态方法**（`HashPatterns`）。
本片它收敛为组件内**唯一真源** `Infrastructure/FullTextPolicyFingerprint.cs` 的 `ComputePatternFingerprint`，
`HashPatterns` **整段删除**（不是委托），调用点（原 `:349` → 现 `:340`）改为**直接调用** helper。
**全仓 `HashPatterns` 命中 = 0**；`LuceneSearchEngine.cs` numstat **1 增 / 10 删**（940 → 931 行，含随之失效的
`using System.Security.Cryptography;` / `using System.Text;`）。

**为什么**：S3 的维护层与 `policyFingerprint` 都要用同一语义；两份实现会让"patterns 是否变了"这个判定分裂。
同时该语义**极脆弱** —— 改成大写 hex、改截断长度、换哈希，都会**静默**把全部现存 `.last_indexed` 判为
「patterns 变了」⇒ 触发一次全量重建（105 MB / ≈115 s），正好是本方案要消灭的东西。因此四条规则已写死在类文档注释里。

**协议形状零变化**：`.last_indexed` 的文件名、`{"t":…,"p":…}` 字段名与顺序、`t` 序列化、写入时机**一律未动**，
并有往返测试钉住（形状整文件正则 + 字段顺序 + `p` 12 位小写 hex + 同 patterns 二次构建 `IndexedFileCount==0`，
换 patterns ⇒ `==2` 作**对照组**，防「增量恒为真」；索引根在系统 Temp 并断言不含 `D:\data`）。

**新增测试（12 条）**：8 条 golden（`null` / `"(default)"` / `*.cs` / `*.md` / `*.cs;*.md` / `*.ts` / `"(DEFAULT)"` / `""`）·
形状断言 · `.last_indexed` 往返 · 组件边界断言 2 条。golden 期望值由**父级独立手算**并与
**生产实际索引**交叉核对（根 scope `.last_indexed` 的 `p = b3ffbbff2d64` = `sha256("(default)")[..12]`），
**不是**由被测代码产出 —— 避免自证。

**组件边界断言（自 S2 提前到本片，理由：它正好守这一次抽取）**：断言组件 csproj 不存在指向
`PuddingHost` / `PuddingRuntime` / `PuddingCodeIndex` / `PuddingPlatform` / `PuddingFullTextIndex.Cli` 的
`ProjectReference`，并**必须**同时断言「确实找到 ≥1 个 ProjectReference」—— 否则输入退化为空集时会**恒真假绿**。

**验证（父级独立复跑，不采信自述）**：组件与 CLI 构建 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 188 / 跳过 4 / 总计 192**（S1b 前 180 ⇒ +12）。
**两条变异取红**（父级脚本 + TRX 机器可读计数）：`[..12]`→`[..16]` ⇒ **failed 10**（8 条 golden + 形状 + 往返）；
`?? "(default)"`→`?? ""` ⇒ **failed 2**，红的正是 `null` 入参与 `(default)` 字面量两条 —— 若测试不覆盖 `null`，
此变异会**照样绿**（那就等于没守住真正的不变量）。两次复原后 blob **逐位相同** `25239e6a34776b7e7794c92b6cb3643df8feae62`。
`MUTATION` 残留 0（带对照组 `ComputePatternFingerprint` = 16 处）。

**⚠️ 仪器教训（本次踩到，记录备查）**：
1. 父级第一次写的验收脚本用**中文正则**匹配测试汇总行，在 PowerShell 5.1 下因「无 BOM 的 UTF-8 脚本按 GBK 解码」
   直接 `ArgumentException` ⇒ **验收脚本必须纯 ASCII**，改用 TRX `Counters`（`total/executed/passed/failed`）做机器可读计数。
2. `search_grep` 在 `Source/` 全仓检索会**触顶 2000 文件枚举上限**并返回 `coverage: partial` —— 此时 **0 命中不构成证据**。
   改成按组件目录（小范围、全量覆盖）检索才得到决定性结论；同时 `code_symbol_search HashPatterns` 仍返回 1 条命中，
   但那是**代码索引的陈旧投影**（索引建于 07:48Z，本片在 15:00Z 之后）——**不是磁盘事实**。
   （顺带印证：这正是本项目「索引需要局部更新」要解决的问题本身。）
3. 子代理报告 `apply_patch` / `file_patch` 对其**多数多行 hunk** 报 `Hunk … did not match`（父级自己的 patch 未复现该现象），
   它改用**确定性 PowerShell 原地位替换**（`occurrences==1` 才写）完成变异与复原。**父级裁定：接受**，
   因为该纪律的目的（防 `Copy-Item` 造成 mtime 回退 ⇒ MSBuild 跳过重编译 ⇒ 假绿）已由更强证据满足：
   `git hash-object` 逐位相同 + 源 mtime > DLL mtime + 复原后复跑为绿。

## 变更（2026-09-25，S2a：checkpoint 推进策略接缝 + 崩溃/重放测试 · 组件 + 测试）

**为什么本片要动生产代码**（codex 的 S2 原文只写「只修改测试工程」）：S1a 只交付了 `MaintenanceCheckpoint.Advance` ——
一个**纯"推进"构造**（调用它必然产出一个新 checkpoint）。组件内**没有任何地方决定"要不要推进"**，
而 codex 给 S2 的变异目标恰恰是「在某个文件失败时仍推进 checkpoint，故障重放测试必须红」。
**决策点不存在，这条不变量就无法被测试守住** ⇒ 本片补上该接缝。它是纯逻辑、零 IO、约 163 行，现在补最便宜。

**语义完全来自方案原文，未自行扩大或缩小**：
- §2.4：`某个文件失败、其他文件成功 ⇒ 成功文件可提交，但全局 checkpoint 不推进；失败文件和成功文件下次都会重放`
  以及 `成功文件被重复处理是允许的；漏掉文件不允许。`
- §3.6 第 12 步：`若这是完整补偿轮次且所有批次成功，原子推进 checkpoint。`；末句：`取消…不写 checkpoint。`

**落成的判定（`CheckpointAdvancePolicy.cs:114-117` 逐字）**：

```csharp
return result.State == FullTextMutationState.Applied
    && result.FailedCount == 0
    && result.RetainedOldCount == 0;
```

其余 5 个终态（`PartiallyApplied` / `Rejected` / `Busy` / `Cancelled` / `Failed`）一律阻止。
`Decide` 另给 9 个 **ASCII** 阻止原因（可区分 失败 / 取消 / 预算拒绝 / 互斥 / 部分成功 / 计数矛盾 / 保留待重试）。

**为什么 `RetainedOldCount > 0` 也必须阻止推进**：watermark 取**扫描开始**时刻，下轮判据是 `mtime >= watermark - overlap`。
被"保留旧索引 + 待重试"的文件 mtime 早于新 watermark ⇒ **下轮被跳过 ⇒ 待重试永远不发生** ⇒ 索引永久停在旧内容。
「旧文档还在」**不是**可推进的理由。与"失败"同构，三条件同一方向 fail-closed。

**⚠️ 饥饿风险（已在类文档注释内显式登记，防后人"顺手优化"掉）**：若某文件**永久**不可读（长期独占锁 / 权限撤销 /
永久离线网络盘），则 checkpoint **永不推进** ⇒ 每轮重扫整个 scope（CPU/IO 与单批路径上限被反复消耗），
但**不会漏文件** —— 这是方案主动选择的 fail-closed 方向。逃生通道是体检层 `ManualRebuildRequired` / 人工重建，
**不得**用"允许推进"绕过。本片是纯策略层，**无法**区分"暂时"与"永久"不可读（入参无重试历史）。

**新增测试（15 条 / 2 个文件）**：
- `CheckpointAdvancePolicyTests.cs`（411 行）：**24 格全状态矩阵**（6 状态 × `FailedCount∈{0,>0}` × `RetainedOldCount∈{0,>0}`，
  测试内自证 `cells.Count == 24` 且断言 allowed 1 / blocked 23 ⇒ **每格都被实际执行**，不是只测代表值）·
  故障重放（3 成功 + 1 失败 ⇒ 不推进，**且下一轮 4 个文件全部重放**）· retained 特测 · 取消特测 ·
  **负向对照**（全成功 ⇒ 允许推进，防"永不推进"也能全绿的假绿）· 原因可区分 · 契约计数矛盾时 fail-closed
  且**不读回** `FullTextMutationResult.CheckpointAdvanced`（防"同一事实两个真源"）· 静态面冻结 · null 拒绝。
- `CheckpointCrashSafetyTests.cs`（392 行）：方案 §2.4 崩溃矩阵 5 行 —— 临时残留不算已提交 · 残留与正式并存只读正式 ·
  原子替换后 generation 单调且 watermark = 扫描**开始**时刻 · commit 后 checkpoint 前重放幂等且不漏文件 ·
  损坏/不可读 fail-closed 且**绝不**当作"无需维护"。

**验证（父级独立复跑，不采信自述）**：组件与 CLI 构建 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 203 / 跳过 4 / 总计 207**（S2a 前 192 ⇒ +15）。
**两条变异取红**（父级脚本 + TRX 机器可读计数，分开做两次）：M1 把判定整式换成
`return result.State != FullTextMutationState.Failed;` ⇒ **failed 8**，红名单含
`ThreeSucceededOneFailed_BlocksAdvance_AndAllFourFilesReplayNextRound`（★要求的重放用例）
与 `FullyAppliedBatch_IsTheOnlyAdvancingCase`（负向对照）；M2 删掉 `&& result.RetainedOldCount == 0`
⇒ **failed 5**，红名单含 `RetainedOld_SinglePendingRetryPath_BlocksAdvance`（★要求的 retained 用例）。
两次复原后 blob **逐位相同** `fa843bd31530c20316ee5a039b41cf5e8827e13a`。
`MUTATION` 残留 0（对照 `CheckpointAdvancePolicy` = 32 处）；S1a/S1b 的 **8 个既有文件哈希全部未变** ⇒ 零越界。

**已登记未决项（父级需在 S3 前裁定）**：① `FullTextMutationResult.CheckpointAdvanced`
字段的数据流方向（产物 or 输入）—— 本片按冻结规则只依赖 `State`/`FailedCount`/`RetainedOldCount`，
把它当**产物、不回读**；若 S3 把它当输入会出现"同一事实两个真源"。
② §3.6 第 12 步的「**且这是完整补偿轮次**」**不在本接缝范围内**（属调用方前置条件）；
若 S3 忘记取交集，会退化为"不完整轮次也推进"，本片防不住。
③ 建议 S3 在体检层加「同一路径**连续 N 轮** retained ⇒ 告警」的可观测项，否则永久不可读文件会导致长期重扫。

## 变更（2026-09-25，S2b：options 边界矩阵 + 路径越界全形态 + 折叠层矩阵 · **纯测试，零生产代码**）

**本片是 codex 方案 §6 的 S2 收尾**，刻意**只新增测试、不动生产代码**（对照 S2a 必须补 `CheckpointAdvancePolicy` 接缝）——
派发前先用 `code_outline` 勘查接缝，确认三处要测的性质都已存在公开面，因此能守住「只改测试工程」的边界。

**新增 15 条用例 / 3 个文件**：

- `MaintenanceOptionsBoundaryTests.cs`（332 行）：对着 `MaintenanceOptions` **公开的界常量**
  （`MaxQueueCapacityAllowed` / `MaxBatchPathsAllowed` / `MaxHealthCheckSliceFilesAllowed` /
  `Min·MaxRecoveryScanInterval` / `Min·MaxHealthCheckInterval` / `MaxMTimeOverlap` /
  `MaxHealthCheckSliceDelay` / `MaxPressureBackoff`）做数据驱动边界矩阵 —— 测的是**区间两端都含**
  这个**语义**、而不是当初拍的那些数字（界常量将来改了测试自动跟随）。
  **断言拒绝时点名的是哪个 `Option`**（不是只断言 `IsValid == false`，否则"任何一个选项报错"都会让断言通过）；
  **覆盖对照**（矩阵实际执行的用例数 == `cases.Count`，且与从源码收集的清单 `AreEquivalent`）；
  并在边界值上复核 `Enabled=false` **不短路数值域**。
- `MaintenancePathBoundaryTests.cs`（275 行）：26 行 / 13 形态路径越界矩阵，**每行断言 `Reason`**
  （`BlankPath` / `NoSource` / `Unnormalizable` / `OutsideScope`），**★前缀同名兄弟 6 形态专项**，
  外加分隔符/大小写等价类。行数自证；需真实目录才能判定的形态如实标"无法在本片验证"（本片零 IO）。
- `FullTextChangeCoalescerMatrixTests.cs`（454 行）：8 终态全覆盖（含 `AreEquivalent(Enum.GetNames<…>)` 覆盖对照）·
  **8 × 7 = 56 格** 来源交叉矩阵（双重自证）· 7 组位或合并且断言不漏位不添位 ·
  **强确定性**（只打乱中间事件，6 种排列 ⇒ 整个结果签名逐字符相同，专门抓"字典迭代顺序泄漏到输出序列"）·
  弱确定性（固定种子交错 12 轮）· 幂等 + 不修改输入 · 规范化后同物理路径只产 1 个动作 · 空/全拒绝输入不抛异常。

**验证（父级独立复跑，不采信自述）**：组件与 CLI 构建 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 218 / 跳过 4 / 总计 222**（S2b 前 207 ⇒ +15）。
**两条变异取红**（父级脚本 + TRX 机器可读计数，**分开做两次**）：
M1 把 `MaintenanceOptions.Validate` 里 `QueueCapacity` 的上界检查由**含端点**改成**不含端点**
（`> MaxQueueCapacityAllowed` → `>= MaxQueueCapacityAllowed`）⇒ **failed 1**，红的**恰好只有**
`OptionsBoundary_EveryThreshold_AcceptsBothEnds_RejectsOneStepBeyond_AndNamesTheOption`
⇒ off-by-one 被精确抓住且**无连带污染**；
M2 让 `FullTextChangeCoalescer.IsWithinScopeKey` **恒返回 `true`** ⇒ **failed 6**，含
★`PathMatrix_PrefixSiblings_AreNeverSwallowedAsInScope`、26 行路径矩阵、2 条 S1a 既有断言与规范化去重用例。
两次复原后两个生产文件 **`git hash-object` 逐位相同**且**与 HEAD 版本逐位相同**（`FINAL_EQ_HEAD=True`）：
`7f3fa423776dc43bb5ce91e93393d66e6e80aa86` / `1efe483ae9287baa32f7ca4c5c1b986d72928131`。
`MUTATION` 残留 0（对照组 `MaintenanceOptions` = 99 处、`IsWithinScopeKey` = 3 处 ⇒ 仪器有效）。

**⚠️ 仪器教训（本片新增，重要）**：
1. **子代理终态 `failed` ≠ 工作没做**：本次子代理报 `shell: Command timed out after 120 seconds`、
   `subagent_status=failed`、`resumable=false`，但它已把 3 个测试文件与报告全部落盘
   （15:47 / 15:49 / 15:55 UTC，之后才死在最后一步的验收上）。
   **必须先用 `git status` + 文件 hash 查磁盘再决定是否重派**，否则会白跑一遍（还计费）。
2. **给子代理的任务书必须写明：长命令一律用 `terminal_start` + `terminal_wait`，不要用 `shell`**
   （`shell` 有硬超时，跑 `dotnet test` 这类命令会中途被杀）。
3. 本次子代理**自己也修了 3 处新测试的缺陷**（首跑 `failed 3` → 终态 `failed 0`），
   并在报告中如实登记——这种"自曝中间失败"是可信信号，值得保留。

## 变更（2026-09-25，S3a：真实 Lucene 局部写内核 + path inventory · 组件内 · 未接宿主）

**本片是 codex 方案 §6 S3 的第一片**，实现 `LuceneFullTextIndexMaintenanceEngine` 的
`ApplyChangesAsync`（真实 Lucene 局部写）与 `EnumerateIndexedPathsAsync`（path inventory）。
**不接 Host、不重启宿主、零后台线程**（FSW / 补偿 / 体检组合留给 S3d）。

**§3.6 执行层单批顺序的落地映射**

| 步骤 | 本片 |
|---|---|
| ① 每 scope 进程内 gate | ✅（复用查询侧同一实例同一字典，`WaitAsync(ct)`；等待期取消 ⇒ `Cancelled`） |
| ② 获取 `FileSupplyLease` | ⛔ **留给 S3c** |
| ③ 最终 stat / 过滤 / 内容提取 | ✅（复用扫描期同一判定体 `Search/FileCandidateCollector.cs`，**不复制**白名单/噪声名单） |
| ④ **提取失败不进 delete 集合** | ✅ ★变异 M1 靶点 |
| ⑤ 单 writer `CREATE_OR_APPEND` | ✅（取锁失败单次尝试 ⇒ `Busy`，不抛不提交） |
| ⑥⑦ delete-then-add / 确认删除 | ✅ |
| ⑧ 预算硬限 | ✅ **写前预检**（`SupplyBudgetCalculator.Fits`）；**写入期实测硬限 ⇒ S3b 的 quota wrapper** |
| ⑨ 单批 `Commit()` | ✅ |
| ⑩ `InvalidateScope(scope)` | ⛔ **留给 S3c**（本类不持有查询侧 reader 缓存） |
| ⑪ 释放 writer | ✅（提交成功 ⇒ `Dispose()`；未提交 ⇒ `Rollback()`；**不重复 Dispose**） |
| ⑫ checkpoint 推进 | ✅ **只产出标志**（写盘 ⇒ S3d / S5） |

**四条额外门禁**：live 索引目录不存在 ⇒ `Rejected` 且**不创建目录**（§3.5）；超过 `budget.MaxPaths` ⇒ `Rejected`；
空变更集 ⇒ `Applied` 且不 commit；无成功项且无删除 ⇒ **不开 writer**、不 commit。

**`CheckpointAdvanced` 语义（本轮裁定 ①，已落地）**：它是**产物**，判定唯一真源是 `CheckpointAdvancePolicy`，
且与输入的 `RequiresCheckpointAdvance` **显式取交集**（裁定 ②）——策略为真源，**输入只能否决、不能强制为真**。

**对 `LuceneSearchEngine.cs` 的改动（严格受限，唯一被改的既有文件）**：`numstat 28/2` = 24 行注释 +
2 个 `internal` 只读访问器（`Analyzer` / `GetScopeGate`，键与既有 `_indexLocks` 完全一致）+
2 处可见性放宽（`AddDocument` / `ExtractContentAsync`：`private` → `internal`）。
**签名与实现逐字不变**，`IFullTextSearchEngine` / `IFullTextIndexRootedEngine` **成员集未变**（CLI 共用实现，加公开成员会破坏其编译）。
**刻意不复制**这两个成员到新类 —— 复制会让两种文档结构**静默漂移**，比可见性放宽危险得多。

**验证（父级独立复跑，不采信自述）**：组件与 CLI 构建 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 228 / 跳过 4 / 总计 232**（S3a 前 222 ⇒ +10）。
**两条变异分开取红**（父级脚本 + TRX 机器可读计数）：
M1 让**提取失败的文件仍进入 delete 集合**（破坏 §3.6 第 4 步，`var enterDeleteSet = content is not null;` → `= true;`）
⇒ **failed 1**，红的**恰好只有** `I1_ExtractionFailure_KeepsOldDocuments_AndDoesNotTouchTheIndex`；
M2 让 **quota 预检恒通过**（`budgetFits = SupplyBudgetCalculator.Fits(...)` → `= true;`）⇒ **failed 1**，
红的**恰好只有** `I5_QuotaPrecheck_RejectsWithoutCommitting_AndKeepsIndexBytesEqual`。
两次复原后引擎 `git hash-object` = `a84165ae7dd4b5bef032063fc0b55ae1e053d40b` **逐位相同**；
`MUTATION` 残留 0（对照组 `enterDeleteSet` = 2、`budgetFits` = 2 ⇒ 仪器有效）。
I6 给出**真实文件操作**的逐 path inventory 数据：`a.txt=2|b.txt=1` →（create c）`+c.txt=1` →（modify a）`a.txt=1` →
（delete b）`-b.txt` →（rename c→d）`c.txt` 消失、`d.txt=1`，路径数 `2→3→3→2→2`；非目标 path 文档数与内容命中数逐条不变。

**⚠️ 仪器教训（本片新增，重要）**：
1. **PS 5.1 的 `Get-Content`（不带 `-Encoding`）读含中文的 UTF-8 文件会错报行数**：同一文件
   `ReadAllLines` / `Get-Content -Encoding UTF8` = **655** 行，而 `Get-Content`（默认编码）= **601** 行（差 54）。
   **行数统计必须用 `[System.IO.File]::ReadAllLines(...).Length` 或显式 `-Encoding UTF8`**。
   （根因：`shell` 工具默认跑 pwsh 7（UTF-8 默认）而 `powershell -File` 是 5.1（ANSI/GBK 默认）⇒ 两套引擎结论不同时，
   先确认自己用的是哪一套，别把仪器差异当成事实变化。）
2. `terminal_wait` 的 `max_lines` 若小于当前输出行数，会**立即返回截断句柄而不阻塞** ⇒ 看起来像"任务卡住了"，
   实际只是等待没生效。等待长任务时把 `max_lines` 放大（≥200）。

**未做 / 未验证（如实登记）**：
- `ProbeIntegrityAsync` **未实现**（显式 `NotSupportedException`，**绝不伪装 Healthy**）；S3d 落地时须同步改断言。
- §4.1 的 S5 硬门禁「quota 失败后 rollback 保留旧 commit **且不突破预算**」目前只证明了**预检被拒**一侧；
  **「写入期不突破预算」需 S3b 的 quota wrapper 给出实测证据**，直写方案**尚未据此放行**。
- 「内容已缓冲 → `Commit()` 之前」的取消窗口有实现但**无确定性注入点 ⇒ 未验证**。
- 未验证：多进程 writer 真实竞争、与手动供给 CLI 的预算竞态、大语料下 `RAMBufferSizeMB=48` flush 行为与
  `EnumerateIndexedPathsAsync` 的耗时/内存。本片语料 ≤3 文件、内容 ≤2 行，**规模性结论不外推**。

## 变更（2026-09-25，S3b：写入期 quota 硬限 + 体积增长机器可读报告 · 组件内 · 未接宿主）

**本片补上 §4.1 的后半句硬门禁**。原文：`若 S3 无法证明 quota 失败后 rollback 保留旧 commit 且不突破预算，直写方案不得进入 S5`。
S3a 只证明了「**写前预检被拒**」一侧；本片证明「**写入期**（含 auto-merge 产生的段文件）也不会突破预算」。

**新增（`Infrastructure/Maintenance/`）**

| 文件 | 行数 | 作用 |
|---|---|---|
| `QuotaEnforcingDirectory.cs` | 232 | `FilterDirectory` 子类 + `IndexOutput` 计数代理：每次写出**先记账、越界即抛**（该次写出不发生），并登记创建/删除的文件名 |
| `IndexSizeReport.cs` | 246 | 体积增长**机器可读**报告（NDJSON 单行 + TSV 双形式，带往返解析）+ 观测累加器 + fail-closed 的 `WithinBudget` |
| `IndexWriteQuotaExceededException.cs` | 48 | 越界专用异常，携带文件名 / 已写字节 / 允许增长 / 预算 / 越界量 |

**接入点**：`LuceneFullTextIndexMaintenanceEngine.RunWriterSession` 的 writer 由 `FSDirectory.Open(indexPath)`
改为建在 `QuotaEnforcingDirectory` 上（`allowed = budget.RemainingBytes`），并显式使用 **`SerialMergeScheduler`**
（关键前提：**自动合并写出的新段文件必须计入同一预算**，若用后台合并调度器则合并可能在提交后才发生、无法回滚），
commit 前新增 `WaitForMerges()` + 越界判定，越界走 `catch (Exception ex) when (quotaDirectory.Violation is not null)` ⇒ `Rejected`。

**关键：S3a 已建立的门禁一条未削弱**（父级逐条 grep 实证）：`ProbeIntegrityAsync` 仍显式 `NotSupportedException`、
`CheckpointAdvancePolicy.AllowsAdvance(candidate) && changeSet.RequiresCheckpointAdvance` 交集判定原样、
「提取失败绝不进 delete 集合」的 `enterDeleteSet` 守卫原样、写前预检原样。
**`LuceneSearchEngine.cs` 本片 0 改动**（blob 仍 `beb0c4bc`）⇒ CLI 兼容性不受影响。

**验证（父级独立复跑，不采信自述）**：组件与 **CLI** 构建均 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 234 / 跳过 4 / 总计 238**（前 232 ⇒ +6）。
**两条变异分开取红，且父级脚本抓取失败消息里的真实字节数**（不是只看测试名）：
- **M3** 让包装层 `CreateOutput` **返回未包装的 `IndexOutput`**（计数失效）⇒ **failed 5**：`I8`/`I9`/`I10`/`I11` + `Quota_CountingCoversEveryWritePrimitive_NotJustWriteBytes`。
  `I8` 失败消息为 `report.BytesWrittenByWriter > 0` 失败且同报告 `Delta=2184` ⇒ **字节确实在动而计数器读到 0**
  ⇒ 证明被破坏的正是计数器，断言**非空洞**。
- **M4** 让超限路径**不回滚** ⇒ **failed 2**：`I9` 的 **`Assert.AreEqual 失败。应为 <2060>，实际为 <14002>`**
  （`bytesBefore` vs `result.IndexBytesAfter`）+ `I11` 的 `report.FilesRemovedByRollback > 0` 失败（报告 `Delta=9190` 残留）。
  ⇒ **不回滚时索引真的从 2,060 B 涨到 14,002 B**，「不突破预算」这条断言有真实字节支撑。
两次复原后两个文件 `git hash-object` **逐位相同**（`7576acb96a1efc54573651fad7e228306509fe90` /
`1d94fc31cf136b54fdc81d95f36e7a7d0095e5ec`）；`MUTATION` 残留 0（对照 `QuotaEnforcingIndexOutput`=4、`writer.Rollback`=1）。

**★ §4.1 的 S5 硬门禁是否已被证明？答：是 —— 但限定范围必须一起传播**：
单 scope · 单进程 · 无并发 reader · 语料与索引根在 `%TEMP%` · 维护 writer 用同步合并调度器。
**跨进程租约、多 scope 全局串行、reader 显式失效（S3c）未证明** ⇒ 「直写 live 可进 S5」这一**整体**结论仍取决于 S3c。

**未做 / 未验证（如实登记）**：`ProbeIntegrityAsync` 仍未实现（S3d）· 报告无生产消费端（S5 需定 sink）·
计数是**写出字节上界**（不抵扣删除/合并回收）⇒ 生产语料下的过拒率未量化 · `AddDocuments` 期间 RAM 缓冲触发 flush 的配额路径无用例 ·
合并频率/成本未建模（未固定 `TieredMergePolicy` 参数）· 大语料（>48 MB 缓冲）未测。

**⚠️ 仪器教训（本片新增）**：用 PS 5.1 读 TRX 时**中文失败消息会变乱码**（TRX 是 UTF-8、5.1 按 GBK 读），
但**其中的数字与异常类型仍可读** ⇒ 取字节级证据时要挑数字看，别因为消息乱码就放弃该证据。

## 变更（2026-09-26，S3c：跨进程租约 + 全局串行 + commit 后失效 reader + 并发可见性 · 组件内 · 未接宿主）

**本片补上引擎自己标注的两处缺口**（S3a 类注释原文）：`:27`「② 跨进程 `FileSupplyLease` —— 本片不做（S3c）」、
`:45`「⑩ `InvalidateScope(scope)` —— 本片不做（S3c）：本类不持有查询侧 reader 缓存」。
S3b 的 S5 硬门禁结论只覆盖「单 scope / 单进程 / 无并发 reader」，本片正是那之外的部分。

**新增（`Infrastructure/Maintenance/`）**

| 文件 | 行数 | 作用 |
|---|---|---|
| `IndexRootWriteGate.cs` | 47 | 进程级 index-root 写者闸门（按索引根规范键分桶）= §4.2 末条「多 scope 增量提交默认全局串行」 |
| `ScopeReaderInvalidation.cs` | 54 | 查询侧 reader 失效接缝 `IScopeReaderInvalidation` + 真实实现转调 `LuceneSearchEngine.InvalidateScope` |
| `Tests/LeaseAndVisibilityTests.cs` | 1247 | L1~L7 八个用例 + 两条**永久化探测**（见下） |

**接入点**：引擎构造签名扩为 `(LuceneSearchEngine, FullTextIndexOptions, IFullTextSupplyLease, TimeSpan leaseWaitUpperBound, IScopeReaderInvalidation)`
（全部 fail-closed 校验，**未**用「可选参数 = null 表示不取租约」这类兼容设计绕过租约）；`ApplyChangesWithReportAsync` 加 ⓪ 全局 index-root gate，
**加锁顺序固定为 全局 index-root → per-scope → 跨进程租约**（全局是唯一跨 scope 共享资源，放最外层则等待图不成环）；
写前预检在**临界区内重测 live 字节**并与调用方传入值取**更严**的一侧（`Math.Max`）；租约在**最终 stat 之前**取得，
commit 成功才失效 reader，租约在 `finally` 无条件释放。`LuceneSearchEngine.cs` 本片 **0 改动**（blob 仍 `beb0c4bc`）。

**★ 一条设计前提被实测证伪（重要，勿再沿用旧假设）**
codex §6 指定的变异原形态是「删掉 commit 后的 `InvalidateScope` ⇒ 并发 reader 最终可见性测试必须红」。
**实测结论：取不了红。** 两条永久化探测（PROBE1/PROBE2）：
- **PROBE1**：已缓存的 reader 在另一个 writer 原地 commit 后**会自刷新** —— `LuceneSearchEngine.GetOrRefreshSearcher`（`:597-647`）用
  `DirectoryReader.OpenIfChanged(existingReader)`。把失效替身设为「只计数、不转发」后搜索**仍**能看到新 commit
  （`autoRefresh=YES newHits=1 oldHits=0`）。
- **PROBE2**：缓存中的 reader **不**阻止索引目录删除（`deleteWithCachedReader=deleted-ok`）⇒ 「句柄释放」形态也观测不到失效效果。

⇒ 在「进程内原地 commit」这一形态下，显式失效对**可见性**是冗余的（它对 `LuceneSearchEngine` 仍必要，原因是**整目录替换**
（staging 切换 = 旧目录改名移走 + 新目录就位）时旧 Reader 会继续返回过期文档 —— 那条路属 **S3d（staging/切换）与 S5（接宿主）**）。
因此本片实现的显式失效是**前瞻性接线**，其必要性**尚未在本片范围内被证明**；变异按任务书裁定换成等价形态（红点 = 失效**调用计数**不变量 L6）。
**「有/无显式失效」的因果链对本进程内可见性未经证明** —— 这是 S3c 最需要被记住的一条限定，勿把「L7 绿」读成「失效调用被证明必要」。

**验证（父级独立复跑，不采信自述）**：组件与 **CLI** 构建均 **0 警告 / 0 错误**；
`PuddingFullTextIndexTests` **失败 0 / 通过 245 / 跳过 4 / 总计 249**（前 238 ⇒ +11）。
**两条变异父级亲跑取红**：
- **M5**（删 commit 后失效调用）⇒ **failed 6**（L1/L2/L3/L4/L6/Probe1，均为 `invalidation.Count` 0≠1），
  而 **`L7_OUTCOME=Passed`** —— 父级亲眼看证伪了原变异前提。
- **M6(a)**（拿不到租约也继续写）⇒ **failed 2**（L2/L6），消息 `Assert.AreEqual 失败。应为 <Busy>，实际为 <Applied>`
  （`FullTextMutationState.Busy` vs `busy.State`）⇒ 断言非空洞、有状态级证据。
复原三次 `git hash-object` = `8c18df34ba8af114d440bd1edabc493d47147a05` **逐位相同**；终态 failed=0 / passed=245 / total=249；
`MUTATION` 残留 0（对照 `IndexRootWriteGate`=3、`_readerInvalidation.InvalidateScope`=1）；`%TEMP%` 残留 `pudding-fts-s3c-*` = **0（两轮红跑之后仍为 0）**；
生产索引根 mtime 仍 `2026-09-25T07:48:46.6377737Z` 未被触碰。

**★ 父级自纠一处未证实的缺陷归因（重要）**：先前把 S3b 的 `%TEMP%` 残留（6 个 `pudding-fts-s3b-*`）归因为
「`QuotaEnforcementTests` 失败路径不清理」，但本片**试图复现时复现不出来**：把断言临时反转制造真实失败后，残留仍为 0；
用旧静默清理（`catch (IOException) {}`）复跑同一失败场景，残留**同样为 0**。⇒ **该归因未获证实**（真正的触发条件是
「删除 rollback 的那两条变异跑」这类**测试宿主异常终止**形态，而非普通断言失败）。本片已把静默清理改成**响亮失败**（隐患消除），
但不得再把「失败路径不清理」当作已确证缺陷引用。

**未做 / 未验证（如实登记）**：M6(b)「跳过取后重新 stat」**未用变异实测**（字面形态需先新增「等待前 stat 快照」这类代码，不属「删/弱化既有代码」的变异形态）
⇒ L3 对「stat 顺序倒置」的敏感度**未经变异确认** · 跨进程的全局预算竞争不在本片范围（§4.2 原文把 index-root budget gate 指给协调器层，本片只做维护路径引擎内）·
`ProbeIntegrityAsync` 仍未实现（S3d）· 多 scope 全局限定于**同进程同一索引根** · 一次**未取红**的变异 Mq（提交前越界判定加条件）已如实登记并解释（越界在写入原语处已抛出，提交前判定只是第二道守卫 ⇒ 行为等价）。
