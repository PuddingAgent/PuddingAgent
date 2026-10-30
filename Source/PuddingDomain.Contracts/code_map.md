# PuddingDomain.Contracts CodeMAP

> 跨层共享的值类型（ADR-089 R2-01）。条目数: 2 · 最近整理: 2026-10-31

## 定位

只放**被多层共同使用、且不属于任何单一上层**的值类型。

当前承载：`CodeSymbolKind`（18 项）、`CodeRelationKind`（9 项）。

为什么单独成组件而非塞进检索契约：这两个枚举被 **81 个文件**使用（CodeIndex 存储 / CodeIntelligence 抽取 / Runtime 工具 / 各路测试）。放进 `PuddingRetrieval.Contracts` 会让「检索契约」事实上变成全仓基础库；留在 `PuddingCodeIndex` 又会让上层为了用枚举而反向依赖存储组件。

## 依赖方向

```
PuddingDomain.Contracts  ←  PuddingCodeIndex
                         ←  PuddingRetrieval.Contracts（后续）
                         ←  其它任何层
```

本组件只引 BCL。反向依赖由 `PuddingDomain.ContractsTests/ComponentBoundaryTests` 运行时断言。

## ⚠️ 命名空间刻意的选择

两个枚举的命名空间**保持 `PuddingCodeIndex.Contracts`**，不改成 `PuddingDomain.Contracts`。

理由：迁移阶段**零调用方改动** —— 81 个文件的 using 一行都不用改，先把「程序集依赖方向」修正；命名空间整理留作后续单独一步（那时可以连带把 `CodeSymbolRecord` 等一起搬到合理位置）。

代价：程序集名与命名空间不一致，读代码时会有短暂的困惑。已在文件头与本文说明。

## 持久化契约

`CodeSymbols.Kind` / `CodeRelations.Kind` 在 SQLite 里按**整数**存储。调整成员顺序**不会有任何编译错误**，但已有索引库里的数据含义会整体错位（`Class` 被读成 `Struct`）。新增成员只能追加到末尾。

`EnumStabilityTests` 把「成员名=数值」整个序列钉死。

## 测试

```
dotnet test Source\PuddingDomain.ContractsTests\PuddingDomain.ContractsTests.csproj
```

5 条：两个枚举的数值/顺序/成员数钉死 + 边界反射断言。
