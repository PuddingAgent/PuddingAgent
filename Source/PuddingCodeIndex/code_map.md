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
| `ICodeIndexStore.cs` | 索引存储端口 |
| `ICodeIndexer.cs` | 索引器端口（实现在 Intelligence） |
| `ICodeIndexScheduler.cs` | 后台调度端口（成员语义未变） |
| `ICodeIndexSchedulerDriver.cs` | **U3-B1** 显式泵端口（`ProcessPendingAsync` + 每 scope `Desired/Committed` 水位） |
| `ICodeIndexMaintenance.cs` | **U3-B1** 变更驱动维护服务的生命周期/只读观测契约 + `CodeIndexMaintenanceScopeStatus` |
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
| `CodeIndexScopeState.cs` | 范围状态（dirty/version/reconcile，无 IO） |
| `CodeIndexWatcher.cs` | 文件系统监视器（64KB 缓冲，回调只过滤 + TryPublish） |
| `CodeIndexChangeCoalescer.cs` | 防抖折叠（静默 500ms / 最长 2s，2 万路径 → reconcile） |
| `CodeIndexChangeBatch.cs` | 折叠产物（重读集合 / 移除集合 / reconcile 标记） |
| `CodeIndexChangeWatchers.cs` | **U3-B1** 变更源抽象（`ICodeIndexChangeWatcher` / `ICodeIndexWatcherFactory`）+ 真实 watcher 适配工厂 |
| `CodeIndexMaintenanceService.cs` | **U3-B1** 变更→索引的单一驱动（消费批次、置脏补跑、removal/reconcile 可见化、有界停止） |

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
| `SqliteCodeIndexStore.cs` | SQLite 索引存储（实现 `ICodeIndexStore`） |

## 依赖

- 包：`Microsoft.Data.Sqlite`、`Microsoft.Extensions.Logging.Abstractions`
- **严禁**：`Microsoft.CodeAnalysis.*` / `Microsoft.Build*` / `Microsoft.Build.Locator`
- 工程引用：**无**（叶子）

## 驱动归属（ADR-089 §2，U3-B1 已落实）

- `CodeIndexScheduler` **不再自建 `Task.Run` worker**；由 `CodeIndexMaintenanceService`（单一驱动）显式泵。
- `CodeIndexMaintenanceService` **不实现 `IHostedService`**；Host 接线（DI + HostedService）属 **U3-B2**。
- 批次仅按 **scope 粒度**触发重索引；**按文件增量提交属 U3-B3**。`PathsToRemove` / `ReconcileRequired` 只做**可见化**（计数 + 状态 + 日志），不得视为已处理。

## 测试

**`../PuddingCodeIndexTests/`（本组件的独立测试工程 —— S2/S3 已兑现）**：只引用本工程，
58 用例（55 个组件用例 + 3 条边界断言），测试进程**不加载** Roslyn/MSBuild 与上层程序集。
`InternalsVisibleTo` **仅**对本组件的测试工程开放（**不得**对上层开放 —— 那是反向依赖）。

`../PuddingCodeIntelligenceTests/` 保留语言解析/查询/DI 等**上层**测试（89 用例）；
2026-09-23 实测：移除指向它的 `InternalsVisibleTo` 后仍 build+test 全绿，故该条目已删除。
