# PuddingPathFiltering — code map
> 源指纹: *.cs=8-630ed46ca9b7, *.csproj=1-caa3bfbb7fba · 条目数: 16 · 最近整理: 2026-10-14

> ADR-089 U4-4 交付的**叶子组件**：路径忽略合同的唯一真源。
> `ProjectReference = 0`、`PackageReference = 0`（由 `PuddingPathFilteringTests/ComponentBoundaryTests` 读盘断言）。

## 定位

把「哪些路径被排除」从**七套互不一致的私有清单**收敛成一份定义：

- **名字级噪声名单**（制成品 / 依赖 / IDE / 工具产物）= `PathNoiseRules`
- **.gitignore 语义**（与真实 `git check-ignore` 逐条一致）= `IgnoreFileParser` + `IgnoreStack` + `GitWildcard`

索引侧（`PuddingCodeIndex` / `PuddingCodeIntelligence` / `PuddingFullTextIndex` / `PuddingCodeIndexer.Cli`）、
工具侧（`search_grep` / `list_dir` / `project_map`）、目标检查（`PuddingPlatform.GoalCheckInputIdentity`）
全部从本组件派生。

## 纯逻辑边界（S4 边界断言）

判定链**零 I/O**（`ComponentBoundaryTests.Decision_chain_does_not_touch_the_file_system` 静态扫描源码）：

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GitWildcard.cs` | git `wildmatch` 的等价实现 | `GitWildcard` | `IgnoreRule.cs` | `*` 与 `?` 不跨 `/`；非边界 `**` 必须退化为 `*`；未闭合 `[` 判不匹配 |
| `PathText.cs` | 分隔符归一与相对路径判定 | `PathText` | `WorkspacePathFilter.cs` | — |
| `IgnoreRule.cs` | 一条已解析规则：取反、目录专属、锚定与基准目录 | `IgnoreRule` | `IgnoreFileParser.cs` | — |
| `IgnoreFileParser.cs` | .gitignore 的行级语义解析 | `IgnoreFileParser` | `IgnoreRule.cs` | 行尾空格要裁剪但转义空格保留；行首空格有意义 |
| `IgnoreStack.cs` | 忽略栈：祖先目录剪枝优先，后命中者胜出 | `IgnoreStack` | `GitIgnoreFileLoader.cs` | 父目录被排除时取反救不回 |
| `PathNoiseRules.cs` | 名字级噪声名单的 D2 canonical 定义 | `PathNoiseRules` | `WorkspacePathFilter.cs` | 绝对路径必须先折算再调 `IsNoisePathBelow`，否则宿主自身 `%TEMP%` 的 `temp` 段会命中 |
| `WorkspacePathFilter.cs` | 消费者唯一入口：名字级噪声 ∪ 忽略栈 | `WorkspacePathFilter` | `PathNoiseRules.cs` | — |
| `GitIgnoreFileLoader.cs` | 唯一触盘类型：广度优先收集 `<root>/**/.gitignore` | `GitIgnoreFileLoader` | `IgnoreStack.cs` | 必须跳过噪声目录，否则读盘退化为全树扫描 |

## 为什么自研而不是用 NuGet

候选轮子：`Ignore` 0.2.1（MIT，`netstandard2.0`+`net8.0`，无依赖，58★，最后推送 2024-07-18，6 个 open issue，
`nuspec` **无 license 元数据**）。裁决：**自研**。理由：验收标准是「与真实 git 逐条一致」，
用包也必须建同一套 oracle；而包会把一条供应链 + 离线还原面塞进索引热路径，且父目录剪枝、
跨 .gitignore 优先级、`core.ignoreCase` 折叠都仍需自建组合层。证据与推理见 `temp/U4-4-REPORT.md` §D3。

## 测试（`Source/PuddingPathFilteringTests/`，84 用例）

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `GitIgnoreOracleTests.cs` | 冻结语料 196 条路径与 `git check-ignore -v` 逐条比对 | `GitIgnoreOracleTests` | `GitWildcard.cs` | 必须同时断言结论与定案规则原文，否则规则选错仍绿 |
| `RepositoryRootReductionTests.cs` | canonical 集合对头部噪声目录的覆盖率 | `RepositoryRootReductionTests` | `PathNoiseRules.cs` | 覆盖率低于实测 83.3% 即取红 |
| `GitWildcardTests.cs` | 通配符语义逐条锁 | `GitWildcardTests` | `GitWildcard.cs` | — |
| `IgnoreFileParserTests.cs` | 行级语义逐条锁 | `IgnoreFileParserTests` | `IgnoreFileParser.cs` | — |
| `IgnoreStackTests.cs` | 忽略栈的优先级逐条锁 | `IgnoreStackTests` | `IgnoreStack.cs` | — |
| `GitIgnoreFileLoaderTests.cs` | 忽略文件装载与作用域 | `GitIgnoreFileLoaderTests` | `GitIgnoreFileLoader.cs` | — |
| `PathNoiseRulesTests.cs` | 噪声名单的段名与大小写行为 | `PathNoiseRulesTests` | `PathNoiseRules.cs` | — |
| `ComponentBoundaryTests.cs` | 零引用、判定链无 `System.IO`、叶子不 `using Pudding*` | `ComponentBoundaryTests` | `PuddingPathFiltering.csproj` | 判定链一旦引入 `System.IO` 必须取红 |

### 关键语义

「测试」表「语义逐条锁」行的完整文件清单（原单元格 133 字符，超「文件 / 目录」列 120 上限）逐字下沉如下：

`GitWildcardTests.cs` / `IgnoreFileParserTests.cs` / `IgnoreStackTests.cs` / `GitIgnoreFileLoaderTests.cs` / `PathNoiseRulesTests.cs`

`Fixtures/` 是**冻结的 oracle**：`*.gitignore.txt`（模式源，故意不叫 `.gitignore`，否则会开始忽略仓库里 Fixtures 下的路径）
+ `*-oracle.tsv`（git 判定，列：路径 / 是否目录 / 结论 / 来源 / 行号 / 模式）。生成脚本 `temp/u4-4-oracle-build.ps1` + `temp/u4-4-freeze-fixtures.ps1`。

⚠️ oracle 的运行环境是 **Windows**（git 2.53.0.windows.2，`core.ignoreCase=true`），因此语料里含
「大小写不同仍命中」的行（`caseprobe.txt` → `CASEPROBE.TXT`），匹配时必须 `ignoreCase: true`。
