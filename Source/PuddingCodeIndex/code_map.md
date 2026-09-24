# PuddingCodeIndex CodeMAP

> 索引组件：**维护索引产物** | 契约 · 存储 · 变更捕获管线 · 调度 · 范围注册/解析 · 排除模式 · 路径标识
> 边界（ADR-089 §2.1）：本工程**不得引用** `PuddingCodeIntelligence`（编译期强制；`ProjectReference` 为空）。
> 依赖倒置：`ICodeIndexer` 定义在本工程，Roslyn/TS 等**实现留在** `PuddingCodeIntelligence`。
> 命名空间：`PuddingCodeIndex.Contracts` / `PuddingCodeIndex.Services` / `PuddingCodeIndex.Services.CodeIndex` / `PuddingCodeIndex.Storage`

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
| `ICodeIndexSchedulerDriver.cs` | **U3-B1** 显式泵端口（`ProcessPendingAsync` + 每 scope `Desired/Committed` 水位） |
| `ICodeIndexMaintenance.cs` | **U3-B1** 变更驱动维护服务的生命周期/只读观测契约 + `CodeIndexMaintenanceScopeStatus`（**U3-B3** 状态增 `RemovedFileCount` / `IncrementallyIndexedFileCount` / `ScopeEscalationCount`；**U3-C** 再增 `SweptFileCount` / `CalibrationRunCount` / `RejectedCalibrationRunCount` / `LastCalibrationAtUtc`） |
| `ICodeIndexScopeRegistry.cs` | 范围注册端口 |
| `ICodeIndexScopeResolver.cs` | 范围解析端口 + `ScopeResolution` |
| `ICodeProjectRegistry.cs` | 项目注册端口 |
| `ICodeWorkspaceResolver.cs` | 工作区解析端口 |
| `ICodeProjectRootDetector.cs` | 项目根探测端口 |

## 变更捕获管线（Services/CodeIndex/ → `PuddingCodeIndex.Services.CodeIndex`）

| 文件 | 用途 |
|------|------|
| `IndexChange.cs` | 单条文件系统变更观测（`IndexChangeKind`） |
| `CodeIndexChangeQueue.cs` | 有界队列（容量 8192，`TryPublish` 不阻塞） |
| `CodeIndexScopeState.cs` | 范围状态（dirty/version/reconcile，无 IO）；**U3-B3** 增 reconcile 原因 `BatchApplicationFailed` |
| `CodeIndexWatcher.cs` | 文件系统监视器（64KB 缓冲，回调只过滤 + TryPublish） |
| `CodeIndexChangeCoalescer.cs` | 防抖折叠（静默 500ms / 最长 2s，2 万路径 → reconcile） |
| `CodeIndexChangeBatch.cs` | 折叠产物（重读集合 / 移除集合 / reconcile 标记） |
| `CodeIndexChangeWatchers.cs` | **U3-B1** 变更源抽象（`ICodeIndexChangeWatcher` / `ICodeIndexWatcherFactory`）+ 真实 watcher 适配工厂 |
| `CodeIndexMaintenanceService.cs` | **U3-B1/U3-B3** 变更→索引的单一驱动（消费批次、置脏补跑、有界停止）。**U3-B3 按文件施用**：`PathsToRemove` → store 真删除；`PathsToReindex` → `ICodeIndexFileUpdater.IndexFileAsync` 逐文件（索引器无该能力则同样升级）；仅 reconcile / 目录变更 / 索引器拒绝才升级为 scope 级重索引；批次施用失败 ⇒ 标 `NeedsReconcile` + 记错误日志（不静默丢弃） |
| `CodeIndexCalibrationService.cs` | **U3-C 校准（mark-and-sweep）**：取 scope 已索引路径集合（`ListFilesAsync`），逐条判磁盘存在性，对"已消失"的调用 `RemoveFilesAsync`（只删索引行）；**根目录缺失/不可读 ⇒ 拒绝 sweep**（零移除 + 保持置位）；宽限窗口内被变更管线刚观测过的路径豁免；每事务 ≤256 条、每轮 ≤4096 条，可取消 |

## 服务（Services/ → `PuddingCodeIndex.Services`）

| 文件 | 用途 |
|------|------|
| `CodeIndexScheduler.cs` | 索引调度器（**U3-B1：显式驱动，无自建后台线程**；in-flight 期间到达的请求置脏并在结束后重新入队，不再丢弃） |
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
| `SqliteCodeIndexStore.cs` | SQLite 索引存储（实现 `ICodeIndexStore`）；**U3-B3** 的 `RemoveFilesAsync` 对一个批次只开**一个事务**（部分失败 ⇒ 一字不删） |

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
**98 用例**（含 3 条边界断言；U3-C 后 66 → 82，**U4-2a 后 82 → 98：+16 条检索合同契约测试**），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。

