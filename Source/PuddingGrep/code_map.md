# PuddingGrep CodeMAP

> 源指纹: Source/PuddingGrep/**=5-d1c01e38c26b · 条目数: 5 · 最近整理: 2026-11-22
> 定位：统一 grep 的独立匹配内核（ADR-089 R2-00）。.NET 10 类库，只引 BCL。
> 只回答一个问题：这一行是否命中、命中在哪；运行时目录与构建入口只在根 code_map.md。

## 入口与配置

刻意不知道：文件系统、目录排除、扩展名过滤、候选索引、Provider、最大结果数——那些属于扫描/编排层。
混进来就会让「同一语义在不同工具里各写一遍」的老问题复发（R1-01 要收口的那类缺陷）。
本工程零 `ProjectReference`、零 `PackageReference`，因此可在无宿主、无索引的情况下单独构建与测试（规程 S1–S4）。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GrepPattern.cs` | 匹配意图的构造期校验（字面量或正则 + 大小写） | `GrepPattern` / `Literal` / `Regex` | `GrepMatcher.cs` | 空模式与非法正则必须在构造期抛 `ArgumentException`，不得留到运行时变成静默不命中 |
| `GrepSpan.cs` | 行内命中位置，不含文件与行号 | `GrepSpan` | `GrepLineMatch.cs` | — |
| `GrepLineMatch.cs` | 一行匹配的三态结果与其 span 集合 | `GrepLineMatch` / `GrepMatchOutcome` | `GrepSpan.cs` | `Timeout` 不得被折成未命中——否则「没法判」会被读成「确实没有」 |
| `GrepMatcher.cs` | 逐行匹配：布尔判定与三态匹配 | `GrepMatcher` / `IsMatch` / `FindAll` / `Match` / `DefaultMatchTimeout` | `GrepPattern.cs` | 字面量走 `IndexOf`（Ordinal 系），正则加 `CultureInvariant` 与 2 秒超时防灾难性回溯；`FindAll` 不允许重叠 |
| `PuddingGrep.csproj` | 项目文件：目标框架与零依赖声明 | — | — | 零 `ProjectReference`、零 `PackageReference`，只引 BCL；加任何引用即破坏组件边界 |

## 边界（S4 运行时断言）

`PuddingGrepTests/ComponentBoundaryTests` 用反射断言：本程序集不含任何 `Pudding*` 引用；
测试工程不含 Runtime / CodeIndex / CodeIntelligence / Host / Platform / FullTextIndex 引用。
匹配语义以 BCL 当 oracle 逐条对照（`IndexOf` / `Regex` / `Regex.Matches`）。

## 接入状态

- 已登记进 `PuddingAgentNetwork.slnx`，全仓构建与门禁会真的构建并运行它；登记属构建门禁，不是运行时接入（S1–S4 已先通过）。
- 未注册 DI、未替换 `Runtime/SearchGrepTool` 的匹配逻辑——那才是 S5 的运行时部分，需部署验证。
- 扫描层（文件枚举、排除目录、扩展名、候选索引、`maxResults` 与预算）不在本组件内（R3）。

## 已知并存（待裁定）

生产端当前零引用：全仓 csproj 扫描只有本工程与 `PuddingGrepTests` 提到它。
`Runtime/SearchGrepTool` 用的是另一个匹配内核 `PuddingCore/Tools/Retrieval/RetrievalMatcher`，
于是仓库里有两个 grep 匹配内核并存，语义尚未收归一处。
两者差异（`RetrievalMatcher` 给可判别的 `Timeout`，本工程给 span 定位）与前置条件见
`Docs/15_tasks/2026-11-01-grep匹配内核二选一-决策输入.md`——待裁定，不要直接接线。

## 测试

`dotnet test Source\PuddingGrepTests\PuddingGrepTests.csproj`（独立工程，46 条）。
另可用 `dotnet test PuddingAgentNetwork.slnx --filter FullyQualifiedName~PuddingGrepTests` 走解决方案入口。
