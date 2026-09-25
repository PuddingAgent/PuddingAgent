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
| `ICodeIndexMaintenance.cs` | **U3-B1** 变更驱动维护服务的生命周期/只读观测契约 + `CodeIndexMaintenanceScopeStatus`（**U3-B3** 状态增 `RemovedFileCount` / `IncrementallyIndexedFileCount` / `ScopeEscalationCount`；**U3-C** 再增 `SweptFileCount` / `CalibrationRunCount` / `RejectedCalibrationRunCount` / `LastCalibrationAtUtc`；**U3-D** `CalibrationRunCount` 计入常规（周期）校准；`LastCalibrationAtUtc` 同时是常规时钟的锚） |
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
| `CodeIndexScopeState.cs` | 范围状态（dirty/version/reconcile，无 IO）；**U3-B3** 增 reconcile 原因 `BatchApplicationFailed`；**U3-C** 增 `CalibrationRootUnavailable` / `CalibrationFailed`；**U3-D** 增 `CalibrationTruncated`（常规路径被 ceiling 截断也置位） |
| `CodeIndexWatcher.cs` | 文件系统监视器（64KB 缓冲，回调只过滤 + TryPublish） |
| `CodeIndexChangeCoalescer.cs` | 防抖折叠（静默 500ms / 最长 2s，2 万路径 → reconcile） |
| `CodeIndexChangeBatch.cs` | 折叠产物（重读集合 / 移除集合 / reconcile 标记） |
| `CodeIndexChangeWatchers.cs` | **U3-B1** 变更源抽象（`ICodeIndexChangeWatcher` / `ICodeIndexWatcherFactory`）+ 真实 watcher 适配工厂 |
| `CodeIndexMaintenanceService.cs` | **U3-B1/U3-B3** 变更→索引的单一驱动（消费批次、置脏补跑、有界停止）。**U3-B3 按文件施用**：`PathsToRemove` → store 真删除；`PathsToReindex` → `ICodeIndexFileUpdater.IndexFileAsync` 逐文件（索引器无该能力则同样升级）；仅 reconcile / 目录变更 / 索引器拒绝才升级为 scope 级重索引；批次施用失败 ⇒ 标 `NeedsReconcile` + 记错误日志（不静默丢弃）。**U3-D 常规校准**：驱动步末尾的校准由 `TryBeginCalibration` 逐 scope 判due —— 被标位的 scope（U3-C，首次尝试在下一步、重试节流 `DefaultCalibrationInterval` 60s）**或**自有常规周期到期的 scope（`DefaultCalibrationPeriod` 15min，按**上次完成**计时，首次以挂载时刻为锚）。未到期 ⇒ **一次校准都不发起**（每日 200ms 步不会变成扫盘）；被拒/被截断 ⇒ **保持或置位** `NeedsReconcile`。**U3-E 重试退避**：被标位 scope 的重试间隔改为**指数阶梯** —— 以 `DefaultCalibrationInterval`(60s) 为底、**第二次及以后的连续失败**逐次翻倍（60s/2m/4m/8m/16m），封顶到组件常量 `DefaultCalibrationBackoffMax`(**30min**)；**任何一次“读到了根”的 sweep（完成或截断）立刻把档位复位到 0**；15min 常规钟与“首次尝试在下一步 / 单次失败仍 60s”**逐字未变**（阶梯只判“重复失败”，且与常规钟不叠加：`IsCalibrationDue` 的 if/else 二者永不同时参与） |
| `CodeIndexCalibrationService.cs` | **U3-C 校准（mark-and-sweep）**：取 scope 已索引路径集合（`ListFilesAsync`），逐条判磁盘存在性，对"已消失"的调用 `RemoveFilesAsync`（只删索引行）；**根目录缺失/不可读 ⇒ 拒绝 sweep**（零移除 + 保持置位）；宽限窗口内被变更管线刚观测过的路径豁免；每事务 ≤256 条、每轮 ≤4096 条，可取消 |

