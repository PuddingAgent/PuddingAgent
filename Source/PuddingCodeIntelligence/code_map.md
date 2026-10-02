# PuddingCodeIntelligence CodeMAP

> 代码语言智能：**生产与消费**（语言索引器 · outliner · LSP · 查询 facade）
> 边界（ADR-089 §2.1）：本工程 → `PuddingCodeIndex`（单向 `ProjectReference`）；索引产物维护（契约/存储/变更捕获/调度/范围解析）已迁至 `Source/PuddingCodeIndex`。本工程**不得被** `PuddingCodeIndex` 引用。
> 历史变更与门禁记录已迁至 [`Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIntelligence-code_map迁出的变更记录.md`](../../Docs/00_changelog/2026Year/10/2026-10-02-PuddingCodeIntelligence-code_map迁出的变更记录.md)。本文件只保留索引，不再追加日志。

## 语言索引器（生产侧）

| 目录 | 语言 | 说明 |
|------|------|------|
| `CSharp/` | C# | `RoslynCSharpIndexer`（`ICodeIndexer` + `ICodeIndexFileUpdater` + **`ICodeIndexFileBatchUpdater`**）· `RoslynSymbolId` · `RoslynWorkspaceBootstrapper` |
| `TypeScript/` | TypeScript | `TypeScriptIndexer`（`ICodeIndexer`）· `TypeScriptFileOutliner` |
| `Python/` | Python | `PythonIndexer`（`ICodeIndexer`）· `PythonFileOutliner` |
| `Cpp/` | C++ | outliner |
| `Json/` | JSON | outliner |
| `Yaml/` | YAML | outliner |
| `Markdown/` | Markdown | outliner |
| `PowerShell/` | PowerShell | outliner |
| `Bicep/` | Bicep | outliner |
| `Extractors/` | 资产 | 提取器脚本资产（TS/Python）的**归属与解析基准**（程序集目录 · fail-closed） |

## 消费侧

| 文件 | 用途 |
|------|------|
| `CSharp/RoslynCSharpIndexer.cs` | C# 语言索引器。**两条路径分工**：`IndexWorkspaceAsync`/`IndexFileAsync`（旧接缝，自己写库）；**`UpdateFilesAsync` 批量接缝**（2026-10-02）：**一个批次只打开一次工程/编译快照**，逐文件提取后以 `CodeFileIndexPayload` 返回，**不写任何索引**（由调用方经 `ReplaceFilesAsync` 与源指纹同事务提交）。路由：非 `.cs`/噪声路径/不在已加载工作区 ⇒ `NotApplicable`；工程根缺失、工作区打不开、单文件提取抛错 ⇒ `Retryable` 带路径级原因（按退避重试，不升级整仓）。语义差异（消失/签名变化）**不在这里算**（需要上次已提交符号，由调用方用自己的持久状态 + `CodeFileSemanticDiff` 得出）。构造器可注入 workspace opener（默认 `RoslynWorkspaceBootstrapper`），使「批内只打开一次工程」可被测试断言 |
| `Services/CodeQueryService.cs` | 代码查询服务（只读索引，`ICodeQueryService`） |
| `Services/FileOutlinerRegistry.cs` | 文件大纲注册（按扩展名派发） |
| `Lsp/IndexBasedLanguageServerService.cs` | 基于索引的 LSP 视图（hover / definition / references） |
| `Lsp/NoOpLanguageServerService.cs` | 空实现 |
| `Contracts/ICodeQueryService.cs` | 查询契约 |
| `Contracts/IFileOutliner.cs` | outliner 契约（`OutlineNode.Kind` 复用 `PuddingCodeIndex.Contracts.CodeSymbolKind`） |
| `Contracts/ILanguageServerService.cs` + `Contracts/LanguageServerContracts.cs` | LSP 契约 |
| `DependencyInjection.cs` | `AddPuddingCodeIntelligence()` 组合根（同时注册 `PuddingCodeIndex` 侧实现：scheduler · scope registry/resolver · project registry · workspace resolver · root detector · `ICodeIndexer`） |

## 已迁出（→ `Source/PuddingCodeIndex`，切片 1）

`Services/`：`CodeIndexScheduler` · `CodeIndexScopeRegistry` · `CodeIndexScopeResolver` · `CodeProjectRegistry` · `CodePathIdentity` · `IndexExcludePatterns` · `DefaultCodeWorkspaceResolver` · `DefaultProjectRootDetector`
`Services/CodeIndex/`：U3-A 6 文件（`IndexChange` · `CodeIndexScopeState` · `CodeIndexChangeQueue` · `CodeIndexChangeBatch` · `CodeIndexWatcher` · `CodeIndexChangeCoalescer`）
`Storage/`：`SqliteCodeIndexStore`
`Contracts/`：索引契约（`CodeFileRecord` · `CodeIndex*` · `CodeSymbol*` · `CodeReferenceRecord` · `CodeRelation*` · `CodeWorkspaceDescriptor` · `ICodeIndex*` · `ICodeProjectRegistry` · `ICodeWorkspaceResolver` · `ICodeProjectRootDetector` · `CodeProject*`）

详见 `../PuddingCodeIndex/code_map.md`。

## 测试

`../PuddingCodeIntelligenceTests/` — 语言索引器 / outliner / LSP + 索引侧测试（索引侧测试直连 `PuddingCodeIndex`；切片 2 将拆分测试工程）

---
