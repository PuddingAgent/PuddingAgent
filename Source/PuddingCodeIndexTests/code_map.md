# PuddingCodeIndexTests CodeMAP

> **索引组件的独立测试工程**（组件化交付规程 S2/S3 的第一次真实兑现，2026-09-23）
> 目的：让 `PuddingCodeIndex` 的测试运行在**只引用它自己**的工程里
> —— 从而"**不重启宿主即可测试与开发**"（规程 R1），且测试进程**不加载** Roslyn / MSBuild / 上层程序集。
> 命名空间：`PuddingCodeIndexTests` / `PuddingCodeIndexTests.Services` / `PuddingCodeIndexTests.Services.CodeIndex` / `PuddingCodeIndexTests.Storage`

## 边界（编译期 + 运行期双重强制）

- `PuddingCodeIndexTests.csproj` 的 `ProjectReference` **恰好 1 条** → `../PuddingCodeIndex/PuddingCodeIndex.csproj`。
- **严禁**引用 `PuddingCodeIntelligence` / `PuddingRuntime` / `PuddingHost` / `PuddingAgent`，也不得引用其他组件或宿主。
- 测试框架/包版本与既有测试工程一致（`MSTest.Sdk/4.0.1`，`net10.0`，`UseVSTest`）；不新增 NuGet 依赖。
- 运行期断言见 `ComponentBoundaryTests`：本进程**已加载**、**元数据引用**、**探测路径可达**三个维度都不允许出现禁用程序集。

## 测试清单（98 用例 = 95 个组件用例 + 3 条边界断言；U4-2a 后 82 → 98）

| 文件 | 用途 |
|------|------|
| `ComponentBoundaryTests.cs` | **S3/S4 边界断言**：① 探测器自检（必须能报出违规）；② 测试进程未加载（并强制加载元数据引用）Roslyn/MSBuild/上层程序集；③ 声明的依赖闭包（`*.deps.json`）不含禁用程序集 |
| `Services/CodeIndexFixture.cs` | 组件本地夹具：临时根目录 + 真实 `SqliteCodeIndexStore`（**自足**，不用上层夹具） |
| `Services/CodeIndex/CodeIndexChangeCoalescerTests.cs` | 防抖折叠（静默 500ms / 最长 2s / 2 万路径 → reconcile） |
| `Services/CodeIndex/CodeIndexChangeQueueTests.cs` | 有界队列容量与 `TryPublish` 不阻塞 |
| `Services/CodeIndex/CodeIndexChangeTestHelpers.cs` | 共享测试替身：`TestDirectory` / `MutableTimeProvider` / `CodeIndexChangeTestFactory` |
| `Services/CodeIndex/CodeIndexMaintenanceServiceTests.cs` | **U3-B1** 变更驱动维护服务：单驱动泵、置脏补跑、有界停止；**U3-B3** 改写为按文件语义（单文件变更不触发全量重索引、删除不触发全量重索引） |
| `Services/CodeIndex/CodeIndexMaintenanceTestDoubles.cs` | 维护服务测试替身：`RecordingCodeIndexer`（含 U3-B3 的 `IndexFileAsync` 记账与拒绝注入）/ `FakeCodeIndexChangeWatcher` / `MaintenanceTestData` |
| `Services/CodeIndex/MaintenanceHarness.cs` | **U3-B3** 共享驱动夹具（真调度器 + 真 store + 假变更源，与管理服务测试共用） |
| `Services/CodeIndex/CodeIndexRemovalCorrectnessTests.cs` | **U3-B3** 管线级施用：删除后**查询不再返回**该文件符号（A6 硬判据，改动前为红）、重命名旧清新增、消失路径按删除处理、索引器拒绝 ⇒ 升级 scope 级重索引 |
| `Services/CodeIndex/CodeIndexSchedulerTests.cs` | **U3-B1** 调度器不变量：in-flight 期间到达的请求不得丢弃、取消时重新入队 |
| `Services/CodeIndex/CodeIndexScopeStateTests.cs` | 范围状态（dirty/version/reconcile，无 IO） |
| `Services/CodeIndex/CodeIndexWatcherTests.cs` | 文件系统监视器过滤与发布 |
| `Storage/SqliteCodeIndexStoreTests.cs` | `SqliteCodeIndexStore` 项目/文件/符号/关系/引用往返，且不触碰源文件 |
| `Storage/SqliteCodeIndexStoreRemoveFilesTests.cs` | **U3-B3** 按文件清除：文件记录 + 符号 + 关系/引用均消失且不动其他文件；幂等；**批次中途失败 ⇒ 一字不删**（触发器具确定性注入） |

## 运行

```
dotnet test Source\PuddingCodeIndexTests\PuddingCodeIndexTests.csproj
```

**不需要**启动 Core / Desktop / 任何后台服务；可在宿主运行中并发执行（不争宿主文件锁）。

> ⚠️ `ComponentBoundaryTests` 会读输出目录的 `*.dll`（与 `*.deps.json` 取交集）。
> 增删引用后请让 `bin/obj` 重新生成，避免陈旧产物造成假红。

## 来源与守恒

