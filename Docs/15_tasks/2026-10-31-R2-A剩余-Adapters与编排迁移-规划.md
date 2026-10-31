---
title: R2-A 剩余部分规划 · Adapters 与编排迁移
author: 鲸鲸
date: 2026-10-31
last_reviewed: 2026-10-31
status: proposed
---

# R2-A 剩余部分规划 · Adapters 与编排迁移

> 前序：R2-01 契约迁移（`cb265a5`）→ 记录类型下沉（`a82301d`）→ `PuddingRetrieval` 独立（`482ba58`）。
> 本文只规划**剩下的一步半**：`RetrievalPortV1` 编排逻辑与 `CodeIndexStoreSymbolSource` 适配器。

## 1. 目标终态

```
                      PuddingRetrieval.Contracts
                                ↑
   PuddingDomain.Contracts → PuddingRetrieval（编排 + 窄端口）
                                ↑
                      PuddingRetrieval.Adapters（ICodeIndexStore → 窄端口）
                                ↑
                         PuddingCodeIndex（存储）
```

**验收这一张图的方式**：`PuddingCodeIndex` 的 csproj 里**不再**出现 `PuddingRetrieval`；
`PuddingRetrieval.Adapters` 是唯一同时引用 `PuddingCodeIndex` 与 `PuddingRetrieval` 的工程。

## 2. 实测依据（2026-10-31，非估算）

| 事实 | 数字 |
|---|---|
| `RetrievalPortV1.cs` 总行数 | 1454 |
| 其中 `IRetrievalSymbolSource`/`IRetrievalTextSource`/值类型 | 已搬走（`482ba58`） |
| `RetrievalPortV1` 类引用的 `Code*` 类型 | 9 个，**全部已下沉到 Domain.Contracts** |
| `RetrievalPortV1` 类对 `ICodeIndexStore` 的依赖 | **0**（仅在 `CodeIndexStoreSymbolSource` 内） |
| `CodeIndexStoreSymbolSource` 行数 | 92（`RetrievalPortV1.cs` L16–L107） |
| 引用 `RetrievalPortV1`/`CodeIndexStoreSymbolSource` 的位置 | 7 个文件 / 5 个工程 |

这意味着**编排层几乎是干净的** —— 它唯一与存储耦合的地方，是同一文件里那个 92 行的适配器。
先把适配器切出去，编排就能原样搬家。

## 3. 影响面（7 个文件）

| 工程 | 文件 | 迁移后的处理 |
|---|---|---|
| `PuddingCodeIndex` | `Services/Retrieval/RetrievalPortV1.cs` | 删除（整体搬走） |
| `PuddingCodeIndexTests` | `Services/Retrieval/RetrievalPortV1Tests.cs` | 迁到 `PuddingRetrievalTests` |
| `PuddingRetrievalEvalProbe` | `ProductionRun.cs` | ✅ 实引用（L150 `new`），需加 2 个 using + 2 个引用 |
| `PuddingRuntime` | `RetrievalServiceCollectionExtensions.cs` | 改注册来源（Adapters） |
| `PuddingRuntime` | `Tools/BuiltIns/Retrieval/WorkspaceSearchTool.cs` | ✅ 实测仅为说明字符串，**不需改** |
| `PuddingRuntimeTests` | `WorkspaceSearchToolCompositionTests.cs` | ✅ 实引用（L60 类型断言），需加 using |
| `PuddingRuntimeTests` | `WorkspaceSearchToolTests.cs` | ✅ 实测仅为查询词字符串，**不需改** |

**未核实项**：EvalProbe / WorkspaceSearchTool 三处是「用了类型」还是「注释里提到」。步骤 0 先量清，避免按错的假设写代码。

## 4. 分步（每步独立可提交、独立可回滚）

### 步骤 0：核实三处引用性质 —— ✅ 已完成（2026-10-31）

实测结果：**5 处出现，只有 2 处是实引用**。

| 位置 | 形态 | 是否需改 |
|---|---|---|
| `PuddingRetrievalEvalProbe/ProductionRun.cs:150` | `new RetrievalPortV1(new CodeIndexStoreSymbolSource(store))` | **是**（加 2 个 using + 2 个 ProjectReference） |
| `PuddingRuntimeTests/Tools/WorkspaceSearchToolCompositionTests.cs:60` | `Assert.IsInstanceOfType<RetrievalPortV1>(port)` | **是**（加 using） |
| `PuddingRuntime/Tools/BuiltIns/Retrieval/WorkspaceSearchTool.cs:77` | 工具说明里的示例查询词字符串 | 否 |
| `PuddingRuntimeTests/Tools/WorkspaceSearchToolTests.cs:52` | 查询词字符串 | 否 |
| `PuddingRuntimeTests/Tools/WorkspaceSearchToolTests.cs:238,241` | 查询词字符串 / 断言输出含该词 | 否 |

⇒ 步骤 3 的实际改代码量是 **2 个文件**，不是 7 个。`PuddingRuntime` 的两个文件里，
`RetrievalServiceCollectionExtensions.cs` 是真正要改的（DI 来源），`WorkspaceSearchTool.cs`
只是字符串，不必动。

### 步骤 1：建 `PuddingRetrieval.Adapters`（S1–S4）

