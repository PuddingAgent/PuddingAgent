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

`Source/PuddingFullTextIndexTests/` — 全文索引测试（60 项：通过 56 / 跳过 4）

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