## 服务（Services/ → `PuddingCodeIndex.Services`）

| 文件 | 用途 |
|------|------|
| `CodeIndexScheduler.cs` | 索引调度器（**U3-B1：显式驱动，无自建后台线程**；in-flight 期间到达的请求置脏并在结束后重新入队，不再丢弃）；**U3-G1：取消分支不再“什么都不写”** —— 保持 `Status=Registering`（语义不变，“仍欠一次完整运行”）的前提下盖章一句可区分的 `StatusMessage`（含 `interrupted` / `cancelled`）；**仅当该行确实处于 `Registering` 时才盖章**（在认领该行之前就被取消的运行不得被记成“被中断”，已 `Removed`/`Active` 的行也绝不能被翻回 `Registering`）。未新增状态枚举值，未动附着判据 |
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
**116 用例**（含 3 条边界断言；U3-C 后 66 → 82，**U4-2a 后 82 → 98：+16 条检索合同契约测试**，**U3-D 后 98 → 107：+9 条常规校准 / 成本用例**，**U3-E 后 107 → 114：+7 条退避用例（含 1 条反射边界断言）**，**U3-G1 后 114 → 116：+2 条取消标记 + 对照用例**），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
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

## U3-D 更新（2026-09-24）— 每 scope 15min 常规校准周期

**问题**（U3-C 遗留、本刀唯一真功能缺口）：校准此前**只能**由 `NeedsReconcile` 触发 ⇒ 变更源长期静默的 scope（附着失败后再无事件、进程未运行期间的变更、标志位出现前丢的事件）**陈旧索引行永远清不掉**；而 scope 级重索引只重读"还在"的文件，结构上不可能清掉已消失者的行。

**交付**（生产改动只在 `Services/CodeIndex/`：`CodeIndexMaintenanceService.cs`、`CodeIndexScopeState.cs`；外加 `Contracts/ICodeIndexMaintenance.cs` 的**文档注释**。**零 Host / 零 DI / 零 csproj / 零 NuGet / 零排除规则改动**）：
- 新常量 `DefaultCalibrationPeriod = 15min`（组件侧常量，不新增配置层；与既有 `DefaultPollInterval`(200ms) / `DefaultCalibrationInterval`(60s) 并列）。
- `ScopeEntry` 增 `AttachedAtUtc`；`CalibrateReconcileScopesAsync` → **`CalibrateDueScopesAsync`**，到期判定抽为 `TryBeginCalibration` + 静态纯函数 `IsCalibrationDue`（锁纪律不变：判定/盖章在服务门下）。
- **到期 = 二者之一**：① 被标位（U3-C 原规则，首次尝试就在下一步、重试 ≥ `DefaultCalibrationInterval` 60s）；② 自有常规周期到期（按**上次完成**计时 —— ADR-089 §U3-C「正常 metadata 校准每 15min…均按上次完成后计时，不并发叠加」；从未跑过 ⇒ 以挂载时刻为锚）。**未到期 ⇒ 一次校准都不发起** ⇒ 200ms 驱动步不会变成扫盘。
- **被拒 / 被截断 ⇒ 保持或置位 `NeedsReconcile`**：新增原因 `CalibrationTruncated`（常规路径被 ceiling 截断也置位，否则剩余陈旧行要等下一个周期），根不可用仍是 `CalibrationRootUnavailable`。校准**时间缝沿用既有构造参数 `TimeProvider`**（未改构造函数签名、未加 DI 注册）。
- 未改构造函数签名、未加 DI 注册：时间缝沿用既有构造参数 `TimeProvider`（测试用 `MutableTimeProvider` 假钟推 15min，**不真等待**）；常规钟就是 `LastCalibrationAtUtc ?? AttachedAtUtc`，没有另新增一份时刻状态。

