# PuddingGrep CodeMAP

> 统一 grep 的**独立匹配内核**（ADR-089 R2-00）。条目数: 3 · 最近整理: 2026-10-31

## 定位

只回答一个问题：**这一行是否命中、命中在哪**。

刻意不知道：文件系统、目录排除、扩展名过滤、候选索引、Provider、最大结果数 —— 那些属于扫描/编排层。混进来就会让「同一语义在不同工具里各写一遍」的老问题复发（这正是 R1-01 要收口的那类缺陷）。

## 文件

| 文件 | 用途 |
|------|------|
| `GrepPattern.cs` | 匹配意图（字面量 / 正则 + 大小写）。**构造期**校验：空模式、非法正则当场失败，不留到运行时变成「静默不命中」 |
| `GrepSpan.cs` | 行内命中位置（`Index` / `Length` / `End`），不含文件与行号 |
| `GrepMatcher.cs` | `IsMatch` / `FindAll`；字面量用 `IndexOf`（Ordinal / OrdinalIgnoreCase），正则用 `Regex` + 文化不敏感 + **超时**（`DefaultMatchTimeout = 2s`，防灾难性回溯挂住扫描线程） |

## 关键语义

- **模式无效必须抛**（`ArgumentException`），不得返回「无匹配」—— 上层无法区分「搜了没结果」与「模式坏了」，那是错误否定的来源。
- `FindAll` **不允许重叠**：下一次搜索从上一个命中结束处继续，与 `Regex.Matches` 一致。
- 正则匹配一律 `RegexOptions.CultureInvariant`：文化相关的相等判断会让同一模式在不同区域设置下给出不同结果。

## 边界（S4 运行时断言）

- 本工程零 `ProjectReference`、零 `PackageReference`，只引 BCL。
- `PuddingGrepTests/ComponentBoundaryTests` 用反射断言：本程序集不含任何 `Pudding*` 引用；测试工程不含 Runtime / CodeIndex / CodeIntelligence / Host / Platform / FullTextIndex 引用。

## 测试

`Source/PuddingGrepTests`（独立工程，40 条）：以 BCL 当 oracle 逐条对照（`IndexOf` / `Regex` / `Regex.Matches`），另覆盖空模式、非法正则、灾难性回溯超时、中文字面量、非重叠语义。

## 接入状态（S5 进行中）

- **已登记进 `PuddingAgentNetwork.slnx`**（`dotnet sln PuddingAgentNetwork.slnx list` 可列出两个工程）—— 这样全仓构建/门禁会真的构建并运行它，而不是“存在但没人跑”。登记属构建门禁，不是运行时接入，S1–S4 已先通过。
- **未注册 DI、未替换 `Runtime/SearchGrepTool` 的匹配逻辑**：那才是 S5 的运行时部分，需部署验证，未做。
- 扫描层（文件枚举、排除目录、扩展名、候选索引、`maxResults`/预算）不在本组件内（R3）。

```
dotnet test Source\PuddingGrepTests\PuddingGrepTests.csproj
dotnet test PuddingAgentNetwork.slnx --filter FullyQualifiedName~PuddingGrepTests
```
