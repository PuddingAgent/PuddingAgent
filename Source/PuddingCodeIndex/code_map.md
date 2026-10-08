# PuddingCodeIndex CodeMAP

> 索引组件：**维护索引产物** | 契约 · 存储 · 变更捕获管线 · 调度 · 范围注册/解析 · 排除模式 · 路径标识
> 边界（ADR-089 §2.1）：本工程**不得引用** `PuddingCodeIntelligence`（编译期强制；`ProjectReference` 为空）。
> 依赖倒置：`ICodeIndexer` 定义在本工程，Roslyn/TS 等**实现留在** `PuddingCodeIntelligence`。
> 命名空间：`PuddingCodeIndex.Contracts` / `PuddingCodeIndex.Services` / `PuddingCodeIndex.Services.CodeIndex` / `PuddingCodeIndex.Storage`
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

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
| `CodeSourceUpdateContracts.cs` | 源变更到索引动作的更新计划合同 | `CodeFileSemanticChange` / `ICodeGraphDependencyQuery` / `CodeSourceUpdateAction` | — | 空符号变化列表表示无影响；计划可被 `Truncated` 截断 |
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
| `CodeSourceUpdatePlanner.cs` | 更新计划生成 | `CodeSourceUpdatePlanner` | `CodeSourceUpdateContracts.cs` | 反向依赖扩展只沿已知符号变化走，无变化则一次图都不查；退避中的路径不参与扩展；扩展有界并显式 `Truncated` |

## 服务（Services/ → `PuddingCodeIndex.Services`）

| 文件 | 用途 |
|------|------|
| `CodeIndexScheduler.cs` | 索引调度器（**U3-B1：显式驱动，无自建后台线程**；in-flight 期间到达的请求置脏并在结束后重新入队，不再丢弃）；**U3-G1：取消分支不再“什么都不写”** —— 保持 `Status=Registering`（语义不变，“仍欠一次完整运行”）的前提下盖章一句可区分的 `StatusMessage`（含 `interrupted` / `cancelled`）；**仅当该行确实处于 `Registering` 时才盖章**（在认领该行之前就被取消的运行不得被记成“被中断”，已 `Removed`/`Active` 的行也绝不能被翻回 `Registering`）。未新增状态枚举值，未动附着判据 |
| `CompositeCodeIndexer.cs` | 聚合：把「注册的 `ICodeIndexer`」变成「所有注册语言」；全量运行**至少一个语言成功即成功**（缺少可选工具链是环境事实，不得让已索引好的 scope 变 Failed），删除运行要求**所有**语言成功。**逐文件路由**（U3-B3）：`IndexFileAsync` 把文件交给唯一 owner（`SupportedExtensions` 是「这是源文件」的唯一真源），无 owner/无按文件能力 ⇒ `Failed`（调用方据此升级，合同未变）。**批量路由**（D3，2026-10-02）：`UpdateFilesAsync` 按 owner 分组，**同一语言一次调用**（批内复用一个工程快照）；无 owner ⇒ `NotApplicable`（能力路由结果，不是失败）；owner 只有逐文件能力 ⇒ 调它并映射为 `Applied`/`Retryable`；两者都没有 ⇒ `ScopeRunRequired`；语言抛错/静默丢路径按路径转成 `Retryable`（都带原因），绝不因此升级整仓 |
| `CodeFileSemanticDiff.cs` | **D3 语义差异（纯函数）**（2026-10-02）：新旧符号集 → 需要让依赖方重新绑定的符号 id（消失 ∪ 名称/种类/签名/容器变化）；**行号变化不算**；**新增符号不算**（没有旧依赖方）；首次索引（无基线）返回空（图里还没有依赖方） |
| `CodeIndexScopeRegistry.cs` | 范围注册表（幂等 ensure / 父子覆盖 / 生命周期） |
| `CodeIndexScopeResolver.cs` | 范围解析器（已注册范围优先，否则根探测 + 自动注册） |
| `CodeProjectRegistry.cs` | 项目注册（`ICodeProjectRegistry` 实现） |
| `CodePathIdentity.cs` | 路径标识（大小写比较器 / 规范化，`internal`） |
| `IndexExcludePatterns.cs` | 索引排除模式（噪声路径判定） |
| `DefaultCodeWorkspaceResolver.cs` | 工作区解析（sln/slnx/csproj 描述符，实现 `ICodeWorkspaceResolver`） |
| `DefaultProjectRootDetector.cs` | 项目根检测（向上遍历 + 标记文件，实现 `ICodeProjectRootDetector`） |