**门禁（本刀实测）**：`PuddingCodeIndexTests` **107/107**（改前 98/98；+9 用例）；`PuddingAgentNetwork.slnx -c Release` 见 `temp/U3-D-REPORT.md`；M1/M2/M3 三个变异各取红（A2 / A1 / A3），复原后 `git hash-object` 逐位相同、`MUTATION` 残留 0。
**成本口径（实测）**：单 scope 350 条索引路径（348 在盘 / 2 已消失）一次常规 sweep = **19 ms**、清 2 行；**未到期的一步 = 0.03 ms**（不列盘）。⇒ 15min × N scope 的叠加成本可接受（见报告 §7.4）。

## U3-E 更新（2026-09-24）— 校准重试指数退避 + 封顶

**问题**：ADR-089 写的是「监听不可用时 60s 轮询 **+ 退避**」，**退避从未实现**。只要一个 scope 的根目录消失/不可读，它就被置 `NeedsReconcile`，此后**每 60s 重试、永不退避**；U3-D 又把这条路径从「只有被标位的 scope」扩到「**任何根消失的 scope 首次常规 sweep 后都会走到**」⇒ 一个坏 scope = **1440 次探测 + 2 行 Error/次/天**。

**交付**（生产改动**只**在 `Services/CodeIndex/CodeIndexMaintenanceService.cs`：`git diff --numstat` = **153 插入 / 10 删除**，10 处删除全是被替换行。**零 Host / 零 DI / 零 csproj / 零 NuGet / 零排除规则 / 未动 `CodeIndexCalibrationService.cs`**）：
- 新组件常量 **`DefaultCalibrationBackoffMax` = 30min**（与 `DefaultPollInterval` / `DefaultCalibrationInterval` / `DefaultCalibrationPeriod` 并列，不新增配置层）。
- 新纯函数 **`CalibrationBackoffInterval(long consecutiveFailures)`**（`internal static`）：`DefaultCalibrationInterval`(60s) 为底、**第二次及以后的连续失败**逐次翻倍 ⇒ 60s/2m/4m/8m/16m；下一档本应 32m，**封顶为 30m** 且此后恒 30m。循环在触到封顶时立即结束（纯且全：失败 10⁶ 次与失败 6 次同价）。
- `ScopeEntry` 纯新增 **`ConsecutiveCalibrationFailures`**（`_gate` 保护；与既有 `LastCalibrationAttemptAtUtc` 同锁同位置）。**未新增任何公共状态字段** ⇒ `CalibrationRunCount` / `RejectedCalibrationRunCount` / `LastCalibrationAtUtc` 语义与 `CodeIndexMaintenanceScopeStatus` 形状**一字未改**。
- `IsCalibrationDue` 标位分支：`A || B` 拆为「从未尝试 ⇒ **立即**（U3-C 逐字保留）」+「`now - lastAttempt >= CalibrationBackoffInterval(failures)`」。**else 分支（15min 常规钟）一字未改** ⇒ **阶梯只作用于“重复失败”**：单次失败仍是 60s。
- 三条结局接阶梯：**异常 ⇒ +1 档**、**被拒 ⇒ +1 档**、**截断 ⇒ 复位**、**成功 ⇒ 复位**；被拒/异常的日志追加「连续失败档位 + 下一次允许尝试的时刻」（R4 可见性）。`Truncated` 算「读到了根」故复位 —— 它是进展不是失败（规格未钉死，理由见 `temp/U3-E-REPORT.md` §2）。
- 缺口（阶梯）**加宽了**，因此 `git hash-object` 在改前/每个变异复原后均为 `422c166c835e75c53d431505f46cd01518878968`。

