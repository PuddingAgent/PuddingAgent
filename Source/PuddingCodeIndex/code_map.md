# PuddingCodeIndex CodeMAP

> 索引组件：**维护索引产物** | 契约 · 存储 · 变更捕获管线 · 调度 · 范围注册/解析 · 排除模式 · 路径标识
> 边界（ADR-089 §2.1）：本工程**不得引用** `PuddingCodeIntelligence`（编译期强制；`ProjectReference` 为空）。
> 依赖倒置：`ICodeIndexer` 定义在本工程，Roslyn/TS 等**实现留在** `PuddingCodeIntelligence`。
> 命名空间：`PuddingCodeIndex.Contracts` / `PuddingCodeIndex.Services` / `PuddingCodeIndex.Services.CodeIndex` / `PuddingCodeIndex.Storage`
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。
> 源指纹: Contracts/**=94ee42442a77, Services/**=1cab52e9b024, Storage/**=08a238ad9cf3, *.csproj=8426a13c8553 · 条目数: 58 · 最近整理: 2026-10-08

## 契约（Contracts/ → `PuddingCodeIndex.Contracts`）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `CodeFileRecord.cs` | 单个文件在索引中的记录 | `CodeFileRecord` | — | — |
| `CodeIndexContracts.cs` | 索引状态与结果的契约类型 | `CodeIndexStatus` / `CodeIndexResult` | — | — |
| `CodeIndexScopeContracts.cs` | 索引范围的定义类型 | `ScopeSource` / `ScopeState` / `CodeIndexScope` | — | — |
| `CodeProjectContracts.cs` | 项目注册记录与增删请求的类型定义 | `CodeProjectStatus` / `CodeProjectRecord` / 增删请求 | — | — |
| `CodeSymbolContracts.cs` | 符号与搜索请求的契约类型 | `CodeSymbolKind` / `CodeSymbolRecord` / 搜索请求 | — | — |
| `CodeReferenceRecord.cs` | 引用记录的契约类型 | `CodeReferenceRecord` | — | — |
| `CodeRelationContracts.cs` | 符号关系的契约类型 | `CodeRelationKind` / `CodeRelationRecord` | — | — |
| `CodeWorkspaceDescriptor.cs` | 索引器输入的工作区描述符 | `CodeWorkspaceDescriptor` | — | — |
| `ICodeIndexStore.cs` | 索引存储端口 | `ICodeIndexStore` / `RemoveFilesAsync` | — | 按文件清除必须与文件记录、符号及其关联图/引用同事务；幂等且回报真实删除数 |
| `ICodeIndexer.cs` | 工作区级索引器端口 | `ICodeIndexer` / `IndexWorkspaceAsync` / `RemoveWorkspaceIndexAsync` | `Source/PuddingCodeIntelligence/code_map.md` | 只含全量操作；给共享端口加成员会破坏每个实现者（含组件外替身），增量能力必须另开端口 |
| `ICodeIndexFileUpdater.cs` | 按文件增量的可选能力端口 | `ICodeIndexFileUpdater` / `IndexFileAsync` | `ICodeIndexer.cs` | 实现方未提供该能力时，调用方必须升级为 scope 级重索引，不得静默跳过文件 |
| `ICodeIndexScheduler.cs` | 后台索引调度端口 | `ICodeIndexScheduler` | — | — |
| `CodeIndexBatchContracts.cs` | 语言侧按批索引的接缝契约 | `CodeIndexConsumerStatus` / `CodeFileIndexPayload` / `ICodeIndexFileBatchUpdater` | — | `NotApplicable` 是能力路由结果；`ScopeRunRequired` 只给项目级结论并交调用方决定；载荷由调用方原子提交，本层不写库 |
| `ICodeIndexSchedulerDriver.cs` | 调度器的显式泵端口 | `ICodeIndexSchedulerDriver` / `ProcessPendingAsync` | — | — |
| `ICodeIndexMaintenance.cs` | 变更驱动维护服务的生命周期与只读观测契约 | `ICodeIndexMaintenance` / `CodeIndexMaintenanceScopeStatus` | — | `LastCalibrationAtUtc` 同时是常规校准时钟的锚；`RejectedCalibrationRunCount` 只计被拒的校准 |
| `ICodeIndexScopeRegistry.cs` | 范围注册端口 | `ICodeIndexScopeRegistry` | — | — |
| `ICodeIndexScopeResolver.cs` | 范围解析端口 | `ICodeIndexScopeResolver` / `ScopeResolution` | — | — |
| `ICodeProjectRegistry.cs` | 项目注册端口 | `ICodeProjectRegistry` | — | — |
| `ICodeWorkspaceResolver.cs` | 工作区解析端口 | `ICodeWorkspaceResolver` | — | — |
| `ICodeSourceMaintenanceStore`（同 `CodeSourceManifestContracts.cs`） | 源清单与维护账本的可选持久化端口 | `LoadSourceMaintenanceAsync` / `SaveSourceManifestAsync` / `SaveMaintenanceLedgerAsync` | — | 账本写入拒绝回退；`ReplaceFilesAsync` 必须把索引结果与源指纹放在同一事务 |
| `CodeSourceScanningContracts.cs` | 磁盘枚举与校准的合同 | `CodeSourceDiskEntry` / `ICodeSourceIgnoreRules` / `ICodeSourceScanner` | `Source/PuddingPathFiltering/code_map.md` | `CodeSourceDiskEntry` 只含元数据、stat 读不到即为 null；忽略规则经注入端口提供，本组件不反向引用 `PuddingPathFiltering` |
| `ICodeProjectRootDetector.cs` | 项目根探测端口 | `ICodeProjectRootDetector` | — | — |
| `CodeSourceUpdateContracts.cs` | 来源变更到索引动作的编排合同 | `CodeFileSemanticChange` / `ICodeGraphDependencyQuery` / `CodeSourceUpdateAction` | — | 空符号变化列表表示无影响；编排结果可被 `Truncated` 截断 |
| `CodeSourceManifestContracts.cs` | 源状态与变更判定合同 | `SourceFingerprint` / `AppliedFileVersion` / `CodeSourceEntry` | — | hash 必须取自实际参与提取的那份内容；水位按消费者分别推进；`Complete=false` 的行不得当作已应用；stat 读不到不得顶替 |

## 变更捕获管线（Services/CodeIndex/ → `PuddingCodeIndex.Services.CodeIndex`）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `IndexChange.cs` | 单条文件系统变更观测 | `IndexChange` / `IndexChangeKind` | — | — |
| `CodeIndexChangeQueue.cs` | 有界变更队列 | `CodeIndexChangeQueue` / `TryPublish` | — | 队列满时 `TryPublish` 必须立即返回，不得阻塞写入方 |
| `CodeIndexScopeState.cs` | 范围脏位与版本的内存态 | `CodeIndexScopeState` | — | 不含 IO；置位后须由维护循环消费才可清除 |
| `CodeIndexWatcher.cs` | 文件系统监视器 | `CodeIndexWatcher` | — | 回调只做过滤与投递，不在回调里做 IO 或索引 |
| `CodeIndexChangeCoalescer.cs` | 变更防抖折叠 | `CodeIndexChangeCoalescer` | — | 静默 500ms / 最长 2s 到点即产出；路径数超阈值退化为整 scope reconcile |
| `CodeIndexChangeBatch.cs` | 折叠后的变更批次 | `CodeIndexChangeBatch` | — | — |
| `CodeIndexChangeWatchers.cs` | 变更源抽象与 watcher 适配工厂 | `ICodeIndexChangeWatcher` / `ICodeIndexWatcherFactory` | — | — |
| `CodeIndexMaintenanceService.cs` | 变更驱动索引的维护循环 | `CodeIndexMaintenanceService` | `CodeSourceMaintenanceCoordinator.cs` | 按文件施用，只有 reconcile、目录变更或索引器拒绝才升级 scope 级；施用失败必须置 `NeedsReconcile` 并记日志；校准未到期不发起 |
| `CodeIndexCalibrationService.cs` | 校准（mark-and-sweep） | `CodeIndexCalibrationService` / `ListFilesAsync` / `RemoveFilesAsync` | — | 根缺失或不可读必须拒绝 sweep（零移除并保持置位）；每事务 ≤256 条、每轮 ≤4096 条 |
| `CodeSourceChangeDetector.cs` | 变更判定纯逻辑 | `CodeSourceChangeDetector` / `CodeSourceChangeSet` | `CodeSourceManifestContracts.cs` | 不读文件、不访问数据库、不看时钟；hash 与实际 stat 不一致必须 Deferred；删除只能由完整且根可用的扫描得出 |
| `CodeSourceMaintenanceLedger.cs` | 待办账本（纯内存） | `CodeSourceMaintenanceLedger` | `SqliteCodeIndexStore.SourceMaintenance.cs` | 水位只在「捕获版本即当前期望 + 扫描完整 + 无未解决路径 + 无待重试」时前进且永不回退；旧世代提交返回 `StaleEpoch` |
| `FileSystemCodeSourceScanner.cs` | 默认磁盘枚举（只读元数据） | `FileSystemCodeSourceScanner` / `ICodeSourceScanner` | `CodeSourceScanningContracts.cs` | 子树读不到或触条目上限只置 `Complete=false` 加原因，绝不假装那里没有文件；不读正文、不写任何东西 |
| `CodeSourceScanService.cs` | 完整清单校准 | `CodeSourceScanService` | — | 不读正文不算 hash、不写索引、不推进扫描水位（水位只由执行层回报 `CompleteCommit` 时前进） |
| `LanguageCodeSourceConsumerInputProvider.cs` | 消费者输入指纹生产者 | `LanguageCodeSourceConsumerInputProvider` | — | 语义输入指纹只取 scope 根那一层工程/配置文件，源码正文不算；无工程文件时返回确定性标记 |
| `WorkspaceCodeSourceIgnoreRules.cs` | 忽略规则适配 | `WorkspaceCodeSourceIgnoreRules` | `Source/PuddingPathFiltering/code_map.md` | 名字级噪声必须相对仓库根判定；用绝对路径会把工作区祖先目录名当噪声而排除整棵工作区（索引悄悄变空） |
| `ICodeSourcePathProbe.cs` | 按路径观测端口 | `ICodeSourcePathProbe` | `FileSystemCodeSourceScanner.cs` | 结果必然 `Complete=false`，永远不能据此删除 |
| `CodeSourceFingerprintReader.cs` | 稳定读（读前读后 stat 一致才认） | `CodeSourceFingerprintReader` | — | 读前后 stat 不一致或长度不符必须判不稳定并弃用该轮结果；默认单文件上限 64MB |
| `CodeIndexMaintenanceScopeStatus`（`Contracts/ICodeIndexMaintenance.cs`） | 维护状态的只读视图 | `CodeIndexMaintenanceScopeStatus` | `Contracts/ICodeIndexMaintenance.cs` | — |
| `CodeSourceMaintenanceMode.cs` | 源维护驱动开关 | `CodeSourceMaintenanceMode` / `CodeIndexMaintenanceOptions` | — | 默认 `Legacy`，切换是行为变化必须显式打开；打开但零件未装配齐必须告警并退回 `Legacy` |
| `CodeSourceMaintenanceCoordinator.cs` | 源维护链协调器 | `CodeSourceMaintenanceCoordinator` | — | 处理过的路径一律清退避；失败路径写入持久待办且水位不前进（不得出现只在索引或只在待办的静默丢失）；提交前复核 stat |
| `CodeSourceUpdatePlanner.cs` | 来源变更到索引动作的编排 | `CodeSourceUpdatePlanner` | `CodeSourceUpdateContracts.cs` | 反向依赖扩展只沿已知符号变化走，无变化则一次图都不查；退避中的路径不参与扩展；扩展有界并显式 `Truncated` |

## 服务（Services/ → `PuddingCodeIndex.Services`）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `CodeIndexScheduler.cs` | 索引调度器 | `CodeIndexScheduler` | — | 无自建后台线程，由维护服务显式泵；运行期间到达的请求置脏并在结束后重新入队；取消只在行仍为 `Registering` 时盖章 `StatusMessage`，不得把已终态的行翻回 |
| `CompositeCodeIndexer.cs` | 把注册的索引器聚合成语言路由 | `CompositeCodeIndexer` / `IndexFileAsync` / `UpdateFilesAsync` | — | 全量运行至少一个语言成功即成功、删除运行要求所有语言成功；无 owner 属能力路由结果 `NotApplicable` 而非失败；逐文件与批量能力都没有时返回 `ScopeRunRequired` |
| `CodeFileSemanticDiff.cs` | 符号级语义差异（纯函数） | `CodeFileSemanticDiff` | — | 行号变化不算差异、新增符号不算差异；首次索引无基线时返回空 |
| `CodeIndexScopeRegistry.cs` | 范围注册表 | `CodeIndexScopeRegistry` | — | 注册幂等；父子范围覆盖 |
| `CodeIndexScopeResolver.cs` | 范围解析器 | `CodeIndexScopeResolver` | — | 已注册范围优先，未命中才做根探测并自动注册 |
| `CodeProjectRegistry.cs` | 项目注册实现 | `CodeProjectRegistry` | `Contracts/ICodeProjectRegistry.cs` | — |
| `CodePathIdentity.cs` | 路径标识（大小写比较与规范化） | `CodePathIdentity` | — | 仅供组件内部使用（`internal`） |
| `IndexExcludePatterns.cs` | 索引排除模式 | `IndexExcludePatterns` | — | — |
| `DefaultCodeWorkspaceResolver.cs` | 工作区解析（sln/slnx/csproj 描述符） | `DefaultCodeWorkspaceResolver` / `ICodeWorkspaceResolver` | `Contracts/ICodeWorkspaceResolver.cs` | — |
| `DefaultProjectRootDetector.cs` | 项目根检测（向上遍历 + 标记文件） | `DefaultProjectRootDetector` / `ICodeProjectRootDetector` | `Contracts/ICodeProjectRootDetector.cs` | — |

## 存储（Storage/ → `PuddingCodeIndex.Storage`）

| 文件 | 用途 | 关键符号 | 关联 | 约束 |
|------|------|------|------|------|
| `SqliteCodeIndexStore.cs` | SQLite 索引存储 | `SqliteCodeIndexStore` / `RemoveFilesAsync` | `Contracts/ICodeIndexStore.cs` | 一个批次只开一个事务，部分失败即一字不删；逐符号图删除走四条精确删除而非每表一条 `Source OR Target`（后者退化为全分区重扫） |
| `SqliteCodeIndexStore.SourceMaintenance.cs` | 源维护状态的持久化（部分类） | `SqliteCodeIndexStore` | `SqliteCodeIndexStore.cs` | manifest 单事务 upsert 加删除，删除同时清消费者水位（同名新文件不继承旧状态）；账本写入整体替换并拒绝回退写入；无记录时返回空 manifest 与默认账本 |
| `SqliteCodeIndexStore.FileReplacement.cs` | 原子文件替换（部分类） | `SqliteCodeIndexStore` | `SqliteCodeIndexStore.cs` | 一个批次一个事务，任一步失败整批回滚（旧产物与旧指纹保持完整）；只删除指向消失符号的入边并先报告其来源文件；`IN (…)` 用 JSON 数组参数加 `json_each` 分块（128），既不拼 SQL 也不撞参数上限 |

## 依赖

- 包：`Microsoft.Data.Sqlite`、`Microsoft.Extensions.Logging.Abstractions`
- **严禁**：`Microsoft.CodeAnalysis.*` / `Microsoft.Build*` / `Microsoft.Build.Locator`
- 工程引用：**无**（叶子）

## 驱动归属（ADR-089 §2，U3-B1 已落实）

- `CodeIndexScheduler` **不再自建 `Task.Run` worker**；由 `CodeIndexMaintenanceService`（单一驱动）显式泵。
- `CodeIndexMaintenanceService` **不实现 `IHostedService`**；Host 接线（DI + HostedService）属 **U3-B2**。
- 批次**默认按文件施用**（U3-B3）：删除路径落到 store，变更路径走 `ICodeIndexFileUpdater.IndexFileAsync`；只有 reconcile 请求、目录变更（其子树可能含未记录文件）、索引器拒绝或索引器**没有按文件能力**时才升级为 scope 级重索引。`ReconcileRequired` 的**自动清除**（以及「全量重索引不做 sweep」的校准）仍归 **U3-C**，本刀不动。

## 测试

**`../PuddingCodeIndexTests/`（本组件的独立测试工程 —— S2/S3 已兑现）**：只引用本工程，
**`../PuddingCodeIndexTests/`（本组件的独立测试工程 —— S2/S3 已兑现）**：只引用本工程，含 **334 用例**（2026-10-02 实测，其中 3 条边界断言）。
各阶段的用例增量与门禁记录见本文件头部链接的 changelog，此处不再累积。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。
