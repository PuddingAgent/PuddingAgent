# PuddingCodeIndex CodeMAP

> 索引组件：**维护索引产物** | 契约 · 存储 · 变更捕获管线 · 调度 · 范围注册/解析 · 排除模式 · 路径标识
> 边界（ADR-089 §2.1）：本工程**不得引用** `PuddingCodeIntelligence`（编译期强制；`ProjectReference` 为空）。
> 依赖倒置：`ICodeIndexer` 定义在本工程，Roslyn/TS 等**实现留在** `PuddingCodeIntelligence`。
> 命名空间：`PuddingCodeIndex.Contracts` / `PuddingCodeIndex.Services` / `PuddingCodeIndex.Services.CodeIndex` / `PuddingCodeIndex.Storage`
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIndex-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

## 契约（Contracts/ → `PuddingCodeIndex.Contracts`）

| 文件 | 用途 |
|------|------|
| `CodeFileRecord.cs` | 文件记录 |
| `CodeIndexContracts.cs` | `CodeIndexStatus` / `CodeIndexResult` |
| `CodeIndexScopeContracts.cs` | `ScopeSource` / `ScopeState` / `CodeIndexScope` |
| `CodeProjectContracts.cs` | `CodeProjectStatus` / `CodeProjectRecord` / Add·Remove 请求 |
| `CodeSymbolContracts.cs` | `CodeSymbolKind` / `CodeSymbolRecord` / 搜索请求 / 详情 |
| `CodeReferenceRecord.cs` | 引用记录 |
| `CodeRelationContracts.cs` | `CodeRelationKind` / `CodeRelationRecord` |
| `CodeWorkspaceDescriptor.cs` | 工作区描述符（索引器输入） |
| `ICodeIndexStore.cs` | 索引存储端口；**U3-B3** 增 `RemoveFilesAsync`（按文件事务性清除：文件记录 + 符号 + 其关联图/引用；幂等，返回真正删除的文件数） |
| `ICodeIndexer.cs` | 索引器端口（实现在 Intelligence）；**只含全量** `IndexWorkspaceAsync` / `RemoveWorkspaceIndexAsync` |
| `ICodeIndexFileUpdater.cs` | **U3-B3** 按文件增量**可选能力端口**（`IndexFileAsync(descriptor, filePath)`）：故意不放进 `ICodeIndexer` —— 给全量端口加成员会破坏**每一个**实现者（实测 2026-09-24：成员版直接弄坏了禁写路径 `Tests/PuddingHost.Tests` 的替身）；不实现该能力 ⇒ 调用方升级为 scope 级重索引 |
| `ICodeIndexScheduler.cs` | 后台调度端口（成员语义未变） |
| `CodeIndexBatchContracts.cs` | **D3/D4 语言侧批量接缝**（2026-10-02）：`CodeIndexConsumerStatus` 四态（`Applied` / **`NotApplicable`＝能力路由结果** / `Retryable`＝按路径退避 / `ScopeRunRequired`＝只能给项目级结论，交调用方决定）；`CodeFileIndexPayload`（提取结果，**由调用方原子提交**，不在这里写库）；`CodeFileIndexOutcome`；`CodeIndexBatchContext`（配置/策略指纹 + 世代号，批内复用一个工程快照的键）；`CodeIndexFileBatchResult`（含 `SessionKey`，供「批内是否重复打开工程」诊断）；`ICodeIndexFileBatchUpdater` |
| `ICodeIndexSchedulerDriver.cs` | **U3-B1** 显式泵端口（`ProcessPendingAsync` + 每 scope `Desired/Committed` 水位） |
| `ICodeIndexMaintenance.cs` | **U3-B1** 变更驱动维护服务的生命周期/只读观测契约 + `CodeIndexMaintenanceScopeStatus`（**U3-B3** 状态增 `RemovedFileCount` / `IncrementallyIndexedFileCount` / `ScopeEscalationCount`；**U3-C** 再增 `SweptFileCount` / `CalibrationRunCount` / `RejectedCalibrationRunCount` / `LastCalibrationAtUtc`；**U3-D** `CalibrationRunCount` 计入常规（周期）校准；`LastCalibrationAtUtc` 同时是常规时钟的锚） |
| `ICodeIndexScopeRegistry.cs` | 范围注册端口 |
| `ICodeIndexScopeResolver.cs` | 范围解析端口 + `ScopeResolution` |
| `ICodeProjectRegistry.cs` | 项目注册端口 |
| `ICodeWorkspaceResolver.cs` | 工作区解析端口 |
| `ICodeSourceMaintenanceStore`（同 `CodeSourceManifestContracts.cs`） | **可选持久化/维护写能力端口**（2026-10-02）：`LoadSourceMaintenanceAsync` / `SaveSourceManifestAsync`（单事务 upsert+删除，删除同时清消费者水位）/ `SaveMaintenanceLedgerAsync`（整体替换消费者水位与待重试，**拒绝回退写入**）/ `ReplaceFilesAsync`（**原子文件替换**：索引结果 + 源指纹同事务；见下）。故意不加进 `ICodeIndexStore`（给共享端口加成员会破坏每一个实现者，含组件外替身） |
| `CodeSourceScanningContracts.cs` | **D2 磁盘枚举与校准合同**（2026-10-02）：`CodeSourceDiskEntry`（只含元数据，stat 读不到就是 null）、`ICodeSourceIgnoreRules`（忽略规则**注入端口**，组件不反向引用 `PuddingPathFiltering`）、`ICodeSourceScanner` + `CodeSourceScanOutcome`（`RootUsable` / `Complete` / 原因）、`CodeSourceScanRun`（变更集 + 捕获版本 + 扫描开始时刻 + 能力缺失）、`CodeSourceScanOptions`、`CodeSourceScanReasons` |
| `ICodeProjectRootDetector.cs` | 项目根探测端口 |
| `CodeSourceUpdateContracts.cs` | **D3 更新计划合同**（2026-10-02）：`CodeFileSemanticChange`（真正提取出的符号变化，空列表=无影响）、`ICodeGraphDependencyQuery`（反向依赖查询端口；实现留在存储）、`CodeSourceUpdateAction`（Extract/RebindOnly/Delete/RetryLater）、`CodeSourceUpdateItem` / `CodeSourceUpdatePlan`（计数 + `Truncated`）、`CodeSourceUpdateReasons` |
| `CodeSourceManifestContracts.cs` | **D2 源状态与变更判定合同**（2026-10-02）：`SourceFingerprint`（mtime/length/内容 hash，hash 必须来自实际参与提取的那份内容）、`AppliedFileVersion`（**每个消费者分别推进**的水位 + 解析器策略/语义输入指纹）、`CodeSourceEntry`（持久 manifest 行，`Complete=false` 不得当作已应用）、`CodeSourceObservation`（stat 读不到就是 null，不得顶替）、`CodeConsumerInputFingerprint`、`CodeSourceAction`（Reuse / RefreshFingerprintOnly / RebindConsumers / ReindexContent / Delete / Deferred）、`CodeSourceChangeSource` 三源位标、`CodeSourceChange` / `CodeSourceScanRequest` / `CodeSourceChangeSet`（含扫描完整性、建议水位与各动作计数） |