## U3-B2a 更新（2026-09-23）— 队列泵已接入宿主

- `ICodeIndexMaintenance` 契约补齐 **`EnsureScope(workspaceId, scopeId, rootPath)`**：宿主只能通过端口挂载 scope，不必解析具体实现类。
- `CodeIndexMaintenanceService.ProcessDueBatchesAsync` 现在是**完整驱动步**：消费到期批次之外，**每步无条件泵一次** `ICodeIndexSchedulerDriver.ProcessPendingAsync`。生产受理点（`code_index_register_project`）直接 `Enqueue`、背后没有变更批次，只在 `HandleBatchAsync` 内泵会让这类请求永久饿死（P0）。
- `CodeIndexMaintenanceService` 仍是普通组件服务（**不实现 `IHostedService`**）：宿主接线只在其外部（DI 注册 ＋ `PuddingHost` 的驱动），组件依赖方向不变。
- 宿主接线（DI ＋ `IHostedService`）落在 `PuddingCodeIntelligence/DependencyInjection.cs` 与 `PuddingHost/Hosting/CodeIndexMaintenanceHostedService.cs` —— 本组件**仍然不引用** Host，边界由编译期强制（`ProjectReference` 清单不变，仍为空）。
- 回归：`PuddingCodeIndexTests` **66/66**（含 3 条边界断言）、`PuddingCodeIntelligenceTests` **93/93**（U3-B3 后）。

## U3-B3 更新（2026-09-24）— 删除/重命名真清除 + 按文件增量

- **基线取证（改动前实测，第 0 步）**：文件被删后旧条目**确实残留且仍被检索返回**。
  - 变更管线级：`file indexed → Deleted 批次 → 查询仍返回该文件符号`（`CodeIndexRemovalCorrectnessTests` 的 A6 用例改动前红：`Assert.IsEmpty 失败。大小 0 的预期集合。实际：1`）。
  - 索引器级（真 Roslyn + 真 store）：文件从编译中消失后跑**全量重索引**，该文件仍残留 2 条符号行
    （`T:Probe.Alpha`、`M:Probe.Alpha.Value`），`CodeFiles` 行也仍在 —— **全量重索引不做 sweep**（sweep/校准属 U3-C）。
- **修复**：`ICodeIndexStore.RemoveFilesAsync`（一个批次只开一个事务；文件记录 + 符号 + 其关系/引用；幂等 no-op，返回真正删除的文件数）
  ＋ 新端口 `ICodeIndexFileUpdater.IndexFileAsync`（按文件能力**独立成端口**，不塞进 `ICodeIndexer`：给全量端口加成员会破坏每一个实现者，实测弄坏了禁写路径的宿主测试替身）；
  ＋ 维护服务按文件施用：`PathsToRemove` → store 真删除、`PathsToReindex` → `ICodeIndexFileUpdater.IndexFileAsync` 逐文件（索引器无该能力 ⇒ 同样升级）；
  重命名 = 旧路径清 + 新路径索引。索引器拒绝 / 目录变更 / reconcile ⇒ 升级为 scope 级重索引（不丢变更）；
  批次施用异常 ⇒ 标 `NeedsReconcile(batch_application_failed)` + 记错误日志。
- **未接线（诚实留白）**：`PuddingFullTextIndex`（Lucene）承载的是**文件内容**检索的另一份数据
  （自建 `stalePaths` 增量清理，见 `LuceneSearchEngine.BuildIndexAsync`），**不读** `CodeFiles/CodeSymbols`，
  与 code index 无数据交叠 ⇒ 本刀**不接**，也未新增任何跨组件依赖。
- **未验证项**：Roslyn 的 `IndexFileAsync` 每次调用会重新打开 MSBuild 工作区（批内多文件 = 多次加载）；
  未做工作区复用，也未做端到端（真 `.csproj`）的按文件索引实测。

## U3-C 更新（2026-09-24）— 校准（mark-and-sweep）清陈旧行 + NeedsReconcile 归位