**必答三问（实测，详见 `temp/U3-E-REPORT.md`）**：
1. **序列 60s→2m→4m→8m→16m→30m(封顶)**。永久坏掉的 scope 尝试时刻 `0, 60s, 180s, 420s, 900s, 1860s`，此后每 1800s ⇒ **首日 52 次（对照 1440，−96.4%）、首周 340 次（对照 10080，−96.6%）**。
2. **不叠加、不会双扫**：`IsCalibrationDue` 的 `if (NeedsReconcile) {阶梯} else {15min}` 二者永不同时参与；每 scope 每步至多一次判定 + 一次 `CalibrateAsync`。实测反证：31 分钟时钟（>2×15min）内被标位 scope 恰好 6 次尝试 = 阶梯预测（叠加则至少多 2 次）。
3. `LastCalibrationAtUtc` **在被拒时确实盖章**（无条件赋值在拒绝分支之前），**但不推迟常规钟** —— 被标位时常规钟不被读取，而解位唯一出口（成功 sweep，`ClearNeedsReconcile` 全组件仅 1 个调用点）会再盖一次 ⇒ 常规钟实际锚在「上次成功」。**根恢复后被重扫的最长等待 = 当前档位：单次失败 60s、档位爬满后 30min**（自觉取舍，见报告 §7③ 的后续建议）。

**门禁（本刀实测）**：`PuddingCodeIndexTests` **114/114**（改前 107/107；**+7 用例 = A1~A6**，含 1 条反射边界断言锁「阶梯是组件常量、构造函数无 backoff knob」）；`PuddingAgentNetwork.slnx -c Release` exit 0 / **0 个错误**（见 `temp/u3e-build-release.txt`）；M1（去封顶）/M2（系数改 1）/M3（成功后不复位）分别取红 **A2 / A1 / A3**，各三份原始输出（红 / 复原绿 / 全绿）在 `temp/u3e-m{1,2,3}-{red,green}.txt` + `temp/u3e-tests-green-full.txt`；复原后 hash 逐位相同、`MUTATION` 残留 **0**。
**成本口径（推算，口径同上）**：坏 scope 由 1440 探测/天 → 52 探测/天（≈104 行 Error/天，对照 ≈2880）。

## U3-G1 更新（2026-09-24）— 被中断的索引 scope：不再可删 + 可诊断

**问题（两条，均已用实读代码 + 只读 SQL 钉死）**：
1. **失数据边缘（宿主侧）**：冗余 scope 删除判据（`Source/PuddingHost/Storage/StorageMaintenanceQueries.cs` 的 `FindObsoleteCodeIndexScopesAsync`）把 `Status IN ('Removed','Failed','Registering')` 列为可删 —— 而 `Registering` 表示「有一次运行欠着」（本调度器的取消分支此前**什么都不写** ⇒ 被中断的运行把该行永久留在 `Registering`），**不是**「已废弃/冗余」。仓库根 scope `b375fee0d6524ad393a26e72ba1e917d` 正命中该判据（`ScopeState IS NULL` ✓ / `Status='Registering'` ✓ / `UpdatedAtUtc=2026-07-28` ✓），它带着 **1083 文件 / 34848 符号 / 96849 关系 / 143587 引用**，目前只靠 `AutomaticCleanupAllowed=false` 挡着 ⇒ **人工清理一次就连注册行一起 DELETE**。
2. **不可诊断**：取消后 `StatusMessage` 仍为 NULL，与「从未开始过」无法区分（`CodeIndexRuns` 表空且全仓无写入者）。

**交付（本组件侧仅取消分支 1 处；零 Host 侧行为改动由本刀承担）**：
- `CodeIndexScheduler.cs`：新增 `private const string InterruptedStatusMessage` 与 `private async Task MarkInterruptedAsync(...)`；取消分支（`catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`）在置 `cancelled = true` 之后调用它。复用既有 `ICodeIndexStore.UpdateProjectStatusAsync`（它只改 `Status` / `StatusMessage` / `UpdatedAtUtc`），并以 `CancellationToken.None` 写入（取消令牌已取消）。
- **语义不变**：`Status` 仍写回 `Registering`（"仍欠一次完整运行"），**未新增状态枚举值**、未改宿主附着判据、未改 `ICodeIndexStore` 契约、未加 DI 注册。
- **fail-closed 的 guard**：仅当该行**确实处于 `Registering`** 时才盖章 —— 在认领该行之前就被取消的运行不得被记成"被中断"，已 `Removed`/`Active` 的行也绝不能被翻回 `Registering`（否则等于伪造"欠一次运行"）。
- 宿主侧配套 1 处（**不属本组件**）：删除判据去掉 `'Registering'`（`StorageMaintenanceQueries.cs`），并同步其**唯一副本** —— `StorageDerivedTargetHandlers.cs::RemoveScopeAsync` 的执行前重校验谓词（`Status IN (` 全仓 .cs **仅 2 处**，均已改；扫描分母 2389 个 .cs，全量覆盖）。`'Covered'` / `'Removed'` / `'Failed'` 语义一字未改。

