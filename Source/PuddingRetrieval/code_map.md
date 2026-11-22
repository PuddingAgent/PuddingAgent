# PuddingRetrieval CodeMAP
> 源指纹: *.cs=6-3d0628744e5b, *.csproj=1-12ab290c6cc1 · 条目数: 6 · 最近整理: 2026-11-22

> 源指纹: Source/PuddingRetrieval/**=7-e007a9946e91 · 条目数: 7 · 最近整理: 2026-11-22
> 定位：检索实现层——编排与数据面抽象（ADR-089 R2-A 四组件之一）。.NET 10 类库。
> 只定义「检索编排需要什么数据」，不定义「数据从哪来」；运行时目录与构建入口只在根 code_map.md。

## 入口与配置

依赖方向（编译期由 csproj 强制）：`PuddingRetrieval → PuddingRetrieval.Contracts + PuddingDomain.Contracts`，仅此二者。
禁止引用 `PuddingCodeIndex`、`PuddingCodeIntelligence`、`PuddingRuntime`、`PuddingHost`、`PuddingPlatform`、
`PuddingFullTextIndex`、`PuddingPathFiltering`、`PuddingGrep`、`PuddingLlmUsage*`。
边界由 `PuddingRetrievalTests/ComponentBoundaryTests` 两层断言把守（运行时反射 + csproj 声明）。

为什么要有窄端口：`ICodeIndexStore` 有 30+ 成员。把 `IRetrievalPort` 的实现直接绑上去，测试就得实现一整个 store，
于是测试会退化成「能编译就算过」。两个窄端口（符号面 / 文本面）让测试可写、让依赖方向显式。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `IRetrievalSources.cs` | 符号面与文本面的窄端口，含文本命中与不可用异常 | `IRetrievalSymbolSource` / `IRetrievalTextSource` / `RetrievalTextMatch` | `RetrievalPortV1.cs` | 文本面返回空列表只有一种含义：查过、没匹配；「索引没建」必须抛 `RetrievalTextSourceUnavailableException`，端口转 `RetrievalEmptyReason.NotIndexed` |
| `RetrievalPortV1.cs` | `IRetrievalPort` 的首个实现：意图路由与多路融合 | `RetrievalPortV1` | `IRetrievalSources.cs` | 未支持意图须报 `IntentUnsupported` 并给替代方案；跨 scope 去重先于过载判定；超页落盘并给游标；带符号锚点时只按（项目, 符号）精确取目标 |
| `DefaultLanguageCapabilityMatrix.cs` | 语言能力矩阵的默认实现（C#/TS/Markdown/未知） | `DefaultLanguageCapabilityMatrix` | `RetrievalCoverageMatrix.cs` | 只声明有实测证据的「支持」；没有证据的能力面不得写支持 |
| `RetrievalCoverageMatrix.cs` | 覆盖矩阵：项目 × 语言 × 能力面在输出层交叉 | `RetrievalCoverageMatrix` / `CoverageCell` / `ProjectCoverage` / `ProjectLanguages` | `DefaultLanguageCapabilityMatrix.cs` | — |
| `SymbolRefFreshness.cs` | `symbol_ref` 的 FRESH / STALE 判定 | `SymbolRefFreshness` / `SymbolRefFreshnessResult` / `SymbolRefState` / `SymbolRefRelocation` | `SymbolRefIssuer.cs` | 纯函数：不读盘不查库，输入全靠调用方传入 |
| `SymbolRefIssuer.cs` | `symbol_ref` 的签发端：把搜索命中批量转成 token | `SymbolRefIssuer` / `SymbolRefIssueRequest` / `SymbolRefIssue` | `SymbolRefFreshness.cs` | 纯类：`FromManifest` 由调用方传入 manifest 快照，不持有 store |
| `PuddingRetrieval.csproj` | 项目文件：依赖方向声明 | — | — | `ProjectReference` 只有 `PuddingRetrieval.Contracts` 与 `PuddingDomain.Contracts`；多一条即破组件边界 |

## 契约要点

- 文本面「没法查」与「确实没有」必须走两条路：前者抛异常、后者返回空列表。折成一条就会制造空洞否定，那是 R0 要消灭的缺陷类型。
- `IRetrievalSymbolSource` 的关系面按 `symbolId` 精确取目标，**不得**退回名字检索。

## 依赖边

`PuddingCodeIndex` 不再引用本工程（全工程扫描 `IRetrievalSymbolSource|IRetrievalTextSource|RetrievalTextMatch|RetrievalPortV1` 为空）。
`CodeIndexStoreSymbolSource` 位于 `Source/PuddingRetrieval.Adapters/`：`PuddingRetrieval ← PuddingRetrieval.Adapters → PuddingCodeIndex`。
`PuddingCodeIndex/Services/Retrieval/` 目录已删除，本工程不再有该子命名空间的对应实现残留。

## 测试

`dotnet test Source\PuddingRetrievalTests\PuddingRetrievalTests.csproj` —— 55 条。
其中 25 条随 `DefaultLanguageCapabilityMatrix` / `RetrievalCoverageMatrix` / `SymbolRefFreshness` 从 `PuddingCodeIndexTests` 迁入
（迁移前 30 → 迁移后 55，对应 `PuddingCodeIndexTests` 399 → 374，两侧差值必须相等）。
`SymbolRefIssuer` 的契约测试仍在 `PuddingRuntimeTests`，未随之搬迁。