本工程 55 个用例由 `Source/PuddingCodeIntelligenceTests/` 迁入（`Services/CodeIndex/` 8 文件 +
`Storage/SqliteCodeIndexStoreTests.cs`），**零用例丢弃**：
`搬迁前 144 = 搬迁后 89（IntelligenceTests）+ 55（本工程）`。
U3-B3 在本工程新增 8 个用例（5 管线 + 3 存储），并把 
`MaintenanceHarness` 提为共享夹具。
**U4-2a**（2026-09-24）在本工程新增 16 个用例（`Contracts/Retrieval/` 10 文件）：
`RetrievalIntentContractTests` / `RetrievalResultContractTests` / `RetrievalRankingContractTests` /
`RetrievalCapabilityContractTests` / `RetrievalBudgetContractTests` / `RetrievalOverloadContractTests` /
`RetrievalEmptyReasonContractTests` / `RetrievalDeduplicationContractTests` / `RetrievalFilterContractTests` /
`RetrievalTestData`（共享构造器）；**零用例丢弃**（82 → 98）。

## U4-2a 更新（2026-09-24）— 检索意图与结果合同的契约测试（82 → 98 用例）

**新增 10 文件（`Contracts/Retrieval/`）**：新增 **16 个用例**，全部只引用 `PuddingCodeIndex`（边界未击穿）。

| 文件 | 覆盖 |
|------|------|
| `RetrievalTestData.cs` | 共享构造器（命中/请求/落盘/建议/小体积页）；刻意只依赖本组件 |
| `RetrievalIntentContractTests.cs` | **A01** 缺省即 Auto（`(int)Auto == 0`、`default` 即 Auto、成员集合 12 值冻结）；**A07** 显式 intent 的层序**不被 Auto 改写**（7 层全序 ×12 张表 + 成文表逐条冻结 + 行为证明 + intent→过滤表） |
| `RetrievalResultContractTests.cs` | **A02** "hits=0 无 reason"不可表示（无公开构造函数 + 工厂拒绝 + 反射结构证明）；**A03** 降级必须带原因（反向亦然）；**A04** 至多一条下一步（**语义选"失败"**，不静默截断）；**A09** 不得静默截断（真实总数 + 游标 + 落盘 + 非空分布 + 落盘路径守卫负面对照 + `totalCount` 无默认值） |
| `RetrievalRankingContractTests.cs` | **A05** 显式全序与确定性（末级身份键承重 + 3 次打乱逐位相同 + 逆序必被 `EnsureOrdered` 拒绝）；**A05b** 多 scope 佐证加权确定性/单调/去重 |
| `RetrievalCapabilityContractTests.cs` | **A06** 能力矩阵诚实（不支持 ⇒ 必须给替代方案；端口形状冻结；替换实现替身；结果层不得把能力缺口报成真空/过泛） |
| `RetrievalBudgetContractTests.cs` | **A08** 单次返回有界（条数 ≤ PageSize、字节/token 双预算未超、超预算构造即拒、页大小有界） |
| `RetrievalOverloadContractTests.cs` | **A10** 过载即信号（Low 三原因 + 分布附加信号 + 诊断自洽 + **过载但未截断仍须恰好一条收窄建议** + **③ 误报守卫**：正当的多与紧凑高语义量都不得报 Low）；**A11** 收窄手段恰好四类 + 载荷与种类一致 |
| `RetrievalEmptyReasonContractTests.cs` | **A12** 过滤性空 ≠ 真空（FilteredOut + "放宽哪个面" + 放宽面必须在生效面内 + 带过滤面报"确实不存在"⇒ 拒绝 + 裁决优先级成文） |
| `RetrievalDeduplicationContractTests.cs` | **A13** 跨 scope 去重先于过载判定（**首选主张先断言**：去重前 Low / 去重后不 Low；再去重后计数与 scope 元数据；嵌套/重叠 scope 检测 + 三重负面对照 + 结果构造拒绝未去重序列）；**A14** 双视图各带命中原因摘要 |
| `RetrievalFilterContractTests.cs` | **A15**（额外）过滤面正交且 AND 组合：**只关注类名称 ⇒ 签名文本命中的 `.ctor` 被过滤掉**（§2.3 实测动机）；目录/扩展名/置信度/命中层各面；空洞阀门（`None` 匹配域、空扩展名、关递归无目录）；规范化与回显；生效面顺序成文 |

**变异取红（M1~M9，每项均已复原且 `git hash-object` 与变异前逐位相同）**：M1→A01、M2→A02、M3→A04、M4→A05、M5→A09、M6→A09、M7→**A10③ 误报守卫**、M8→**A13③**、M9→A12；每次取红只有目标用例变红（97/98），复原后 98/98。

**运行**：`dotnet test Source\PuddingCodeIndexTests\PuddingCodeIndexTests.csproj`（无需宿主、不加载 Roslyn/MSBuild）。

> ⚠️ 复原文件时若用 `Copy-Item` 覆盖，PowerShell 会保留**备份文件的旧时间戳** ⇒ MSBuild 认为源文件比输出更旧而**跳过重编译**，于是"复原后跑测试"会读到上一次变异的 DLL（本刀实测踩到：`u4-2a-mut-M1-green*.txt`）。复原后必须刷新 `LastWriteTime`（或清 `bin/obj`）再判定绿。