- 新工程，csproj 引用 `PuddingRetrieval` + `PuddingCodeIndex`。
- 把 `CodeIndexStoreSymbolSource`（92 行）整体移入，命名空间 `PuddingRetrieval.Adapters`。
- 新测试工程，断言**两条**：
  1. `Adapters` 引用的 `Pudding*` 恰好是 `{PuddingRetrieval, PuddingCodeIndex, PuddingDomain.Contracts, PuddingRetrieval.Contracts}`（允许子集，但不得含 Runtime/Host/Intelligence/FullText）；
  2. csproj 声明层不得含 `PuddingRuntime`/`PuddingHost`。
- **禁改**：不动 `ICodeIndexStore` 签名；不顺手加缓存、不改异常语义。
- **三态**：基线绿 → 变异（把 `ListProjectsAsync` 改成返回空集合）→ 若没有断言能红，说明**测试不足以保护适配器**，必须先补一条行为测试（用内存 store 断言转发正确）。
- **风险**：适配器 92 行里有 6 个方法逐一转发，机械搬运容易漏一个方法体。对策：搬完全文 diff 逐方法对照。
- **回滚**：单 commit 删除即可，`PuddingCodeIndex` 未动。

### 步骤 2：搬编排逻辑 `RetrievalPortV1`（S1–S4）

- 整体移入 `PuddingRetrieval`，命名空间改 `PuddingRetrieval`，加 `using PuddingRetrieval.Adapters` 之外的必要 using。
- 同时把 `RetrievalPortV1Tests.cs`（约 780 行）迁到 `PuddingRetrievalTests` —— 它用的是自己实现的 `FakeSymbolSource`，不依赖 `ICodeIndexStore`，可整块搬。
- **判据**：`PuddingRetrieval` 的 `ComponentBoundaryTests` 仍绿（此时它已含 1340 行编排，仍未依赖存储）；`PuddingRetrievalTests` 用例数与迁移前一致（**数字必须能对上：425 → 425 - N，另行 +N**）。
- **风险**：编排层若有隐藏的 `PuddingCodeIndex` 用法（除 `CodeIndexStoreSymbolSource` 外），会在编译期暴露 —— 这是好事，但可能牵出第 3 个工程。
- **回滚**：单 commit。

### 步骤 3：反转依赖边（改 csproj，风险最高的一步）

- 删除 `PuddingCodeIndex.csproj` 对 `PuddingRetrieval` 的 `ProjectReference`。
- `PuddingRuntime` / `PuddingRetrievalEvalProbe` 加 `PuddingRetrieval` + `PuddingRetrieval.Adapters` 引用（按步骤 0 的结论）。
- **判据（编译期取红）**：反向依赖一旦残留，构建必须失败 —— 这是本条边唯一的强制手段。
- **风险**：`PuddingCodeIndexTests` 若还有其它地方间接用到编排层，会断；届时按错误清单补 using 或迁移。
- **回滚**：把两条引用加回去。

### 步骤 4：DI 与全量回归

- `RetrievalServiceCollectionExtensions` 改为 `new CodeIndexStoreSymbolSource(...)` 来自 Adapters 命名空间；`RetrievalPortV1` 来自 `PuddingRetrieval`。
- 跑全量：CodeIndex 425、CodeIntelligence 151、Runtime 2511、Contracts 51、Domain 6、Retrieval（4 + N）。
- **判据**：逐项与迁移前一致，且 `PuddingRetrievalTests` 数字与步骤 2 声明吻合。

### 步骤 5：部署与线上验证（S5）

- 部署前确认产物含 4 个新 DLL：`PuddingRetrieval` / `PuddingRetrieval.Adapters` 及其测试不需要、但两个生产 DLL 必须在。
- 部署后以「符号探针 + 一次真实 `workspace_search`」验收，不看自述。
- 时间窗：至少 240s（上次 120s 差点不够）。

## 5. 全局风险

| 风险 | 说明 | 对策 |
|---|---|---|
| 依赖边反转后宿主起不来 | 端口漏注册会让 `BuildServiceProvider` 直接抛（2026-10-29 三次部署回滚的根因） | 部署后必须真实调一次 `workspace_search`，不是只看 healthy |
| 测试数静默丢失 | 780 行测试搬迁 | 迁移前后数字对账（`425 + N → 425 + N`），不通过不算完成 |
| 适配器漏转发 | 6 个方法逐一转发 | 步骤 1 要求有一条行为测试能取红 |
| 命名空间与程序集不一致 | 与 Domain.Contracts 同款过渡 | 本步骤**不做**命名空间整理，避免混入无关改动 |

## 6. 明确不做

- 不搬 `RetrievalPortV1` 之外的其它检索实现（`FullTextRetrievalTextSource` 属 Runtime，是**适配器**性质，若要搬是另一步）
- 不改 `ICodeIndexStore` 的成员与语义
- 不做命名空间整理（`PuddingCodeIndex.Contracts` 前缀保留）
- 不顺手优化编排逻辑（性能、去重策略、预算算法）
- 不在本规划内处理 R1 的 5 项待裁定

## 7. 完成判据（一句话）

> `grep -r "PuddingRetrieval" Source/PuddingCodeIndex/PuddingCodeIndex.csproj` 为空，
> 且全量测试逐项与迁移前一致，且线上真实调用 `workspace_search` 通过。
