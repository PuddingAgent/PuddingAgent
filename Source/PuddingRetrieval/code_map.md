# PuddingRetrieval CodeMAP

> 检索实现层：**编排 + 数据面抽象** | 条目数: 6 · 最近整理: 2026-10-31

## 定位

ADR-089 R2-A 四组件之一。本工程只定义「检索编排需要什么数据」，不定义「数据从哪来」。

```
PuddingRetrieval  →  PuddingRetrieval.Contracts + PuddingDomain.Contracts（仅此二者）
```

**禁止**引用：`PuddingCodeIndex` / `PuddingCodeIntelligence` / `PuddingRuntime` / `PuddingHost` /
`PuddingPlatform` / `PuddingFullTextIndex` / `PuddingPathFiltering` / `PuddingGrep` / `PuddingLlmUsage*`。

由 `PuddingRetrievalTests/ComponentBoundaryTests` 两层断言把守（运行时反射 + csproj 声明）。

## 为什么要有窄端口

`ICodeIndexStore` 有 30+ 成员。把 `IRetrievalPort` 的实现直接绑上去，测试就得实现一整个 store，
于是测试会退化成「能编译就算过」。两个窄端口（符号面 / 文本面）让测试可写、让依赖方向显式。

## 文件

| 文件 | 用途 | 关键符号 |
|---|---|---|
| `IRetrievalSources.cs` | 符号面 + 文本面的窄端口，以及文本命中的值类型与「不可用」异常 | `IRetrievalSymbolSource` / `IRetrievalTextSource` / `RetrievalTextMatch` / `RetrievalTextSourceUnavailableException` |
| `RetrievalPortV1.cs` | `IRetrievalPort` 首个实现（1391 行）：意图路由 / 多路融合 / 去重 / 排序 / 预算 / 空原因裁决 / 跨页游标 | `RetrievalPortV1` |
| `DefaultLanguageCapabilityMatrix.cs` | 语言能力矩阵的默认实现（C# / TS / Markdown / 未知语言）；**只声明有实测证据的「支持」** | `DefaultLanguageCapabilityMatrix` |
| `RetrievalCoverageMatrix.cs` | 覆盖矩阵：项目 × 语言 × 能力面 在输出层交叉，可渲染成 Markdown 表 | `RetrievalCoverageMatrix` / `CoverageCell` / `ProjectCoverage` / `ProjectLanguages` |
| `SymbolRefFreshness.cs` | `symbol_ref` 的 FRESH / STALE 判定（纯函数，不读盘不查库） | `SymbolRefFreshness` / `SymbolRefFreshnessResult` / `SymbolRefState` / `SymbolRefRelocation` |

## 契约要点

- 文本面返回**空列表**只有一种含义：查了，没匹配。「索引没建」必须抛
  `RetrievalTextSourceUnavailableException`，端口捕获后转成 `RetrievalEmptyReason.NotIndexed`。
  否则「没法查」会被读成「确实没有」—— 那正是 R0 要消灭的空洞否定。
- `IRetrievalSymbolSource` 的关系面按 `symbolId` 精确取目标，**不得**退回名字检索。

## 依赖边已反转（2026-10-31 步骤 2/3）

`PuddingCodeIndex` **不再**引用本工程。全工程扫描
`IRetrievalSymbolSource|IRetrievalTextSource|RetrievalTextMatch|RetrievalPortV1` 的结果为空。

`CodeIndexStoreSymbolSource` 已迁至 `PuddingRetrieval.Adapters`：

```
PuddingRetrieval  ←  PuddingRetrieval.Adapters  →  PuddingCodeIndex
```

## 未迁入（仍在 `PuddingCodeIndex/Services/Retrieval/`）

`SymbolRefIssuer.cs` —— 实测它是**纯类**：`FromManifest(IReadOnlyDictionary<string, CodeSourceEntry>, projectRoot)` 由调用方传入快照，不持有 `ICodeSourceMaintenanceStore`，不需要端口。障碍只有一处：`CodeSourceEntry` / `SourceFingerprint` / `AppliedFileVersion` 三个值类型仍在 `PuddingCodeIndex.Contracts`，需先按既有模式下沉到 `PuddingDomain.Contracts`（命名空间不变、调用方零改动）。

## 测试

```
dotnet test Source\PuddingRetrievalTests\PuddingRetrievalTests.csproj
```

**55 条**。其中 25 条随 `DefaultLanguageCapabilityMatrix` / `RetrievalCoverageMatrix` / `SymbolRefFreshness` 从 `PuddingCodeIndexTests` 迁入（迁移前 30 → 迁移后 55，对应 `PuddingCodeIndexTests` 399 → 374，两侧差值必须相等）。
