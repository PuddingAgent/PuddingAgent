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
| `SqliteCodeIndexStore.cs` | SQLite 索引存储（实现 `ICodeIndexStore`）；**U3-B3** 的 `RemoveFilesAsync` 对一个批次只开**一个事务**（部分失败 ⇒ 一字不删）。**逐符号图删除用四条精确删除**（2026-10-02）：`RemoveSymbolGraphForFileAsync` 对每个符号分别删 CodeReferences/CodeRelations 的 Source 与 Target，而不是每表一条 `Source OR Target` —— OR 形式只落到主键作用域前缀，每个符号都重扫整个 workspace/project 分区；四条精确删除走既有的 Source/Target 索引，四次删除与文件符号删除仍在同一事务 |

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
**165 用例**（2026-10-02 实测；含 3 条边界断言；U3-C 后 66 → 82，**U4-2a 后 82 → 98：+16 条检索合同契约测试**，**U3-D 后 98 → 107：+9 条常规校准 / 成本用例**，**U3-E 后 107 → 114：+7 条退避用例（含 1 条反射边界断言）**，**U3-G1 后 114 → 116：+2 条取消标记 + 对照用例**；**高磁盘读取修复 C 后 +7：4 条入边/出边/自引用/跨 scope 语义 + 3 条删除计划与 VDBE 工作量用例**），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。
