# PuddingRetrieval.Adapters CodeMAP

> 适配器层：**存储 → 检索窄端口** | 条目数: 1 · 最近整理: 2026-10-31

## 定位

ADR-089 R2-A 四组件之四。全仓**唯一**同时看见「检索窄端口」与「代码存储」的工程。

```
PuddingRetrieval（窄端口 + 编排）
        ↑
PuddingRetrieval.Adapters  →  PuddingCodeIndex（ICodeIndexStore）
        ↑
PuddingRuntime（DI 装配）
```

**禁止**引用：`PuddingRuntime` / `PuddingHost` / `PuddingCodeIntelligence` / `PuddingFullTextIndex`。

## 为什么单独成层

- 让 `PuddingRetrieval` 不依赖存储：否则检索实现层绑死在 SQLite 上，
  测试必须实现 30+ 成员的 store，测试会退化成「能编译就算过」。
- 让 `PuddingCodeIndex` 不依赖检索：存储不该知道上层要什么。

## 文件

| 文件 | 用途 | 关键符号 |
|---|---|---|
| `CodeIndexStoreSymbolSource.cs` | 把 `ICodeIndexStore` 适配成 `IRetrievalSymbolSource`（6 个方法逐一转发） | `CodeIndexStoreSymbolSource` |

## 约束（改动前必读）

1. **只转发，不改语义**：不加缓存、不改异常行为、不改返回集合的筛选。任何「顺手优化」
   都会让窄端口的含义与存储的真实行为脱节，而偏差在检索侧看起来像「索引数据错了」。
2. **`relationKind` 必须显式传 `null`**：`ICodeIndexStore` 同时有「单个 relationKind（旧）」
   与「relationKinds（集合）」两个可选参数。不显式传 null，将来有人给旧参数加默认值时会
   静默改写调用语义。由 `IncomingRelations_ForcesLegacyRelationKindToNull` 把守。
3. **出边走 `ListRelationsAsync`**，不是 `ListOutgoingRelationsAsync`（存储层没这个方法名）。
   由 `OutgoingRelations_RoutesToStoreListRelations` 把守。

## 测试

```
dotnet test Source\PuddingRetrieval.AdaptersTests\PuddingRetrieval.AdaptersTests.csproj
```

5 条。用 `DispatchProxy` 运行时生成 `ICodeIndexStore` 替身记录调用，避免手写 30+ 方法。
⚠️ 代理基类**不能是 `sealed`**（运行时需要派生子类）。

## 待办

`PuddingCodeIndex → PuddingRetrieval` 的反向边仍在（规划步骤 3 删除）。