## 变更捕获管线（Services/CodeIndex/ → `PuddingCodeIndex.Services.CodeIndex`）

| 文件 | 用途 |
|------|------|
| `IndexChange.cs` | 单条文件系统变更观测（`IndexChangeKind`） |
| `CodeIndexChangeQueue.cs` | 有界队列（容量 8192，`TryPublish` 不阻塞） |
| `CodeIndexScopeState.cs` | 范围状态（dirty/version/reconcile，无 IO）；**U3-B3** 增 reconcile 原因 `BatchApplicationFailed`；**U3-C** 增 `CalibrationRootUnavailable` / `CalibrationFailed`；**U3-D** 增 `CalibrationTruncated`（常规路径被 ceiling 截断也置位） |
| `CodeIndexWatcher.cs` | 文件系统监视器（64KB 缓冲，回调只过滤 + TryPublish） |
| `CodeIndexChangeCoalescer.cs` | 防抖折叠（静默 500ms / 最长 2s，2 万路径 → reconcile） |
| `CodeIndexChangeBatch.cs` | 折叠产物（重读集合 / 移除集合 / reconcile 标记） |
| `CodeIndexChangeWatchers.cs` | **U3-B1** 变更源抽象（`ICodeIndexChangeWatcher` / `ICodeIndexWatcherFactory`）+ 真实 watcher 适配工厂 |
| `CodeIndexMaintenanceService.cs` | **U3-B1/U3-B3** 变更→索引的单一驱动（消费批次、置脏补跑、有界停止）。**U3-B3 按文件施用**：`PathsToRemove` → store 真删除；`PathsToReindex` → `ICodeIndexFileUpdater.IndexFileAsync` 逐文件（索引器无该能力则同样升级）；仅 reconcile / 目录变更 / 索引器拒绝才升级为 scope 级重索引；批次施用失败 ⇒ 标 `NeedsReconcile` + 记错误日志（不静默丢弃）。**U3-D 常规校准**：驱动步末尾的校准由 `TryBeginCalibration` 逐 scope 判due —— 被标位的 scope（U3-C，首次尝试在下一步、重试节流 `DefaultCalibrationInterval` 60s）**或**自有常规周期到期的 scope（`DefaultCalibrationPeriod` 15min，按**上次完成**计时，首次以挂载时刻为锚）。未到期 ⇒ **一次校准都不发起**（每日 200ms 步不会变成扫盘）；被拒/被截断 ⇒ **保持或置位** `NeedsReconcile`。**U3-E 重试退避**：被标位 scope 的重试间隔改为**指数阶梯** —— 以 `DefaultCalibrationInterval`(60s) 为底、**第二次及以后的连续失败**逐次翻倍（60s/2m/4m/8m/16m），封顶到组件常量 `DefaultCalibrationBackoffMax`(**30min**)；**任何一次“读到了根”的 sweep（完成或截断）立刻把档位复位到 0**；15min 常规钟与“首次尝试在下一步 / 单次失败仍 60s”**逐字未变**（阶梯只判“重复失败”，且与常规钟不叠加：`IsCalibrationDue` 的 if/else 二者永不同时参与） |
| `CodeIndexCalibrationService.cs` | **U3-C 校准（mark-and-sweep）**：取 scope 已索引路径集合（`ListFilesAsync`），逐条判磁盘存在性，对"已消失"的调用 `RemoveFilesAsync`（只删索引行）；**根目录缺失/不可读 ⇒ 拒绝 sweep**（零移除 + 保持置位）；宽限窗口内被变更管线刚观测过的路径豁免；每事务 ≤256 条、每轮 ≤4096 条，可取消。**D 后续阶段**：扩展为「磁盘清单 vs manifest 差异扫描」（发现漏通知的新增文件），并接入持久账本 |
| `CodeSourceChangeDetector.cs` | **D2 变更判定纯逻辑**（2026-10-02）：三源提示（watcher / mtime·stat 扫描 / 深度核验）＋ 持久 manifest ＋ 消费者输入指纹 → `CodeSourceChangeSet`。不读文件、不访问数据库、不看时钟。落地的不变量：新路径即使 mtime 很旧也必须处理；stat 未变+无提示+输入未变 ⇒ 复用（不读正文）；mtime 落在 `ScanStartedUtc - RacyOverlap` 内是候选；提示/深度核验/stat 读不到必须核验内容；**hash 与本次 stat 不一致（读写竞争）⇒ Deferred，绝不提交**；内容一致但策略/语义输入变了 ⇒ 只重绑；**删除只能由完整且根可用的扫描得出**，不完整/根不可用/watcher-only/扫描期间又变化的路径一律 Deferred；水位只在「完整+根可用+确实扫描过」时推进 |
| `CodeSourceMaintenanceLedger.cs` | **D2 持久待办语义（纯内存，无 I/O）**（2026-10-02）：`FromState` 从持久快照恢复（保留 `DirtyAgain`）；`RecordObservedChanges` 每批递增期望版本并**标记「还有工作」**，返回捕获版本；`CompleteCommit` 只确认捕获版本、只对真正提交成功的消费者做单调 `max` 推进；扫描水位仅在「捕获版本即当前期望 + 扫描完整 + 无未解决路径 + 无待重试」时前进且永不回退；`BeginEpoch` 递增世代（旧世代提交返回 `StaleEpoch`）；`RecordFailure`/`DueRetries`/`ClearRetry` 实现有界退避（默认 60s→30min）。持久化在 `SqliteCodeIndexStore.SourceMaintenance.cs` |
| `FileSystemCodeSourceScanner.cs` | **D2 默认磁盘枚举**（2026-10-02，只读元数据、不读正文、不写任何东西）：根先探测（缺失/不可读 ⇒ `RootUsable=false`）；目录被忽略即整棵子树不枚举（忽略规则经 `ICodeSourceIgnoreRules` 注入）；子树读不到或触条目上限只置 `Complete=false` + 原因（**绝不假装「那里没有文件」**）；忽略规则抛错按「不忽略」处理（多一个候选只是核验一次）；枚举顺序按组件路径身份确定 |
| `CodeSourceScanService.cs` | **D2 完整清单校准**（2026-10-02）：持久状态 → 磁盘枚举 → 真实变更集 → 账本登记捕获版本并持久化。**不读正文/不算 hash**（返回 `RequiresContentHash=true` 交执行层）、**不写索引**、**不推进扫描水位**（水位只在执行层回报 `CompleteCommit` 时前进）；存储无该能力时降级为 `CapabilityMissing` 并只产出磁盘事实；无待办时不递增版本也不写库 |
| `CodeSourceFingerprintReader.cs` | **D4 稳定读**（2026-10-02）：`ReadAsync` 读前 stat → 读内容 → 读后 stat，**两者一致才认**，hash 来自真正读到的那份内容；不一致/长度不符 ⇒ 不稳定（调用方必须弃用本轮结果）；不存在/无权限/超上限 ⇒ 带原因的失败。`virtual` 是为了让「不稳定 ⇒ 弃用」这条分支能被确定性验证（真实读写竞争无法可靠复现）。默认单文件上限 64MB |
| `CodeSourceMaintenanceCoordinator.cs` | **D4 维护协调器**（2026-10-02）：把各件串成一条链 —— 校准（真实变更集 + 捕获版本）→ 更新计划 → **语言批量接缝一次调用** → **稳定读**算指纹 → `ReplaceFilesAsync` **一个事务**提交索引 + 指纹 + 消费者水位 → 账本 `CompleteCommit`（只有它能推进扫描水位）。失败语义：语言 `Retryable` / 稳定读不稳定 ⇒ 记退避 + 计入未解决 ⇒ 水位不前进；单文件失败只影响它自己，绝不升级整仓。`NotApplicable` 路径仍如实记指纹并按「消费者视图已最新」推进（否则每轮都会被当新文件）。watcher 节流、宿主忽略规则、DI 与调度节拍不在本类 |
| `CodeSourceUpdatePlanner.cs` | **D3 更新计划**（2026-10-02）：变更集 → 执行计划（Extract → RebindOnly → Delete → RetryLater，组内按路径排序）。**反向依赖扩展只沿已知符号变化走**（没有 `CodeFileSemanticChange` 就一次图都不查）；依赖方以 `RebindOnly`/深度 1 入计划并沿用触发它的消费者；本来要提取/删除/退避的依赖方不被降级；退避中的路径不参与扩展也不重复入队；扩展有界并显式 `Truncated`；`RefreshFingerprintOnly` 不进计划 |

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
**295 用例**（2026-10-02 实测；含 3 条边界断言；U3-C 后 66 → 82，**U4-2a 后 82 → 98：+16 条检索合同契约测试**，**U3-D 后 98 → 107：+9 条常规校准 / 成本用例**，**U3-E 后 107 → 114：+7 条退避用例（含 1 条反射边界断言）**，**U3-G1 后 114 → 116：+2 条取消标记 + 对照用例**；**高磁盘读取修复 C 后 +7：4 条入边/出边/自引用/跨 scope 语义 + 3 条删除计划与 VDBE 工作量用例**；**D2 第一阶段 +25：源指纹 / 三源变更判定 / 删除可证实性 / 水位规则**；**D2 第二阶段 +15：账本捕获版本、消费者水位、扫描水位前置条件、世代作废与退避阶梯**；**D2 存储 +14：manifest/账本往返、单事务原子性、删除连带水位、回退写入拒绝**；**D2 校准 +17：元数据扫描器 8 条（忽略剪枝/不完整/触顶/规则异常）+ 校准服务 9 条（增删改候选、水位不推进、根不可用不删、能力缺失降级）**；**D4 原子替换 +10：同事务提交、所有权重建、稳定入边保留、消失目标报告依赖方、批次回滚、分块删除**；**D3 更新计划 +15：动作映射与顺序、只沿已知符号变化扩展、退避不驱动依赖方、扩展触顶 Truncated、图查询分块与双来源**；**D3 批量路由与语义差异 +19：能力路由四态（NotApplicable/Retryable/ScopeRunRequired）、同语言单次批量调用、抛错与静默丢路径按路径收容、语义差异只认消失与签名变化**），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。
