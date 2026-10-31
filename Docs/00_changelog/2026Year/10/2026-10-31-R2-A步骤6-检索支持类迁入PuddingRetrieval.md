# 2026-10-31 R2-A 步骤 6 检索支持类迁入 PuddingRetrieval

## 改动

- **迁移 3 个文件（+3 个测试类）**从 `Source/PuddingCodeIndex/Services/Retrieval/` 到 `Source/PuddingRetrieval/`（命名空间 `PuddingCodeIndex.Services.Retrieval` → `PuddingRetrieval`）：
  - `DefaultLanguageCapabilityMatrix.cs`（能力矩阵默认实现）
  - `RetrievalCoverageMatrix.cs`（项目 × 语言 × 能力面覆盖矩阵）
  - `SymbolRefFreshness.cs`（`symbol_ref` 的 FRESH/STALE 纯函数判定）
  - 对应 `DefaultLanguageCapabilityMatrixTests.cs` / `RetrievalCoverageMatrixTests.cs` / `SymbolRefFreshnessTests.cs` 迁至 `PuddingRetrievalTests/`
- **补 10 处 `using PuddingRetrieval;`**（4 个 Runtime 生产文件 + 6 个 RuntimeTests 文件）。这些文件此前靠 `using PuddingCodeIndex.Services.Retrieval;` 就能解析 `SymbolRefRelocation` / `SymbolRefState` / `SymbolRefFreshness`；类型换程序集后必须显式引入新命名空间。
- `PuddingCodeIndex/code_map.md`：删去 `Retrieval/SymbolRefFreshness.cs` 行（文件已不在本工程），`SymbolRefIssuer` 的关联路径改为 `PuddingRetrieval/SymbolRefFreshness.cs`，头部补步骤 6 说明，条目数 58 → 57。
- `PuddingRetrieval/code_map.md`：文件表补 3 行，「未迁入」收窄为仅 `SymbolRefIssuer.cs`，测试段注明 55 条及迁移对账关系，条目数 3 → 6。

## 证据（实测）

### 测试数守恒（本次迁移的核心判据）

| 工程 | 迁移前 | 迁移后 | 差 |
|---|---:|---:|---:|
| `PuddingCodeIndexTests` | 399 | **374** | −25 |
| `PuddingRetrievalTests` | 30 | **55** | +25 |
| 合计 | 429 | **429** | **0** |

三个等式同时成立：`399−25=374`、`30+25=55`、`374+55=429`。漏一个测试文件时，总数会变但对不上之前**不会有任何报错**。

### 全仓回归

| 测试工程 | 通过 | 跳过 | 失败 |
|---|---:|---:|---:|
| `PuddingRuntimeTests` | 2,504 | 7 | 0 |
| `PuddingCodeIndexTests` | 374 | 0 | 0 |
| `PuddingFullTextIndexTests` | 325 | 4 | 0 |
| `PuddingCodeIntelligenceTests` | 151 | 0 | 0 |
| `PuddingRetrievalTests` | 55 | 0 | 0 |
| `PuddingRetrieval.ContractsTests` | 51 | 0 | 0 |
| `PuddingGrepTests` | 41 | 0 | 0 |
| `PuddingDomain.ContractsTests` | 6 | 0 | 0 |
| `PuddingRetrieval.AdaptersTests` | 5 | 0 | 0 |
| **合计** | **3,512** | **11** | **0** |

`PuddingRuntime` 构建 0 error。`PuddingRetrievalEvalProbe` 构建 0 error。

## 过程中的两个坑（已修复，记录以免重犯）

1. **迁移不完整**：接手时工作树里这 3 个文件已在暂存区（纯 rename），但工作副本只有命名空间改动，**没有补调用方 using**。`PuddingRuntimeTests` 因此编译失败 10 处（`CS0246`×6 + `CS0738`×4 —— 后者是测试替身 `FixedRefSource` 的返回类型解析不到，报成「不实现接口成员」，实际根因仍是类型解析失败，不是签名写错）。
2. **`file_write` 相对路径不按仓库根解析**：写入时按进程 cwd（当时是 `temp/build/winui3/.../core`）落盘，导致后续 `py -3 temp\xxx.py` 报 `No such file`。凡涉及脚本/文件落地一律用绝对路径。

## 未做

- `SymbolRefIssuer.cs` 仍在 `PuddingCodeIndex/Services/Retrieval/`。**更正**：本轮初稿称它「读存储内容哈希、需先设计端口」，实测不成立 —— `FromManifest(...)` 由调用方传入 manifest 快照，类本身不持有 `ICodeSourceMaintenanceStore`，是纯类。真正障碍是 `CodeSourceEntry` / `SourceFingerprint` / `AppliedFileVersion` 三个值类型仍在 `PuddingCodeIndex.Contracts`。
- 不部署：本轮为结构调整 + 命名空间搬迁，无行为变化；线上构建版本未在本轮核实。
- R2-A 步骤 5（部署验证）仍待授权。