## 存储（Storage/ → `PuddingCodeIndex.Storage`）

| 文件 | 用途 |
|------|------|
| `SqliteCodeIndexStore.cs` | SQLite 索引存储（实现 `ICodeIndexStore` + `ICodeSourceMaintenanceStore` + `ICodeGraphDependencyQuery`，`sealed partial`）；**U3-B3** 的 `RemoveFilesAsync` 对一个批次只开**一个事务**（部分失败 ⇒ 一字不删）。**逐符号图删除用四条精确删除**（2026-10-02）：`RemoveSymbolGraphForFileAsync` 对每个符号分别删 CodeReferences/CodeRelations 的 Source 与 Target，而不是每表一条 `Source OR Target` —— OR 形式只落到主键作用域前缀，每个符号都重扫整个 workspace/project 分区；四条精确删除走既有的 Source/Target 索引，四次删除与文件符号删除仍在同一事务 |
| `SqliteCodeIndexStore.SourceMaintenance.cs` | **D2 源维护状态持久化**（2026-10-02，部分类）：幂等建表 `CodeSourceManifest` / `CodeSourceAppliedVersions` / `CodeIndexMaintenanceLedger` / `CodeIndexMaintenanceConsumerWatermarks` / `CodeIndexMaintenanceRetries`；manifest 单事务 upsert+删除（删除同时清消费者水位，同名新文件不继承旧状态）；账本写入整体替换消费者水位与待重试，并**拒绝回退写入**（世代更旧或同世代期望版本更旧 ⇒ 返回 false，存储保持原值）；无记录时返回空 manifest 与默认账本（含「没有基线」的 null 指纹与 `Complete=false` 行原样往返） |
| `SqliteCodeIndexStore.FileReplacement.cs` | **D4 原子文件替换**（2026-10-02，部分类）：一个批次一个事务。每个文件按**所有权**重建出边/引用（所有权 = `SourceFilePath` 属于该文件，或来源符号属于其旧符号集）；**只有**指向消失符号的入边才删除，并先报告其来源文件（依赖方，剔除本批次内文件）——指向仍存在符号的入边必须保留；符号整体替换；文件记录/符号/引用/关系与 manifest 行（指纹 + `Complete` + 各消费者水位）同事务写入；任一步失败整批回滚（旧产物与旧指纹保持完整，不提前 clear）。`IN (…)` 用 JSON 数组参数 + `json_each` 展开并分块（128），既不拼 SQL 也不撞参数上限；校验（路径一致、scope 一致、同批不重复）在事务之前 |

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
**334 用例**（2026-10-02 实测；含 3 条边界断言；U3-C 后 66 → 82，**U4-2a 后 82 → 98：+16 条检索合同契约测试**，**U3-D 后 98 → 107：+9 条常规校准 / 成本用例**，**U3-E 后 107 → 114：+7 条退避用例（含 1 条反射边界断言）**，**U3-G1 后 114 → 116：+2 条取消标记 + 对照用例**；**高磁盘读取修复 C 后 +7：4 条入边/出边/自引用/跨 scope 语义 + 3 条删除计划与 VDBE 工作量用例**；**D2 第一阶段 +25：源指纹 / 三源变更判定 / 删除可证实性 / 水位规则**；**D2 第二阶段 +15：账本捕获版本、消费者水位、扫描水位前置条件、世代作废与退避阶梯**；**D2 存储 +14：manifest/账本往返、单事务原子性、删除连带水位、回退写入拒绝**；**D2 校准 +17：元数据扫描器 8 条（忽略剪枝/不完整/触顶/规则异常）+ 校准服务 9 条（增删改候选、水位不推进、根不可用不删、能力缺失降级）**；**D4 原子替换 +10：同事务提交、所有权重建、稳定入边保留、消失目标报告依赖方、批次回滚、分块删除**；**D3 更新计划 +15：动作映射与顺序、只沿已知符号变化扩展、退避不驱动依赖方、扩展触顶 Truncated、图查询分块与双来源**；**D3 批量路由与语义差异 +19：能力路由四态（NotApplicable/Retryable/ScopeRunRequired）、同语言单次批量调用、抛错与静默丢路径按路径收容、语义差异只认消失与签名变化**），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。