- **新增 `Services/CodeIndex/CodeIndexCalibrationService.cs`**（同文件内 `CodeIndexCalibrationRequest` / `CodeIndexCalibrationResult` / `CodeIndexCalibrationRejections`）：scope 的 mark-and-sweep。
  - **输入**：`WorkspaceId` / `ScopeId` / `RootPath`（先探测）/ `RecentObservations`（变更管线最近观测到的路径 → 时间戳）。
  - **算法**：`ListFilesAsync` 取已索引路径 → 逐条 `File.Exists || Directory.Exists` → 对"已消失且不在宽限窗口内"的路径分批 `RemoveFilesAsync`（**只删索引行**，事务性、幂等）。
  - **有界**：`DefaultSweepBatchSize`=256（每个事务）/ `DefaultMaxRemovalsPerRun`=4096（每轮，超出 ⇒ `Truncated=true` 留给下一轮）；逐路径与逐批次检查取消令牌。
  - **宽限窗口** `DefaultGraceWindow`=2min：窗口内刚被观测的路径豁免（与 watcher/在途索引竞争；原子替换/在途重命名/构建重写自身输出的"瞬时不存在"不是历史遗留）。豁免是延后，窗口过后下一轮照扫。
  - **拒绝（本刀最危险处的防线）**：`TryProbeRoot` 失败（路径/根缺失、根不可读）⇒ `RootUsable=false` + `Error` 日志 + **0 移除**（不列 store）；调用方据此保持/置位 `NeedsReconcile`。
- **`CodeIndexMaintenanceService` 接入**：驱动步在"批次施用 + 无条件泵"之后跑 `CalibrateReconcileScopesAsync` —— 只处理 `NeedsReconcile` 的 scope；成功 ⇒ `CodeIndexScopeState.ClearNeedsReconcile()`；被拒/被截断 ⇒ 保持置位；每 scope ≥ `DefaultCalibrationInterval`(60s) 才重试（防不可用根目录按 200ms 轮询刷日志）。每个已施用批次的路径记入 `ScopeEntry.RecentObservations`（宽限窗口数据来源，按窗口清理防无界增长）。**变更源创建失败 ⇒ 立即 `MarkNeedsReconcile(watcher_error)`**。
- **`CodeIndexScopeState`**：新增 `ClearNeedsReconcile()`（只清 reconcile + reason，**不动** dirty / 计数器 / ObservedVersion）与常量 `CalibrationRootUnavailable` / `CalibrationFailed`。
- **测试**：`CodeIndexCalibrationDriverTests`（A1 硬判据 / A2 不动文件系统 / A3 拒绝 sweep / A4 计数 / A5 watcher 失败也校准 / 宽限窗口 / 每轮 ceiling）、`CodeIndexCalibrationServiceTests`（A6 幂等 / 拒绝 / 宽限窗口 / 分批与上限 / 取消 / 只调用 `ListFiles`+`RemoveFiles` / 空 scope）、`CodeIndexCalibrationTestDoubles`（`RecordingCodeIndexStore` / `RecordingLogger` / `ThrowingWatcherFactory`）。
- **门禁**：本工程 build exit 0；`PuddingCodeIndexTests` **82/82**、`PuddingCodeIntelligenceTests` **93/93**、`PuddingHost.Tests` **124/124**、`PuddingAgent -c Release` exit 0 且 `error CS`=0；`ProjectReference` 仍为 **0**；变异 A/B 取红 + 复原逐位相同（见根 `code_map.md` 的 U3-C 条目）。
- **边界未击穿**：未新增 NuGet；本组件仍**不引用** `PuddingCodeIntelligence` / `PuddingRuntime` / `PuddingHost` / `PuddingAgent`（反向检索 4 名仅命中 2 处注释，带阳性对照）；未改 Host/DI（驱动器新增的可选构造参数有默认值，宿主照旧只解析 `ICodeIndexMaintenance`）。

## U4-2a 更新（2026-09-24）— 检索意图与结果合同冻结（叶子侧，不接 Host）

**新增目录 `Contracts/Retrieval/`（命名空间 `PuddingCodeIndex.Contracts.Retrieval`，24 文件）**：只冻结合同与纯函数，**不实现任何真实检索引擎**（U4-2c / U4-3 才做）。