**门禁（本刀实测）**：`PuddingCodeIndexTests` **116/116**（改前 114/114，+2 = 取消标记用例 + "从未开始仍为 NULL"对照用例）；`PuddingHost.Tests` **126/126**（改前 124/124，+2 = A1/A2）；`PuddingAgentNetwork.slnx -c Release` exit 0 / **0 个错误**（见 `temp/u3g1-build-release.txt`）；M1（把 `'Registering'` 加回判据）取红 **A1**（`Interrupted_Scope_Is_Not_A_Cleanup_Candidate` 报 `Assert.Empty 失败。Collection: [... interrupted-scope ... ArtifactRows = 5]`）；M2（`MarkInterruptedAsync` 写 null）取红 **A3**（`Assert.IsFalse 失败：a cancelled run must leave a StatusMessage behind`）；复原后 `git hash-object` 与变异前**逐位相同**（`dad96e0f…` / `c8d92481…`），`MUTATION` 残留 **0**。
**零写证明**：索引库 `906551296 B` / mtime `2026-09-24 16:25:18.732416` 在探针前后**完全一致**（`-wal` 仍 0 字节；`-shm` 大小不变、mtime 被 SQLite 只读连接推进 —— 非数据写入，已如实登记）。

**R3 判据级复核（只读 SQL 且 SQL 从源码抽取，非手抄）**：改前命中 **1 行**（根 scope），改后命中 **0 行**；其余 3 行改前/改后**一致**（其中 `scope-6526fb344e33` 是 `ScopeState='Active'` + `Registering` 的第二种"卡住"形态 —— 它本来就被第一条分支保护，改前改后都不命中）。详见 `temp/U3-G1-REPORT.md` §2。

**留白（未做，如实登记）**：
- **仍无启动自愈**：被中断的行会一直停在 `Registering`。本刀只让它"可辨 + 不可被误删"，**没有**把它变回"可运行"（改语义/改附着判据不在本刀范围）。
- **取消的取证粒度只有一句话**：`CodeIndexRuns` 仍空且无写入者 ⇒ 无法回答"哪一次运行被取消、被谁取消"。`StatusMessage` 会被**下一次运行**的成功写回覆盖（`Active` + 结果消息），因此它只表达"最近一次被中断"。
- **`MarkInterruptedAsync` 的 guard 分支未有用例覆盖**（要在 store 层注入"取消发生在认领之前"才能触达）；正确性目前靠代码审阅 + `Removed`/`Active` 行不被翻回的状态机推理。

## M1-a 更新（2026-09-24）— 覆盖态投影：穷举 + fail-safe（D1 根治第一刀）

**问题（本组件内，父级实读源码）**：`CodeIndexScopeRegistry.MapStatus`（原 `:222-228`）只显式处理 `Active`/`Failed`/`Removed` 三个生命周期状态，`_ => ScopeState.Covered` 把 **`Unknown` / `Registering` / `Removing` 三个不同状态静默折叠成「被父 scope 覆盖」**。而 `Covered` 在本组件契约里的语义是「不归属、不服务、无需索引」⇒ 命中该投影的 scope 被 `CodeIndexMaintenanceHostedService` 的 `State != Active ⇒ continue` **永久跳过、无法自愈**；`Removing`（删除中）被判为「被覆盖」还与宿主清理判据（`ScopeState IN ('Covered','Removed')`）方向相反。这同时是 U3-G1 留白中「仍无启动自愈」的根因之一 —— 仓库根 scope 正因此一直不可见。

