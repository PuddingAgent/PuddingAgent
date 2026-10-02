# 从 Source/PuddingHost/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）

> **为什么在这里**：`Source/PuddingHost/code_map.md` 只保留索引（关键概念 · 组件 · 关键文件 · 用途）。原先按轮次/日期堆叠在其中的变更、门禁与验收记录迁出到本文件。日志规则见 `Docs/00Changelog/README.md`。
>
> **内容来源**：迁出前 `Source/PuddingHost/code_map.md` 的原文，**逐字保留，未做删改**（节之间仅插入 `---` 分隔，不改变任何原文字）。原文件快照可 `git show <迁出前提交>:Source/PuddingHost/code_map.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（链接文字未变）；正文反引号内的路径仍保持原文的仓库根口径。
> **覆盖范围**：原文件第 74–141 行，共 4 节；日期 2026-09-23 ~ 2026-09-30。

**〔原文第 74–82 行：U3-B2a（2026-09-23）— 代码索引维护的宿主生命周期驱动〕**

## U3-B2a（2026-09-23）— 代码索引维护的宿主生命周期驱动

| 文件 | 用途 |
|------|------|
| `Hosting/CodeIndexMaintenanceHostedService.cs` | 🔑 U3-B2a：索引维护组件的**唯一生命周期驱动**。`StartAsync` 非阻塞启动组件驱动（`ICodeIndexMaintenance`），并把「已注册 scope」挂上变更源 —— 附着在启动路径之外（`ScopeAttachmentCompleted` 可观测），失败只记日志，无 scope 时安全 no-op；`StopAsync` 有界（外层 10s 上限）且不抛异常逃逸、不丢已入队请求。**本身不含任何循环 / 队列 / 索引逻辑**：泵与变更捕获留在 `PuddingCodeIndex`（ADR-089 §2 驱动归属）。注册点：`PuddingServiceCollectionExtensions.Platform.cs` 紧邻 `AddPuddingCodeIntelligence()`。 |

组合根同时新增 `../Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs`（3 用例）：驱动可解析、泵端口与 `ICodeIndexScheduler` 同实例、驱动已注册且持有同一实例、无 scope 时安全 no-op、`Enqueue → 泵 → ICodeIndexer`（替身计数）闭合、已注册 scope 被挂上变更源。

顺带修复：`Storage/StorageMaintenanceServiceTests.cs` 与 `Storage/StorageManagementAdministrationTests.cs` 各补 1 行 `using PuddingCodeIndex.Contracts;` —— 此前 `ICodeIndexScheduler` 已迁出 `PuddingCodeIntelligence.Contracts`，整个 `PuddingHost.Tests` 编排期编译不过。

---

**〔原文第 84–99 行：U4-7（2026-09-25）— 全文索引「供给参数」配置化 + fail-closed 校验（**默认关闭**）〕**

## U4-7（2026-09-25）— 全文索引「供给参数」配置化 + fail-closed 校验（**默认关闭**）

用户裁定（2026-09-25）：「1GB 请使用配置文件确定参数，方便后期替换为 XXGB……用**项目目录统计 json** 或 **Data 目录配置文件**决定，而不是选择一个固定值。」
⇒ 本刀取 **Data 目录配置文件**：`<DataRoot>/config/system.json` 的 `FullTextIndex` 节（宿主 `PuddingApplicationHost.CreateBuilder` 已加载它并支持 hot reload）。

| 文件 | 用途 |
|------|------|
| `Hosting/FullTextIndexSupplyOptions.cs` | 🔑 供给参数类型（节名 `FullTextIndex`）：`Enabled`（**默认 `false`**）/ `Scopes`（目标目录，可为绝对或相对 `WorkspaceRoot`）/ `WorkspaceRoot`（相对项的**显式绝对基准**，**禁用进程 CWD**）/ `MaxIndexBytes`（**默认 `1_073_741_824` = 1 GiB = 2^30**；硬天花板 `1L << 40` = 1 TiB）/ `MinRebuildInterval`（默认 12h，JSON 写 `"hh:mm:ss"`）。**单一真源**：1 GiB 字面量全仓生产代码只在此出现一次。 |
| `Hosting/FullTextIndexSupplyResolver.cs` | 🔑 fail-closed **纯函数**校验（不依赖 DI / 文件系统 / 网络）：关闭 ⇒ 成功且空动作、**连 scope 探针都不调用**；开启而 `Scopes` 为空 / 含空串·不存在·非目录·重复项 / 相对项无绝对基准 / `MaxIndexBytes <= 0` 或超 1 TiB / `MinRebuildInterval < 0` ⇒ **结构化拒绝**（`ParameterName` + `Value` + 原因枚举 + 可读消息），且 accepted / rejected **分别列出**。scope 探针是三态委托（`Missing`/`NotDirectory`/`Directory`），可注入替身 ⇒ 单测零文件系统访问。 |
| `Hosting/IndexPrebuildFreshness.cs` | `MinRebuildInterval` 的消费点（纯函数、注入时钟）：无索引 / 索引过旧 / 间隔 ≤ 0 ⇒ 重建；索引足够新 ⇒ 跳过。⚠️ 仪器口径 = **索引根目录 mtime**（per-scope 索引目录在 `PuddingFullTextIndex` 内且 `internal`）⇒ 粗粒度代理，已在交付报告登记留白。 |
| `Services/IndexPrebuildService.cs` | 由配置门控的预建服务（此前是 `HOSTED-DISABLED`）。`StartAsync` **永不阻塞宿主**；**默认配置下不建索引、零索引 I/O、不记 Error**；校验不过 ⇒ 记 Error 且什么都不做（fail-closed，不部分生效）；通过 ⇒ 启动路径之外按 `Scopes` 逐个预建 —— **目标来自配置，不再是 `Directory.GetCurrentDirectory()`**（历史缺陷）。 |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 接线：`Configure<FullTextIndexSupplyOptions>(builder.Configuration.GetSection(FullTextIndexSupplyOptions.SectionName))`。**必须是 `builder.Configuration`**：`bootstrapConfiguration` 只含 appsettings/环境变量，`system.json` 只加在 `builder.Configuration` 上。同处把 `IndexPrebuildService` 从 `HOSTED-DISABLED` 注释改为常驻注册（默认配置下等价 no-op，现网行为不变）。 |

测试（`../Tests/PuddingHost.Tests/Hosting/`，**20 用例**）：`FullTextIndexSupplyResolverTests`（A1~A5 + 相对/绝对基准 + 零间隔边界，10 条）、`IndexPrebuildServiceTests`（A6 + 开启路径 + 拒绝可观测，3 条）、`IndexPrebuildFreshnessTests`（5 条）、`FullTextIndexSupplyHostBindingTests`（system.json → IOptions 绑定 + hosted 注册 + 无该节即默认关闭，2 条）。证据与变异输出见 `temp/U4-7-REPORT.md`、`temp/u4-7-evidence/`。

⚠️ **留白（R5）**：体积护栏的**执行**行为（达 `MaxIndexBytes` 时告警 / 拒写 / GC）**未实现**，属后续切片（ADR-089 §7.2「触发 GC 留到后续切片」）。本刀只提供参数与校验。

---

**〔原文第 101–121 行：S5（2026-09-25）— 预建索引改走「协调器 + 暂存供给」（**默认关闭 ⇒ 零 I/O**）〕**

## S5（2026-09-25）— 预建索引改走「协调器 + 暂存供给」（**默认关闭 ⇒ 零 I/O**）

U4-7 的预建**直写** `IFullTextSearchEngine.BuildIndexAsync`，且用**索引根 mtime** 判所有 scope 的新鲜度。
S5 把两条都换掉：写路径统一进组件协调器（跨进程租约 / 幂等合并 / 预算硬限 / staging / 原子切换），
新鲜度改为 **per-scope**（该 scope **自己**的 live 索引目录）。

| 文件 | 用途 |
|------|------|
| `Hosting/IFullTextIndexSupplyComposition.cs` | 🔑 宿主侧供给端口：`IFullTextIndexSupplyComposition`（`Coordinator` / `ComponentOptions` / `LiveIndexLastWriteUtc`）+ `IFullTextIndexSupplyCompositionFactory`。**为什么用工厂**：R4 要求 `Enabled=false` 时「不解析 scope、**不构造协调器组合**、不 touch 索引根」—— 协调器 / staged builder / 清点 / 租约的实例化被推迟到真正进入供给路径之后（默认关闭时永不发生）。 |
| `Hosting/LuceneFullTextIndexSupplyCompositionFactory.cs` | 生产装配：`FileSystemSupplyInventory` + `StagedFullTextIndexBuilder`（live 引擎 = **查询侧同一实例**，否则 reader 失效打空）+ `FileSupplyLease` + `FullTextIndexSupplyCoordinator`；**配置流入组件**（R2）：`DefaultBudgetBytes = FullTextIndexSupplyOptions.MaxIndexBytes`、`MinRebuildInterval = MinRebuildInterval` —— 宿主侧「1 GiB」仍只有 `FullTextIndexSupplyOptions.DefaultMaxIndexBytes` 一处真源，组件常量退化为未接线时的兜底。**per-scope 新鲜度**（R3）= `IFullTextIndexRootedEngine.ResolveIndexDirectory(scope)` 指向的 **live 索引目录** mtime（组件内的映射单一真源；`SupplyIndexDirectoryLayout` 仍是 internal，宿主**不复刻**哈希规则）。 |
| `Services/IndexPrebuildService.cs` | 写路径改为「提交 `SupplyScopeRequest` → 轮询 `GetStatusAsync` 到终态」（`StartupDelay` / `StatusPollInterval`(默认 250ms) / `BuildWaitTimeout`(默认 **30min 上限**) 可注入；**超时只停止观测、不取消 job、不重试**）；`Busy` / `Rejected`(含 OverBudget) / `Failed`(含 RolledBack / 切换失败，原因原文照登) / 超时**逐类如实记录**（带 jobId）；**每个 scope 最多提交一次**。引擎只剩**只读**用途（`HasIndex`）。 |
| `Hosting/IndexPrebuildFreshness.cs` | 注释口径修正：新鲜度输入从「索引根 mtime」改为**该 scope 自己的 live 索引目录 mtime** —— U4-7 登记的「粗粒度代理」留白就此关闭（旧口径下建出任一 scope 就会把其余 scope 集体误判为新鲜）。 |
| `Extensions/PuddingServiceCollectionExtensions.Runtime.cs` | 新增 `AddSingleton<IFullTextIndexSupplyCompositionFactory>(…)`：取 `FullTextIndexOptions` + 断言引擎实现 `IFullTextIndexRootedEngine`（staged 切换需要「语料根 → 索引目录」映射与 reader 缓存失效两个接缝），staging 引擎工厂 = `new LuceneSearchEngine(stagingOptions)`。 |

测试（`../Tests/PuddingHost.Tests/Hosting/`，宿主 **154 用例**；S5 新增 8 条）：
`S5IndexSupplyHostWiringTests`（A1 默认关闭零 I/O・工厂/协调器/引擎 0 调用・索引根不建；A2 **真实 Lucene** 端到端可查询 + live 引擎**零直写**；A3 OverBudget 如实记录且 live 逐字节不变、旧索引仍可查；A4 **真实跨进程文件租约** Busy ⇒ 只提交一次、记录 owner/PID、继续下一 scope；A5 per-scope 新鲜度只提交陈旧者（A 新鲜只跳 A）；A6 配置流入组件；轮询超时有界）、
`S5FullTextIndexSupplyHostCompositionTests`（组合根：`system.json` → 组件策略 + `IFullTextIndexRootedEngine` 接缝成立，全程零索引写入）、
`S5SupplyTestDoubles.cs`（夹具与替身）、`IndexPrebuildServiceTests`（U4-7 三条按 S5 语义适配）。
证据：`temp/S5-REPORT.md`、`temp/s5-evidence/`。

⚠️ **边界**：CLI 与其它工程一律未改（组件零改动）；**重启后的运行态验证由父级执行**（本刀禁止重启任何进程）。

---

**〔原文第 123–141 行：变更（2026-09-30，启动阶段埋点 `StartupPhaseTracker`：让「启动耗时」可归因 · 组件 + 宿主入口）〕**

## 变更（2026-09-30，启动阶段埋点 `StartupPhaseTracker`：让「启动耗时」可归因 · 组件 + 宿主入口）

**动机（实测）**：Core 启动耗时 **21.5 s**（Desktop 显示口径 = Core 进程 `ReadyAt − StartedAt`，见 `Source/PuddingDesktop/MainWindow.xaml.cs:123`），
而系统日志文件 `D:\data\logs\system\pudding-*.log` 的**第一行**出现在进程启动后 **17.5 s**（07:00:31 进程启动 vs 07:00:48.460 首行）
⇒ **日志管线建立之前的那段启动时间无法从任何日志归因**。

**新增**：`Hosting/StartupPhaseTracker.cs` —— 纯逻辑（时钟与输出通道可注入；无文件 IO / 无静态可变状态 / 无线程）
+ `StartupPhases` 阶段名常量（唯一真源）。每个阶段点输出一行 `[StartupPhase] <name> total=<N>ms delta=<N>ms`，
同时进入 **stdout**（Desktop 捕获；日志管线就绪前唯一可用通道）与 **Serilog**（落系统日志文件）。

**打点位置（11 处，已用 grep 复核）**：`Program.cs` 6 处（process-start / options-resolved / data-root-lease / initialized / server-started / ready）；
`CreateBuilder` 3 处（data-root-bootstrapped / logging-ready / services-registered）；`Build` 2 处（host-built / middleware-mapped）。
`CreateBuilder` / `Build` / `InitializeAsync` 新增**可选**参数 `StartupPhaseTracker? phases = null` ⇒ 既有调用点零改动。

**验证**：新增 xUnit 用例 4 个全绿；宿主全量 **159/159**（failed 0）；M1（`Format` 去掉 delta）⇒ 红点恰好 `Mark_WritesExactlyOneLinePerMark_InOrder_ThroughTheSink`；
M2（删掉单调兜底）⇒ 红点恰好 `Mark_ClampsNegativeDelta_WhenClockMovesBackwards`（Expected 500 / Actual 100）；`MUTATION` 残留 **0**（活对照 14 / 20）。

**未证实**：① 埋点真实触发需下次 Core 启动才可见（本次 Core 运行中，`Source/PuddingAgent/bin/Debug/net10.0` 被 PID 26676 锁定 ⇒ 30×MSB3021/3027、CS 错误 0）；
② 复原后宿主 DLL 哈希未回到基线（`57E70653…` → `F1D19D2F…`），原因未证实 ⇒ 不声称逐位复原。