| 文件 | 内容 |
|------|------|
| `RetrievalIntent.cs` | 检索意图枚举；**`Auto = 0`（必须占 0 ⇒ 缺省即 Auto）**，共 12 值 |
| `RetrievalIntentPolicy.cs` | intent → 层序 / 符号种类过滤 / 关系过滤 的**成文表**；`IsLegitimateBulkIntent`（正当的多豁免） |
| `RetrievalTaxonomy.cs` | `RetrievalHitKind`（六层 + Unknown，值即层序）、`RetrievalConfidence`（信任秩 Semantic>Lexical>Unknown）、`RetrievalRelationKind`、`RetrievalMatchTarget`（`[Flags]`：只关注类名称 = `SymbolName`） |
| `SymbolIdentity.cs` | 跨 scope 去重键（规范化 symbol_id + 文件 + 行；**归一化在构造期完成** ⇒ 取值相等即身份相等）；`ScopeIdSet.cs`（规范集合，避免裸数组的引用相等陷阱） |
| `RetrievalEvidence.cs` | 证据位置（文件 + 行 + 可选片段）——"每条命中都能回答证据在哪" |
| `RetrievalHit.cs` | 命中（层/种类/语言原始种类/关系/证据/置信度/分数/**why 必填**/匹配域/scope 盖章/`ScopeCorroborationWeight`） |
| `RetrievalHitComparer.cs` | **显式全序**（有效分数 → 层序 → 路径 → 行 → 符号名 → 身份键）；`RetrievalHitOrdering.EnsureOrdered` / `EnsureUniqueIdentities` |
| `RetrievalHitDeduplicator.cs` | 跨 scope 去重（同身份只留一条 + 并集 scope 盖章），**先于过载判定** |
| `RetrievalFilter.cs` | 正交过滤面（扩展名 / 目录+递归 / 匹配域 / `CodeSymbolKind` / 命中层 / 关系 / 置信度下限）+ `RetrievalFacet`（放宽面）+ `Matches`（**AND 组合**）+ `Describe`（回显） |
| `RetrievalDiagnostics.cs` | `QuerySpecificity` / `OverloadReasonKind` / `QueryDiagnostics`（Low 必须有原因、High 必须无原因）/ `RetrievalOverloadFacts`（聚合事实）/ `RetrievalOverloadDiagnostics.Evaluate`（成文规则） |
| `RetrievalNextStep.cs` | 下一步建议（`NarrowQuery`/`RelaxFilter`/`RebuildIndex`/`UseAlternativeFace`/`AcceptAbsence`）+ `RetrievalSuggestionKind`（**恰好四类**收窄手段） |
| `RetrievalEmptyReason.cs` | 空结果原因 + `RetrievalEmptyReasonPolicy`（裁决优先级成文；**过滤性空 ⇒ FilteredOut**） |
| `RetrievalOverflow.cs` | 落盘信息（路径 + 条数 + 预算口径）+ `RetrievalSpillPolicy`（路径必须在 `.pudding`/`temp`/`.tmp` 或系统临时目录内） |
| `RetrievalBudget.cs` | 双预算默认值（条数 + 字节/token，token 优先）与**估算**口径（非实测） |
| `RetrievalRequest.cs` | 请求（query + scope + `Intent = Auto` + 过滤面 + 页大小 + 游标 + 双预算） |
| `RetrievalResult.cs` | 结果（分层命中 + 双视图 + 真实总数 + 分页 + 落盘 + 分布 + 诊断 + 跨 scope 警告 + 降级标注 + 生效过滤面回显）；**唯一构造路径是两个静态工厂**，跨字段不变量 fail-closed |
| `RetrievalHitViews.cs` | 双视图派生（`symbols` / `files`，各带命中原因摘要） |
| `ScopeOverlap.cs` | 嵌套/重叠 scope 描述、警告与**纯函数检测器**（段边界，`repo` 不吞 `repo2`） |
| `LanguageCapability.cs` | 能力矩阵条目（**不支持 ⇔ 必须给替代方案**）+ `ILanguageCapabilityMatrix` 端口 |
| `RetrievalContractViolationException.cs` | 跨字段合同违反（与入参级 `ArgumentException` 分工明确） |
| `RetrievalDistribution.cs` / `RetrievalPathFacts.cs` | 分布摘要（SortedDictionary ⇒ 确定性）+ 语言/顶层目录纯推导 |
| `IRetrievalPort.cs` | 端口：`Task<RetrievalResult> RetrieveAsync(RetrievalRequest, CancellationToken)`（**本刀无实现**） |

**把"空洞否定"变成不可表示**（不靠注释约定，靠类型与构造校验）：① `hits=0` 且无 `EmptyReason` 的构造路径不存在（无公开构造函数 + 工厂校验 + 反射断言）；② `Degraded=true` ⇒ `DegradedReason` 必填；③ `NextSteps` 至多 1 条（>1 抛异常，**不静默截断**）；④ 截断（`totalCount > hits.Count`）必须同时给 `Overflow`（落盘路径）+ `NextCursor` + 非空 `Distribution`，否则拒绝构造。

**成品门禁（本刀实测）**：`dotnet build PuddingAgentNetwork.slnx -c Release` exit 0 / `0 个错误`（改前同）；`PuddingCodeIndexTests` **98/98**（改前 82/82）；`ProjectReference` 仍为 **0**、未新增 NuGet、未改任何 `DependencyInjection.cs` / `.slnx` / Host。
