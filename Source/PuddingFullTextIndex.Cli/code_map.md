# PuddingFullTextIndex.Cli CodeMAP

> 全文索引**供给**的离线驱动工具（A3 切片）| 把 A1 的供给协调器变成可显式驱动的命令

## 用途

把 `Source/PuddingFullTextIndex`（A1 供给协调器）从"库"变成"可显式驱动的工具"：
干跑估算（`plan`）、如实观察（`status`）、真实构建（`build`）、跨进程取消（`cancel`）。
**不进解决方案**（`PuddingAgentNetwork.slnx` 留给 S5 干净窗口），独立构建/测试。
组件没有 DI 文件 ⇒ 本 CLI **自行组装**组合根（`SupplyCliHost`），不引入 DI 容器、不新增 NuGet。

## 命令与退出码

| 命令 | 作用 | 关键约束 |
|------|------|----------|
| `plan --scope <绝对路径>... [--budget-bytes <n>] [--index-root <路径>] [--json]` | 干跑估算（FileCount / CorpusBytes / PredictedIndexBytes / WithinBudget + 逐条拒绝） | **零写入**（不建索引目录、不写租约、不触发构建） |
| `status [--job <id>] [--scope <路径>]... [--index-root <路径>] [--json]` | 如实报告**磁盘可观察**状态：owner、本进程 job 台账、每 scope 的 `HasIndex` / 索引目录条目数与字节数 / 租约持有者 | `--job` 在本进程查不到 ⇒ 逐字输出 `not_in_this_process`，**不**外推状态 |
| `build --scope <绝对路径>... --index-root <路径> [--budget-bytes <n>] [--wait] [--json]` | 真实 `LuceneSearchEngine` + `FileSystemSupplyInventory` + `FullTextSearchEngineIndexBuilder` + `FileSupplyLease` ⇒ `BuildAsync`；`--wait` 轮询到终态 | `--index-root` **必填**（防误写生产索引根）；`IndexedFileCount/TotalBytes/ElapsedMs` 来自 builder 自报（`metrics-source = builder`） |
| `cancel --job <id> [--json]` | 取消本进程 job | 本 CLI 每次调用都是新进程 ⇒ 外来 job 必然 `not_in_this_process` + 非零退出（**不静默成功**） |

退出码：`0` 成功 · `2` 被拒（Rejected）· `3` 本进程无法完成（Busy / job 不在本进程 / `--wait` 超时）·
`4` 执行未成功（Failed / Cancelled / 未预期异常）· `5` 用法错误（未知命令 / 缺必填参数 / 非法数值 / 该命令不支持的选项）。

## 文件清单

| 文件 | 作用 |
|------|------|
| `Program.cs` | 进程入口：只把 `args` + 标准输出/错误交给 `SupplyCli.RunAsync` |
| `SupplyCli.cs` | 核心：命令分派、退出码契约、`--wait` 轮询、磁盘只读观察（`ObserveDirectory`） |
| `SupplyCliViews.cs` | 输出层：JSON DTO（camelCase）+ 人类可读 `key = value` 渲染 |
| `SupplyCommandLine.cs` | 手写参数解析 + 用法文本；非法组合一律判用法错误（不默默忽略） |
| `SupplyCliHost.cs` | 组合根：可注入装配面 + `RecordingIndexBuilder`（留住 builder 的结构化度量） |
| `SupplyScopeMirror.cs` | 组件内部口径的镜像（**A19 后只剩** `ToScopeKey` / `NormalizeRoot`；索引目录名改调组件 `FullTextIndexPaths`，不再复刻） |

## 与组件实现的耦合点（须警惕）

A19 之后 `SupplyScopeMirror` **不再计算索引目录名**：`SupplyCli.ObserveScopeAsync` 改调组件单一真源
`PuddingFullTextIndex.Infrastructure.FullTextIndexPaths.ResolveIndexDirectory`。CLI 侧只剩 `ToScopeKey` 一处复刻
（镜像 `SupplyScopeNormalizer.ToScopeKey`）；`NormalizeRoot` **刻意保留** —— 它与组件 helper **语义不同**
（保盘根 `C:\`、不改大小写，返回值参与显示与 scopeKey，见 `SupplyScopeMirror` 类注释）。

漂移仍会让 `status` 报告错误的 scopeKey / 索引目录，因此由**真实构建的交叉断言**守护：
- `SupplyCliBuildTests`：① `build --json` 的 `scopes[0].scopeKey`（来自协调器内部规范化）必须与**冻结金标准**
  `ScopeMirrorGolden`（A19 之前镜像的逐字副本，现位于 `Source/PuddingFullTextIndex.Cli.Tests/ScopeMirrorGolden.cs`）一致；
  ② 真 Lucene 构建后，金标准解析出的索引目录必须**就是**磁盘上引擎实际建出的那个目录。
- `A19SingleSourceTests`：CLI 打印值 == 组件 helper == 引擎 `ResolveIndexDirectory` == 引擎 `ProbeDocuments` 解析目录
  == 旧镜像金标准（6 类边界输入）；`A4` 结构性断言「生产工程内不得再出现 `SHA256.HashData` / `ToUpperInvariant` /
  `Convert.ToHexStringLower`」（冻结副本在测试工程，属**豁免对象**，并由 A4 反向断言其仍存在）。

## 门禁

- 独立构建：`dotnet build Source/PuddingFullTextIndex.Cli -c Release`
- 独立测试：`dotnet test Source/PuddingFullTextIndex.Cli.Tests -c Release`
- 红线：**不得**写 `D:\data\fulltext-index`（所有构建/测试显式传 `--index-root` 到 `Path.GetTempPath()` 下的临时目录）；
  测试夹具在构造函数里断言根路径以 `Path.GetTempPath()` 开头。

## 边界（本切片未做）

- 不接宿主（S5/A4）、不登记进任何 `*.slnx`；跨进程 job 台账 / 跨进程取消属后续切片。
- A2 的 staging / 预算硬限 / 成功后原子切换一律未做，且**不得**在本 CLI 里补（本 CLI 只驱动 A1 的既有入口）。
