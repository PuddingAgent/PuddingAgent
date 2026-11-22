# PuddingRetrieval.Adapters CodeMAP
> 源指纹: *.cs=1-48f57d383e48, *.csproj=1-ada73ba0e8fd · 条目数: 1 · 最近整理: 2026-11-22

> 源指纹: Source/PuddingRetrieval.Adapters/**=2-7ad9aada20c0 · 条目数: 2 · 最近整理: 2026-11-22
> 定位：适配器层——存储到检索窄端口的桥（ADR-089 R2-A 四组件之四）。.NET 10 类库。
> 全仓唯一同时看见检索窄端口与代码存储的工程；运行时目录与构建入口只在根 code_map.md。

## 入口与配置

依赖方向：`PuddingRetrieval（窄端口 + 编排）` 被 `PuddingRetrieval.Adapters` 引用，后者再引用 `PuddingCodeIndex`；
`PuddingRuntime` 在 DI 装配处引用本工程。禁止引用 `PuddingRuntime`、`PuddingHost`、`PuddingCodeIntelligence`、`PuddingFullTextIndex`。

为什么单独成层：`PuddingRetrieval` 若直接依赖 `ICodeIndexStore` 就绑死在 SQLite 上，测试须实现 30+ 成员，
测试会退化成「能编译就算过」；反过来让 `PuddingCodeIndex` 实现窄端口则是反向依赖，存储不该知道上层要什么。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CodeIndexStoreSymbolSource.cs` | 把 `ICodeIndexStore` 适配成检索符号窄端口 | `CodeIndexStoreSymbolSource` | `Source/PuddingRetrieval/IRetrievalSources.cs` · `Source/PuddingCodeIndex/ICodeIndexStore.cs` | 只转发不改语义：不加缓存、不改异常行为、不改返回集合筛选；`relationKind` 必须显式传 `null`；出边走 `ListRelationsAsync` |
| `PuddingRetrieval.Adapters.csproj` | 项目文件：依赖声明即职责边界 | — | `Source/PuddingRetrieval` · `Source/PuddingCodeIndex` | `ProjectReference` 必须恰好是这两条（白名单；黑名单会让「多一条」静默通过） |

## 本项目约束

- 任何「顺手优化」都会让窄端口的含义与存储真实行为脱节，而偏差在检索侧看起来像「索引数据错了」。
- `ICodeIndexStore` 同时有单个 `relationKind`（旧）与 `relationKinds`（集合）两个可选参数：不显式传 `null`，
  将来有人给旧参数加默认值时会静默改写调用语义。由 `IncomingRelations_ForcesLegacyRelationKindToNull` 把守。
- 测试替身用 `DispatchProxy` 运行时代理 `ICodeIndexStore`，避免手写 30+ 方法；代理基类**不能是 `sealed`**（运行时需派生子类）。
- 存储层没有 `ListOutgoingRelationsAsync` 这个名字：出边必须走 `ListRelationsAsync`，由 `OutgoingRelations_RoutesToStoreListRelations` 把守。
- 边界断言里的 `LocateCsproj` 必须指向**被测组件**而非测试工程：指错时测试工程也可能凑够 2 条引用，断言会静默失效。

## 边界断言

`PuddingRetrieval.AdaptersTests/ComponentBoundaryTests.cs` 两层把守。
声明层：csproj 的 `ProjectReference` 恰好两条，见上表。闭包层：`*.deps.json` 必须含两侧且不含任何上层程序集。
声明层看不见传递依赖，闭包层能；闭包层看不见「声明了但没用」，声明层能。
三态证据（2026-10-31）：基线 8/8 → 给 csproj 加 `PuddingGrep` 引用后恰好 1 红且红的正是声明层断言 → 还原后 8/8 复绿。

## 测试

`dotnet test Source\PuddingRetrieval.AdaptersTests\PuddingRetrieval.AdaptersTests.csproj` —— 8 条 = 5 条转发行为 + 3 条边界
（声明层两条引用 / 闭包层两侧且无上层 / 探测器自检）。
