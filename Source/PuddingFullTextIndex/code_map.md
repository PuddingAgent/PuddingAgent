# PuddingFullTextIndex CodeMAP

> 通用全文索引引擎 | 文件内容提取 · 搜索
> 历史变更与门禁记录已迁至 [`Docs/00Changelog/2026Year/10/2026-10-02-PuddingFullTextIndex-code_map迁出的变更记录.md`](../../Docs/00Changelog/2026Year/10/2026-10-02-PuddingFullTextIndex-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

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
| `Maintenance/` | **局部维护（S3a/S3b/S3c/S3d）**：纯逻辑部分零 IO / 零线程；**循环本体（S3d）含真实 FSW 与专用体检线程**（生命周期严格受 Start/Stop 管辖）：`MaintenanceCheckpoint.cs`（checkpoint 模型 + 协议 JSON + 路径解析，**只经 `FullTextIndexPaths`**）· `MTimeComparison.cs`（`>=` 判定 / `effective = watermark - overlap` / `ComputeNextWatermark` 取**扫描开始**时刻 / 时钟回拨判定 / stat 稳定性）· `FullTextChangeCoalescer.cs`（per-path latest-wins / `Sources` 位或 / rename 折叠 / 越界拒绝）· `MaintenanceOptions.cs`（fail-closed 校验，默认全关）· `CheckpointAdvancePolicy.cs`（**决定「本轮要不要推进 checkpoint」的纯策略接缝**：`AllowsAdvance` / `Decide`，规则 `State==Applied && FailedCount==0 && RetainedOldCount==0`；给出可区分的阻止原因；文档注释内登记了**饥饿风险**——永久不可读文件 ⇒ checkpoint 永不推进、每轮重扫但不漏文件）· `QuotaEnforcingDirectory.cs`（**S3b 写入期配额硬限**：`FilterDirectory` 子类 + `IndexOutput` 计数代理，超限抛专用异常）· `IndexSizeReport.cs`（体积增长机器可读报告）· `IndexWriteQuotaExceededException.cs`（越界异常，携带文件名/已写字节/允许增长/预算/越界量）· `IndexRootWriteGate.cs`（**S3c 进程级 index-root 写者闸门**：按索引根规范键分桶，§4.2 末条「多 scope 增量提交默认全局串行」的落地）· `ScopeReaderInvalidation.cs`（**S3c 查询侧 reader 失效接缝** `IScopeReaderInvalidation` + 转调 `LuceneSearchEngine.InvalidateScope`；public 是因为引擎构造函数是 public，C# 不允许 public 成员暴露 internal 类型）· `LuceneFullTextIndexMaintenance.cs`（**S3d 维护循环本体** 1924 行：每 scope 一个 FSW + 一条泵任务；折叠缓冲 + 溢出计数并请求补偿；Startup/Interval/溢出/压力触发的 mtime 补偿扫描；checkpoint fail-closed 读 + 原子写；专用 `BelowNormal` 体检线程（退避 + 切片）；三源汇一后交执行层；**watcher 批次永不推进 checkpoint**（水位线语义 = 「最近一次成功补偿扫描的开始时刻」））· `MaintenanceCorpusScan.cs`（**S3d** 维护侧共用语料走查器：单次 DFS + 噪声剪枝 + 逐目录异常隔离 + 失败目录计数 `IsComplete` 是「能否算 Delete 候选」的唯一依据）· `IResourcePressureProbe.cs`（**S3d** 资源压力采样接缝，`null` = 不可采样 ⇒ 必须按繁忙退避；默认探针只采系统 CPU，**磁盘维度未采样**）· `LuceneFullTextIndexMaintenanceEngine.cs`（**S3a/S3b/S3c/S3d 真实 Lucene 局部写内核 + path inventory + 写入期配额硬限 + 跨进程租约/全局串行/commit 后失效 reader**：`ApplyChangesAsync` 单批 `CREATE_OR_APPEND`，**内容先提取→后 delete/add**、提取失败绝不进 delete 集合、单批 `Commit`、取消/Busy/quota 超限一律不提交；`CheckpointAdvanced` 是**产物** = `CheckpointAdvancePolicy.AllowsAdvance` **且** 输入的 `RequiresCheckpointAdvance`（**显式取交集**：策略为唯一真源，输入只能否决、不能强制为真）。`ProbeIntegrityAsync` **已落地**（S3d：只读三态 `ManualRebuildRequired` / `Healthy` / `Degraded`；索引目录不存在 / 不可读 / 段损坏 ⇒ `ManualRebuildRequired`，**绝不**通过局部写偷偷初始化、**绝不自动重建**；探针前后索引目录字节与文件数逐位相同）） |
| `FullTextPolicyFingerprint.cs` | **`.last_indexed.p` patterns 指纹的唯一真源**（S1b 从 `LuceneSearchEngine` 私有方法收敛而来）：`filePatterns ?? "(default)"` → SHA256(UTF-8) → 小写 hex → **前 12 字符**。**不得**改大写 hex / 改截断长度 / 换哈希（会**静默**让全部现存 `.last_indexed` 判为「patterns 变了」⇒ 触发全量重建）；类内**不含**路径命名哈希 |

## 配置

| 文件 | 用途 |
|------|------|
| `FullTextIndexOptions.cs` | 索引选项 |

## 测试

`Source/PuddingFullTextIndexTests/` — 全文索引测试（**268 项：通过 264 / 跳过 4**；S3d 前为 249，S3c 前为 238，S3b 前为 232，S3a 前为 222，S2b 前为 207，S2a 前为 192，S1b 前为 180，S1a 前基线为 146）
