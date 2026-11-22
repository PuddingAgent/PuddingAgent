# PuddingFullTextIndex

基于 Lucene.Net + jieba.NET 的通用全文索引引擎：统一变更集、局部维护、索引供给与中文分词。

> 源指纹: Source/PuddingFullTextIndex/**=pending · 条目数: 62 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Contracts/IFullTextSearchEngine.cs` | 搜索与建索引端口的接口及结果 DTO | `IFullTextSearchEngine`、`FullTextSearchResult`、`FullTextSearchScope` | `LuceneSearchEngine` | 被 CLI 与多处替身共同实现，不得增成员 |
| `Contracts/IFullTextIndexRootedEngine.cs` | 带语料根参数的索引引擎端口 | `IFullTextIndexRootedEngine` | `IFullTextSearchEngine` | 同上：增成员会破坏现有实现 |
| `Contracts/IFileContentExtractor.cs` | 文件内容提取端口 | `IFileContentExtractor` | `PlainTextExtractor` | — |
| `Contracts/IFullTextIndexBuilder.cs` | 由供给结果构建索引的端口 | `IFullTextIndexBuilder`、`SupplyBuildResult` | `FullTextSearchEngineIndexBuilder` | — |
| `Contracts/IFullTextIndexFreshnessProbe.cs` | per-scope 索引新鲜度只读探针 | `IFullTextIndexFreshnessProbe` | `FullTextIndexFreshness` | 只读：不建目录、不写索引、不打开 reader；新能力走独立接缝而非改搜索接口 |
| `Contracts/FullTextIndexFreshness.cs` | 三态新鲜度读数：stamp 时刻、patterns 指纹与该 scope 索引目录 mtime | `FullTextIndexFreshness`、`FullTextIndexFreshnessState` | `IFullTextIndexFreshnessProbe` | 只上报事实、不含重建判定；读不出不得伪报时间或伪报为刚建过 |
| `Contracts/FullTextChangeSet.cs` | 三源统一变更集，watcher/补偿扫描/体检共用同一形状 | `FullTextChangeSet`、`FullTextFileChange`、`FullTextChangeKind` | `FullTextChangeSource`、`FullTextChangeCoalescer` | 三种来源以 Flags 1·2·4 编码 |
| `Contracts/IFullTextIndexMaintenanceEngine.cs` | 局部维护执行接缝与预算/结果/完整性探针 DTO | `IFullTextIndexMaintenanceEngine`、`FullTextMutationResult`、`FullTextIndexIntegrityProbe` | `LuceneFullTextIndexMaintenanceEngine` | 与搜索接缝分开，合并会把维护成员压进 CLI 实现方 |
| `Contracts/IFullTextIndexMaintenance.cs` | 维护生命周期接缝与 scope/reason/snapshot DTO | `IFullTextIndexMaintenance`、`FullTextMaintenanceScope`、`FullTextRecoveryReason` | `LuceneFullTextIndexMaintenance` | 宿主只负责起停 |
| `Contracts/IFullTextIndexLiveUsage.cs` | 上报 live 索引使用情况的端口 | `IFullTextIndexLiveUsage` | — | — |
| `Contracts/IFullTextIndexSupplyCoordinator.cs` | 供给（重建）协调端口 | `IFullTextIndexSupplyCoordinator` | `FullTextIndexSupplyCoordinator` | — |
| `Contracts/IFullTextIndexSupplyInventory.cs` | 供给侧语料与索引体积盘点端口 | `IFullTextIndexSupplyInventory`、`SupplyInventory` | `FileSystemSupplyInventory` | — |
| `Contracts/IFullTextSupplyLease.cs` | 供给侧跨进程租约端口与租约 DTO | `IFullTextSupplyLease`、`SupplyLease`、`SupplyLeaseOwner` | `FileSupplyLease` | 所有者以进程身份标识 |
| `Contracts/SupplyLeaseRole.cs` | 租约角色枚举 | `SupplyLeaseRole` | `IFullTextSupplyLease` | — |
| `Contracts/SupplyLeaseHolder.cs` | 租约持有者记录 | `SupplyLeaseHolder` | `SupplyLease` | — |
| `Contracts/SupplyLeaseDocument.cs` | 租约文件的持久化文档形状 | `SupplyLeaseDocument` | `FileSupplyLease` | — |
| `Contracts/SupplyJobStatus.cs` | 供给作业状态、阶段与状态 DTO | `SupplyJobStatus`、`SupplyJobState`、`SupplyJobPhases` | `SupplyJobStateMachine` | — |
| `Contracts/SupplyPlanResult.cs` | 供给范围与供给结果的规划 DTO | `SupplyPlanResult`、`SupplyPlanScope` | `FullTextIndexSupplyCoordinator` | — |
| `Contracts/SupplyRequestOutcome.cs` | 供给请求的逐范围结果与总体结局 | `SupplyRequestOutcome`、`SupplyScopeOutcome`、`SupplyOutcome` | `SupplyJobStore` | — |
| `Contracts/SupplyScopeRequest.cs` | 供给范围请求、规范化范围与拒绝原因 | `SupplyScopeRequest`、`SupplyScope`、`SupplyRejectionReason` | `SupplyScopeNormalizer` | — |
| `Contracts/SupplySwapReport.cs` | 索引目录切换结果报告 | `SupplySwapReport`、`SupplySwapOutcome` | `SwapRegressionGate` | — |
| `Contracts/IndexChunkDocument.cs` | 分块索引文档与分块结果 | `IndexChunkDocument`、`ChunkIndexResult` | `LuceneSearchEngine` | — |
| `Contracts/IndexDocumentProbe.cs` | 索引文档探针读数 | `IndexDocumentProbe` | `LuceneSearchEngine` | — |
| `Infrastructure/FullTextIndexPaths.cs` | 语料根与索引目录路径解析的唯一入口 | `FullTextIndexPaths` | `MaintenanceCheckpoint` | 其它组件解析路径必须经此，不得各自拼接 |
| `Infrastructure/FullTextPolicyFingerprint.cs` | `.last_indexed` 中 patterns 指纹的唯一真源 | `FullTextPolicyFingerprint.ComputePatternFingerprint` | `FullTextIndexFreshness` | 算法、大小写与截断长度不可改，否则现存索引全被判为 patterns 变化并触发全量重建 |
| `Infrastructure/Maintenance/CheckpointAdvancePolicy.cs` | 决定本轮是否推进 checkpoint 的纯策略 | `CheckpointAdvancePolicy.Decide`、`CheckpointAdvanceBlockReason` | `MaintenanceCheckpoint` | 仅应用成功且无失败、无保留旧项时推进；永久不可读文件会导致常驻饥饿 |
| `Infrastructure/Maintenance/FullTextChangeCoalescer.cs` | 变更折叠：按路径 latest-wins 并对来源做位或 | `FullTextChangeCoalescer.Coalesce`、`FullTextCoalesceResult` | `FullTextChangeSet` | 越界路径必须拒绝 |
| `Infrastructure/Maintenance/FullTextMaintenanceScopePlanner.cs` | 由请求推导维护范围的纯规划器 | `FullTextMaintenanceScopePlanner.Create` | `FullTextMaintenanceScope` | 纯逻辑，零 IO 零线程 |
| `Infrastructure/Maintenance/IResourcePressureProbe.cs` | 资源压力采样接缝与系统 CPU 默认实现 | `IResourcePressureProbe`、`ResourcePressureSample` | `LuceneFullTextIndexMaintenance` | null 表示不可采样，必须按繁忙退避；默认只采 CPU，磁盘维度未采 |
| `Infrastructure/Maintenance/IndexRootWriteGate.cs` | 进程级 index-root 写者闸门，按规范键分桶 | `IndexRootWriteGate.For` | `LuceneFullTextIndexMaintenanceEngine` | 多 scope 增量提交默认全局串行 |
| `Infrastructure/Maintenance/IndexSizeReport.cs` | 索引体积增长的机器可读报告 | `IndexSizeReport`、`IndexSizeObservation` | — | — |
| `Infrastructure/Maintenance/IndexWriteQuotaExceededException.cs` | 写入配额越界异常，携带文件名与越界字节 | `IndexWriteQuotaExceededException` | `QuotaEnforcingDirectory` | 越界即整批不提交 |
| `Infrastructure/Maintenance/LuceneFullTextIndexMaintenance.cs` | 维护循环本体：每 scope 一个 FSW、补偿扫描与体检线程 | `LuceneFullTextIndexMaintenance`、`MaintenanceDiagnostics`、`MaintenanceHealthCheckSlice` | `LuceneFullTextIndexMaintenanceEngine` | watcher 批次永不推进 checkpoint；水位线取最近一次成功补偿扫描的开始时刻 |
| `Infrastructure/Maintenance/LuceneFullTextIndexMaintenanceEngine.cs` | 真实 Lucene 局部写内核：单批提交、配额硬限与完整性探测 | `ApplyChangesAsync`、`ProbeIntegrityAsync`、`EnumerateIndexedPathsAsync` | `IFullTextIndexMaintenanceEngine` | 先提取后 delete/add；提取失败不进 delete 集合；完整性探测绝不自动重建 |
| `Infrastructure/Maintenance/MTimeComparison.cs` | mtime 判定：重叠水位线、时钟回拨与 stat 稳定性 | `MTimeComparison.ComputeEffectiveWatermark`、`MTimeCalibrationDecision` | `MaintenanceCheckpoint` | 水位线取扫描开始时刻，不得取结束时刻 |
| `Infrastructure/Maintenance/MaintenanceCheckpoint.cs` | checkpoint 模型、协议 JSON 与原子写路径解析 | `MaintenanceCheckpoint`、`MaintenanceCheckpointReadStatus` | `FullTextIndexPaths` | 路径解析只经 FullTextIndexPaths；读取 fail-closed |
| `Infrastructure/Maintenance/MaintenanceCorpusScan.cs` | 维护侧共用语料走查：单次 DFS 与噪声剪枝 | `MaintenanceCorpusScan.EnumerateFiles`、`CorpusScanResult` | `IndexedPathEntry` | `IsComplete` 是判定 Delete 候选的唯一依据 |
| `Infrastructure/Maintenance/MaintenanceOptions.cs` | 维护开关与阈值选项 | `MaintenanceOptions`、`MaintenanceOptionsValidationResult` | `LuceneFullTextIndexMaintenance` | fail-closed 校验，默认全关 |
| `Infrastructure/Maintenance/QuotaEnforcingDirectory.cs` | 写入期配额硬限的 Lucene 目录装饰器 | `QuotaEnforcingDirectory` | `IndexWriteQuotaExceededException` | 超限抛专用异常，不得静默截断 |
| `Infrastructure/Maintenance/ScopeReaderInvalidation.cs` | 查询侧 reader 失效接缝与搜索引擎适配 | `IScopeReaderInvalidation`、`SearchEngineScopeReaderInvalidation` | `LuceneSearchEngine` | 公开类型是因引擎构造函数为 public |
| `Infrastructure/Search/LuceneSearchEngine.cs` | Lucene 搜索与建索引主体，兼只读新鲜度探针 | `LuceneSearchEngine.SearchAsync`、`BuildIndexAsync`、`InvalidateScope` | `IFullTextSearchEngine`、`IFullTextIndexFreshnessProbe` | 扩展名/子目录过滤是结果后过滤，窗口须按批续扫；探针不打开 reader |
| `Infrastructure/Search/FileCandidateCollector.cs` | 搜索候选文件收集与跳过原因分类 | `FileCandidateCollector.TryCollectCandidate`、`CandidateSkipReasons` | `LuceneSearchEngine` | — |
| `Infrastructure/Search/PlainTextExtractor.cs` | 纯文本内容提取器 | `PlainTextExtractor.ExtractAsync` | `IFileContentExtractor` | — |
| `Infrastructure/Supply/FullTextIndexSupplyCoordinator.cs` | 供给协调器：规划与构建，以及状态查询和取消 | `FullTextIndexSupplyCoordinator.PlanAsync`、`BuildAsync`、`GetStatusAsync` | `IFullTextIndexSupplyCoordinator` | — |
| `Infrastructure/Supply/StagedFullTextIndexBuilder.cs` | 分阶段构建：先写暂存目录再切换 live | `StagedFullTextIndexBuilder.BuildAsync` | `IndexDirectorySwapper` | 未成功不得切换 live 目录 |
| `Infrastructure/Supply/FileSupplyLease.cs` | 基于文件的跨进程供给租约 | `FileSupplyLease.TryAcquireAsync`、`RenewAsync`、`ReleaseAsync` | `IFullTextSupplyLease` | — |
| `Infrastructure/Supply/FileSystemSupplyInventory.cs` | 文件系统语料与索引体积盘点 | `FileSystemSupplyInventory.MeasureAsync` | `IFullTextIndexSupplyInventory` | — |
| `Infrastructure/Supply/FullTextSearchEngineIndexBuilder.cs` | 把供给结果交给搜索引擎建索引的适配器 | `FullTextSearchEngineIndexBuilder.BuildAsync` | `IFullTextIndexBuilder`、`LuceneSearchEngine` | — |
| `Infrastructure/Supply/IndexDirectorySwapper.cs` | 索引目录切换接缝与目录移动实现 | `IIndexDirectorySwapper`、`DirectoryMoveSwapper` | `StagedFullTextIndexBuilder` | — |
| `Infrastructure/Supply/SupplyBudgetCalculator.cs` | 供给预算解析与容量判定 | `SupplyBudgetCalculator.ResolveEffectiveBudget`、`Fits` | `SupplyCoordinatorOptions` | — |
| `Infrastructure/Supply/SupplyIndexDirectoryLayout.cs` | 暂存与回收目录布局、体积测量与目录清理 | `SupplyIndexDirectoryLayout`、`ArtifactCleanupResult` | `StagedFullTextIndexBuilder` | live 目录名判定是唯一依据 |
| `Infrastructure/Supply/SupplyIndexSizeEstimator.cs` | 索引体积预估与供给作业命名 | `SupplyIndexSizeEstimator.PredictIndexBytes`、`SupplyJobNaming.NewJobId` | `SupplyJobStore` | — |
| `Infrastructure/Supply/SupplyJobStateMachine.cs` | 供给作业状态机：终态与合法转移判定 | `SupplyJobStateMachine.CanTransition`、`DescribeIllegalTransition` | `SupplyJobStatus` | — |
| `Infrastructure/Supply/SupplyJobStore.cs` | 供给作业的内存登记与阶段推进 | `SupplyJobStore.TryTransition`、`SnapshotAll` | `SupplyJobStateMachine` | 单进程内存态，跨重启不持久 |
| `Infrastructure/Supply/SupplyScopeNormalizer.cs` | 供给范围规范化与包含关系判定 | `SupplyScopeNormalizer.Normalize`、`IsWithinScopeKey` | `SupplyScopeRequest` | — |
| `Infrastructure/Supply/SwapRegressionGate.cs` | 索引切换前的回退判定 | `SwapRegressionGate.Evaluate`、`SwapRegressionDecision` | `SupplySwapReport` | — |
| `Infrastructure/Text/JiebaAnalyzer.cs` | 基于 jieba 的 Lucene 分析器 | `JiebaAnalyzer` | `JiebaTokenizer` | — |
| `Infrastructure/Text/JiebaTokenizer.cs` | jieba 分词器实现 | `JiebaTokenizer` | `JiebaSegmenterPool` | — |
| `Infrastructure/Text/JiebaSegmenterPool.cs` | 分词器资源池与词典/停用词加载 | `JiebaSegmenterPool` | `JiebaTokenizer` | 资源不可读须显式报错，不得静默降级为空分词 |
| `FullTextIndexOptions.cs` | 索引选项：可索引扩展名与排除路径判定 | `FullTextIndexOptions.IsIndexableExtension`、`IsExcludedPath` | — | — |
| `SupplyCoordinatorOptions.cs` | 供给协调器选项 | `SupplyCoordinatorOptions` | `FullTextIndexSupplyCoordinator` | — |
| `PuddingFullTextIndex.csproj` | 索引引擎工程，引用路径过滤工程与 Lucene/jieba 包 | — | `PuddingPathFiltering` | 分析器与 Lucene 版本即索引格式契约，替换会破坏既有索引 |
