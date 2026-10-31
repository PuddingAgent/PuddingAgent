# 2026-10-31 R2-A 步骤 8 SymbolRefIssuer 迁入 PuddingRetrieval（CodeIndex 检索目录清零）

## 改动

- `SymbolRefIssuer.cs` 从 `Source/PuddingCodeIndex/Services/Retrieval/` 迁至 `Source/PuddingRetrieval/`，命名空间 `PuddingCodeIndex.Services.Retrieval` → `PuddingRetrieval`。
- **`Source/PuddingCodeIndex/Services/Retrieval/` 目录已删除** —— 该子命名空间不再存在。
- 16 个文件清理 using：15 处删除已失效的 `using PuddingCodeIndex.Services.Retrieval;`（这些文件此前已补过 `using PuddingRetrieval;`），1 处 `PuddingRuntime/DependencyInjection.cs` 直接改写为新命名空间。
- `PuddingCodeIndex/code_map.md`：删除 `Services/Retrieval/SymbolRefIssuer.cs` 行，头部补步骤 8 说明，条目数 57 → 56。
- `PuddingRetrieval/code_map.md`：文件表补 `SymbolRefIssuer.cs` 行，「未迁入」标为已无，条目数 6 → 7。

## 前置：为什么这一轮才做

上一轮（步骤 7）先把 `SourceFingerprint` / `AppliedFileVersion` / `CodeSourceEntry` 下沉到 `PuddingDomain.Contracts`。没有那一步，`SymbolRefIssuer` 无法离开 `PuddingCodeIndex`。

同时**更正了一个错误判断**：此前记「它依赖存储内容哈希，搬迁需先设计端口」。实测 `FromManifest(IReadOnlyDictionary<string, CodeSourceEntry> manifest, string? projectRoot)` 由调用方传入快照，类本身不持有 `ICodeSourceMaintenanceStore`，是纯类 —— 不需要端口。

## 证据（实测）

### 编译

```
PuddingRetrieval    0 error
PuddingCodeIndex    0 error
```

初次编译出现 `CS0234: 命名空间 PuddingCodeIndex.Services 中不存在类型或命名空间名 Retrieval`（2 处）。这不是搬迁漏改，而是**目录删空后该命名空间整体消失**，所有仍写着它的 `using` 都失效 —— 16 个文件需要清理，远超最初以为的 2 个。

### 测试

| 工程 | 通过 | 跳过 | 失败 |
|---|---:|---:|---:|
| `PuddingRetrievalTests` | 55 | 0 | 0 |
| `PuddingCodeIndexTests` | 374 | 0 | 0 |
| `PuddingRuntimeTests` | 2,504 | 7 | 0 |

逐项与搬迁前一致 —— 纯命名空间搬迁，无行为变化，故测试数不应变（也确实没变）。

### 一个刻意的取舍

`SymbolRefIssuerTests` **留在 `PuddingRuntimeTests`**，没有随迁。它是「签发端 × 宿主桥接」的契约测试，依赖 Runtime 侧类型；搬进 `PuddingRetrievalTests` 会引入反向依赖。因此 `PuddingRetrievalTests` 仍是 55 条而不是 60+。

## 未做

- 不部署：本轮仍是纯结构调整，零行为变化。
- `ICodeSourceMaintenanceStore` 等存储端口仍在 `PuddingCodeIndex.Contracts`（未评估是否下沉）。
- R2-A 步骤 5（部署验证）仍待授权。