**交付（本组件 1 文件 + 1 新测试文件）**：
- `Services/CodeIndexScopeRegistry.cs`：`MapStatus` → **`ProjectLegacyScopeState`**（更名以显式标出「只适用于 `ScopeState` 列为 NULL 的 legacy 行」），逐项穷举 6 个成员：`Active`→`Active`｜`Registering`→**`Active`**（**覆盖是归属事实**；「欠一次运行」属运行态）｜`Unknown`→`Active`（未建立的状态不得冒充「被覆盖」）｜`Failed`→`Failed`｜`Removing`/`Removed`→`Removed`｜默认臂 **fail-safe：绝不报 `Covered`**（`Covered` 是唯一「把 scope 藏起来」而非暴露它的投影）。调用点 `ToScope` 的 `p.ScopeState ?? ProjectLegacyScopeState(p.Status)` **优先级不变**（显式值永远胜出 ⇒ 不会改写已声明的真实覆盖关系）。
- `PuddingCodeIndexTests/Services/CodeIndex/CodeIndexScopeRegistryTests.cs`（**新建，5 用例**）：`Registering`→`Active`（D1 断言）、`Unknown`≠`Covered`、`Removing`→`Removed`、`Active`/`Failed` 不漂移（对照）、显式 `Covered` 优先（对照）。夹具复用 `CodeIndexFixture`，**不传 scheduler**（列出注册表不得入队）。

**门禁（本刀实测）**：`PuddingCodeIndexTests` **121/121**（改前 116，+5）；`PuddingHost.Tests` **126/126**（无回归）；变异（整段回退为 `_ => Covered`）⇒ **3 红 / 2 对照绿**（`temp/test-out/m1a-mut-red.txt`），复原 ⇒ `MUT_green_EXIT=0`（`-green.txt`）。

**留白（未做，如实登记）**：
- 本刀**只修投影**。`ScopeState` / `CodeProjectStatus` 两个枚举的**正交化**（coverage / serving / run 三轴拆分、`Registering` 不再借生命周期枚举表达）**未动** —— 属 M1 后续刀。
- **RunState 仍未独立**：「欠一次运行」目前只能靠 `Status='Registering'` + `StatusMessage` 表达（`CodeIndexRuns` 表仍无写入者）。
- **附着后果须在下次 Core 重启后实测**：`Registering`→`Active` 会让根 scope 在下次重启被附着；父级已用 `code_outline` 核实 `CodeIndexCalibrationService` **只有剪枝路径**（唯一变更方法 `RemoveBatchAsync`，结果字段无回填/重索引计数）⇒ 不会触发仓库级全量索引，但**本刀不便重启宿主**，实际行为待重启后观察（重点看根 scope 是否进入校准、库体积与 `-wal`）。

## M2-a 更新（2026-09-24）— 容量预算配置化（护栏不再是硬编码常量）

**背景**：用户裁定容量上限必须由配置文件决定（后期可改为 XXGB），并指出「1GB」的 GB/GiB 歧义必须固化为精确字节数。

**本组件新增（严格 S1~S3：只动组件与独立测试，未碰宿主）**：
- `Contracts/CodeIndexLibraryBudget.cs`：预算记录 + 来源/级别枚举 + fail-closed 默认（`1L << 30` = 1 GiB，注释明示「改配置不要改常量」）+ `CodeIndexLibraryCapacityReport`。
- `Contracts/CodeIndexSizes.cs`：`CodeIndexSizes.Parse` —— `KiB/MiB/GiB/TiB` 为二进制(2ⁿ)，`KB/MB/GB/TB` 为十进制(10ⁿ)；**使用十进制后缀会被标记**（`UsedDecimalUnit`）供调用方告警，歧义永不静默。
- `Services/CodeIndexLibraryBudgetResolver.cs`：**路径注入**（组件不猜 DataRoot），优先级 项目文件 > 全局文件 > 内置默认，**逐字段合并**（项目只设上限、全局设软线也能同时生效）；坏 JSON / 非正值 ⇒ **退回默认并告警（fail-closed）**；缺失文件 = 该来源缺席。
- `Services/CodeIndexLibraryCapacity.cs`：`MeasureDirectoryBytes`（库目录总占用，含 `wal`/`shm`/向量段/临时重建）+ `Evaluate`（Ok / SoftExceeded / HardExceeded）。
- 测试：`PuddingCodeIndexTests/Services/CodeIndex/CodeIndexLibraryBudgetTests.cs`（**12 用例**），锁定：默认精确字节、SI/IEC 单位语义、优先级、逐字段合并、fail-closed、十进制告警、目录测量、分级。

