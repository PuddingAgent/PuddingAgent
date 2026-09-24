# PuddingPathFiltering — code map

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

| 文件 | 角色 | I/O |
|---|---|---|
| `GitWildcard.cs` | git `wildmatch()`（`WM_PATHNAME`）等价实现：`*`/`?` 不跨 `/`；边界处 `**` 跨目录、非边界 `**` 退化为 `*`；`[...]`/`[!...]`/`[^...]`、`[]]` 首字符字面量、未闭合 `[` 判不匹配 | 无 |
| `PathText.cs` | 分隔符归一、`TryGetBelow`、`Basename` | 无 |
| `IgnoreRule.cs` | 一条已解析规则：取反 / 目录专属 / 锚定 / 基准目录 + `Matches` | 无 |
| `IgnoreFileParser.cs` | 行级语义：注释、空行、**行尾空格裁剪（转义空格保留）**、**行首空格有意义**、`\#`/`\!`、`!` 取反、尾部 `/`、首部 `/` | 无 |
| `IgnoreStack.cs` | 忽略栈：**祖先目录剪枝优先**（父目录被排除时取反救不回）、后命中者胜出、更深 .gitignore 优先 | 无 |
| `PathNoiseRules.cs` | **D2 canonical 名单**：52 个目录名 + 6 个文件名；`IsNoisePath`（相对扫描根）/ `IsNoiseSegment` / **`IsNoisePathBelow`**（绝对路径必须先折算，否则宿主自身的 `%TEMP%` 段名会命中 `temp`） | 无 |
| `WorkspacePathFilter.cs` | 消费者唯一入口：名字级噪声 ∪ 忽略栈 | 无 |
| `GitIgnoreFileLoader.cs` | **唯一**触盘类型：广度优先读 `<root>/**/.gitignore`，跳过噪声目录；支持 `walkScopeDirectory` 只收集被扫描树内的忽略文件 | 有（只读） |

## 为什么自研而不是用 NuGet

候选轮子：`Ignore` 0.2.1（MIT，`netstandard2.0`+`net8.0`，无依赖，58★，最后推送 2024-07-18，6 个 open issue，
`nuspec` **无 license 元数据**）。裁决：**自研**。理由：验收标准是「与真实 git 逐条一致」，
用包也必须建同一套 oracle；而包会把一条供应链 + 离线还原面塞进索引热路径，且父目录剪枝、
跨 .gitignore 优先级、`core.ignoreCase` 折叠都仍需自建组合层。证据与推理见 `temp/U4-4-REPORT.md` §D3。

## 测试（`Source/PuddingPathFilteringTests/`，84 用例）

| 文件 | 覆盖 |
|---|---|
| `GitIgnoreOracleTests.cs` | **核心验收**：两份冻结语料共 **196 条路径**，逐条断言「结论 + 定案规则原文」与 `git check-ignore -v` 一致（一致率 100%）。语料含取反、父目录剪枝、嵌套 .gitignore、`**`、字符类、行首/行尾空格、大小写折叠等判别性行 |
| `RepositoryRootReductionTests.cs` | **「−84%」判据的可失效断言**：canonical 集合必须覆盖 P3 实测的四家头部噪声目录（23,756/28,519 = 83.3%） |
| `GitWildcardTests.cs` / `IgnoreFileParserTests.cs` / `IgnoreStackTests.cs` / `GitIgnoreFileLoaderTests.cs` / `PathNoiseRulesTests.cs` | 语义逐条锁 |
| `ComponentBoundaryTests.cs` | S4：csproj 零 ProjectReference/PackageReference、判定链无 `System.IO`、叶子不 `using Pudding*`、测试工程只引用本叶子 |

`Fixtures/` 是**冻结的 oracle**：`*.gitignore.txt`（模式源，故意不叫 `.gitignore`，否则会开始忽略仓库里 Fixtures 下的路径）
+ `*-oracle.tsv`（git 判定，列：路径 / 是否目录 / 结论 / 来源 / 行号 / 模式）。生成脚本 `temp/u4-4-oracle-build.ps1` + `temp/u4-4-freeze-fixtures.ps1`。

⚠️ oracle 的运行环境是 **Windows**（git 2.53.0.windows.2，`core.ignoreCase=true`），因此语料里含
「大小写不同仍命中」的行（`caseprobe.txt` → `CASEPROBE.TXT`），匹配时必须 `ignoreCase: true`。
