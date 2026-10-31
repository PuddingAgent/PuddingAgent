# PuddingRetrieval CodeMAP

> 检索实现层：**数据面抽象**（窄端口） | 条目数: 2 · 最近整理: 2026-10-31

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

## 契约要点

- 文本面返回**空列表**只有一种含义：查了，没匹配。「索引没建」必须抛
  `RetrievalTextSourceUnavailableException`，端口捕获后转成 `RetrievalEmptyReason.NotIndexed`。
  否则「没法查」会被读成「确实没有」—— 那正是 R0 要消灭的空洞否定。
- `IRetrievalSymbolSource` 的关系面按 `symbolId` 精确取目标，**不得**退回名字检索。

## 未迁入（仍在上游，待 `PuddingRetrieval.Adapters`）

- `RetrievalPortV1`（编排：意图路由 / 去重 / 排序 / 预算 / 空原因裁决）—— 在 `PuddingCodeIndex`
- `CodeIndexStoreSymbolSource`（`ICodeIndexStore` → 窄端口适配器）—— 在 `PuddingCodeIndex`

> ⚠️ 过渡态：当前 `PuddingCodeIndex` 引用本工程（为了拿到窄端口接口）。适配器迁出到
> `PuddingRetrieval.Adapters` 后，这条边应反转为 `Adapters → CodeIndex`。

## 测试

```
dotnet test Source\PuddingRetrievalTests\PuddingRetrievalTests.csproj
```

4 条：两条边界（运行时 / csproj 声明）、`RetrievalTextMatch` 值相等、异常的 InnerException 保留。