**约定（供后续刀与宿主接线复用）**：
- 全局文件 `<DataRoot>/config/code-index.json`（与 `llm.providers.json` 同目录同风格，已落地）；
- 项目文件 `<projectRoot>/.pudding/code-index.json`（`.pudding` 已被索引器硬排除）；
- 两者同 schema，只读 `library` 段：`{ "library": { "maxLibrarySize": "1GiB", "maxLibraryBytes": <exact>, "softRatio": 0.8 } }`。

**门禁**：`PuddingCodeIndexTests` **133/133**（改前 121，+12）；`PuddingHost.Tests` 126/126。变异 #1（fail-open）⇒ **1 红**；变异 #2（`GB`→二进制）⇒ **2 红**；复原全绿，`MUTATION` 残留 grep 0 命中。

**留白（未做，如实登记）**：
- **尚未接线**：宿主侧「配置路径发现 + 解析结果注入组件」属 **S5**；当前**无任何代码读取** `D:\data\config\code-index.json` ⇒ 该文件是「就绪待接线」，不是「已生效」。
- **阈值触发后的行为未实现**（拒绝增长 / GC / `GrepDegraded`），属 M2-b/c。
- 测量口径为「库目录总占用」＝自觉取舍：不会漏计，但 `-wal` 抖动可能让瞬时读数越线；若后续要改为「仅主库文件」，是解析器 + 一处测量函数的局部改动（配置里未引入分支，避免投机式可配置性）。
- 本刀**未做**「每项目一库」的拓扑迁移（M2-d），也未拆分现有中央库。

---

## 变更（2026-09-25，ADR-089 U4-2a）：符号检索的「匹配域」

**症状**：`code_symbol_search(query="conf")` 返回 40 条**全部是 `.ctor`** —— 构造器的 Name 是 `.ctor`，命中实际发生在 **Signature** 列（其签名里含该词）。调用方无法表达「只关注符号名」，拿到的是假阳性；而工具的 query 描述还写着 "matched against symbol names"，与实现不符。

**根因（代码级）**：`SqliteCodeIndexStore.SearchSymbolsAsync` 的 WHERE 让 query 同时匹配 `Name`/`Signature`/`Container` 三列：
`AND ($query = '' OR Name LIKE $likeQuery OR Signature LIKE $likeQuery OR Container LIKE $likeQuery)`
排序亦只有「`Name` 精确等于」优先 ⇒ 签名命中与名字部分匹配同权。

**交付**：

| 面 | 位置 | 事实 |
| --- | --- | --- |
| 契约 | `Contracts/CodeSymbolContracts.cs` | 新增 `[Flags] CodeSymbolMatchTarget { None=0, Name=1, Signature=2, Container=4, All=Name\|Signature\|Container }`；`CodeSymbolSearchRequest` 末尾新增可选 `MatchTarget = All` ⇒ **既有调用零行为变化** |
| 存储 | `Storage/SqliteCodeIndexStore.cs` | WHERE 改为逐列开关 `($matchName=1 AND Name LIKE …) OR …`；`MatchTarget == None` 视为 `All`（否则退化为「返回全部」的静默陷阱） |
| 工具 | `Source/PuddingRuntime/…/CodeIntelligence/CodeQueryTools.cs`（S5 接入） | 新增 `match_target`（逗号分隔，name/signature/container/all）；未知取值 **fail-closed** 且不触达服务；修正与实现不符的 query 描述 |

