# PuddingRetrieval.Contracts CodeMAP

> 跨 provider 的检索契约（ADR-089 R2-00 / R2-01）。条目数: 2 · 最近整理: 2026-10-31

## 定位

检索能力的**契约层**：只描述「请求/结果/覆盖声明长什么样」，不含任何实现。

依赖方向是单向的：实现（CodeIndex / Runtime / Adapters）依赖本组件，本组件不认识它们。这条由 `PuddingRetrieval.ContractsTests/ComponentBoundaryTests` 在运行时断言。

## 文件

| 文件 | 用途 |
|---|---|
| `CodeSymbolKind.cs` | 符号种类（18 项） |
| `CodeRelationKind.cs` | 关系种类（9 项） |

两者都是**持久化契约**：`CodeSymbols.Kind` / `CodeRelations.Kind` 按整数存储，调整成员顺序会让已有索引库的数据含义整体错位，且不会有任何编译错误。

## ⚠️ 过渡状态（必须知道）

`CodeSymbolKind` / `CodeRelationKind` 此刻在 **`PuddingCodeIndex.Contracts` 也有一份定义**。

原因：R2-01 迁移顺序被依赖方向倒逼 —— 检索契约里有 6 个文件直接用 `CodeSymbolKind`
（`RetrievalHit` / `RetrievalHitViews` / `RetrievalFilter` / `RetrievalIntentPolicy` /
`RetrievalDistribution` / `ContainerSymbolGuidance`），若先搬契约，本组件就必须引用 CodeIndex。

因此先下沉枚举（本步骤），再搬契约（任务卡步骤 2），届时删除 CodeIndex 侧定义，本组件成为唯一真源。

**期间两份必须逐成员逐数值一致**（`EnumStabilityTests` 锁住数值、顺序与成员数）。

## 测试

`Source/PuddingRetrieval.ContractsTests`（5 条）：枚举数值/顺序/成员数钉死 + 边界反射断言。

```
dotnet test Source\PuddingRetrieval.ContractsTests\PuddingRetrieval.ContractsTests.csproj
```

## 尚未迁入

30 个检索契约文件（`IRetrievalPort` / `RetrievalClaim` / `RetrievalResult` / `SymbolRefV1` 等）仍在
`Source/PuddingCodeIndex/Contracts/Retrieval/`，按任务卡
`Docs/15_tasks/2026-10-31-R2-01契约迁移到PuddingRetrieval.Contracts-任务卡.md` 的步骤 2 迁入。