**门禁**：`PuddingCodeIndexTests` **139/139**（基线 133 + 6）；`PuddingRuntimeTests` 全套 **1879 通过 / 0 失败 / 6 跳过 / 1885**；`dotnet build Source/PuddingRuntime -c Release` exit 0。

**变异取红**：变异 = 三列恒全开（模拟修复前行为）⇒ 主测红在正确断言（应为 `<1>` 实际 `<3>`，`CodeSymbolMatchTargetTests.cs:60`）；复原后 139/139；`MUTATION` 残留 grep **0**。

**诚实登记**：首个变异（`$matchName` 恒 1）**未能取红** —— 在 Name 域上它与正确实现等价（正确实现本就把另两列设 0），属**假变异**；改为模拟修复前行为后才取红。教训：变异必须真正改变受测路径的可观测行为。

**留白（未做，如实登记）**：
- 默认匹配域仍是 `All`（保持既有召回）⇒ 「搜 `conf` 返回一堆 `.ctor`」的**默认体验未变**，需调用方显式传 `match_target=name`。是否把默认改为 `Name` 是**召回/精度取舍**，留待裁定。
- **排序未按匹配域加权**：`ORDER BY CASE WHEN Name = $query THEN 0 ELSE 1 END, Name, SymbolId` 仍只对「名字精确等于」加权。
- `Kind` 与匹配域**正交但未联动**（各自独立过滤）。

---

## 变更（2026-09-25，ADR-089 U4-2b）：跨 scope 去重（硬约束 9/10）

**症状（实测）**：`code_symbol_search` 不传 `project_id` 时，10 条结果里 **5 对重复** —— 同一 `SymbolId` 在仓库根 `b375fee0…` 与 `PuddingRuntime` `scope-6526fb…` 各出现一次。

**根因**：库里有 **4 个互相嵌套的已登记 project**（仓库根 + PuddingCore/PuddingPlatform/PuddingRuntime），同一符号被各索引一份；而 `SymbolId` 是全限定名、跨项目唯一 ⇒ 未限定 project 的检索**必然**返回重复，白占结果位（50 条里可能只剩 25 个不同符号）。

**交付**：

| 面 | 位置 | 事实 |
| --- | --- | --- |
| 服务 | `PuddingCodeIntelligence/Services/CodeQueryService.cs` | `SearchSymbolsAsync` 按 `SymbolId` 去重（保留首次出现者） |
| 存储 | `Storage/SqliteCodeIndexStore.cs` | `ORDER BY` 末尾加 `ProjectId` ⇒ 同 `SymbolId` 行的返回顺序确定，上层「保留哪一条」可预期 |

**门禁**：`PuddingCodeIntelligenceTests` **95/95**（含 2 新用例）；`PuddingCodeIndexTests` **139/139**（ORDER BY 改动无回归）。

**变异取红**：禁用去重 ⇒ 红在正确断言（`Assert.HasCount` 预期 1 实际 2，`CodeQueryServiceTests.cs:100`）；复原 95/95；`MUTATION` 残留 **0**。守卫用例 `SearchSymbols_Keeps_Same_Named_Symbols_That_Have_Distinct_SymbolIds` 锁定「**去重键是 `SymbolId` 不是 `Name`**」—— 两个项目里各自名为 `Helper` 的类必须都保留。

**留白（未做，如实登记）**：
- 去重发生在 store 的 `LIMIT` **之后** ⇒ 跨项目重复密度高时**返回条数可能少于 `limit`**；根治要在 SQL 层按 `SymbolId` 去重并让 `LIMIT` 作用于去重后集合。
- 本改动需宿主重启才在运行中的 `code_symbol_search` 上生效。
- **4 个嵌套 project 的拓扑本身未解决**（同一符号被索引 4 次 = 存储与索引时间都浪费 4 倍）—— 去重只是消费侧止血。
