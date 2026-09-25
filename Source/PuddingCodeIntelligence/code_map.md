# PuddingCodeIntelligence CodeMAP

> 代码语言智能：**生产与消费**（语言索引器 · outliner · LSP · 查询 facade）
> 边界（ADR-089 §2.1）：本工程 → `PuddingCodeIndex`（单向 `ProjectReference`）；索引产物维护（契约/存储/变更捕获/调度/范围解析）已迁至 `Source/PuddingCodeIndex`。本工程**不得被** `PuddingCodeIndex` 引用。

## 语言索引器（生产侧）

| 目录 | 语言 | 说明 |
|------|------|------|
| `CSharp/` | C# | `RoslynCSharpIndexer`（`ICodeIndexer` 实现）· `RoslynSymbolId` · `RoslynWorkspaceBootstrapper` |
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

## 变更（2026-09-25，ADR-089 U4-2b）

`Services/CodeQueryService.cs` 的 `SearchSymbolsAsync` 增加**跨 scope 去重**（按 `SymbolId`，保留首次出现者）—— ADR-089 硬约束 9/10「跨 scope 去重优先于过载判定」。根因：本仓有 4 个互相嵌套的已登记 project，同一符号被各索引一份，未限定 project 的检索必然返回重复（实测 10 条里 5 对）。门禁 `PuddingCodeIntelligenceTests` **95/95**（含 2 新用例）；变异取红与留白见 `Source/PuddingCodeIndex/code_map.md` 的 U4-2b 条目。

---

## 变更（2026-09-25，B4+ 提取器资产归属与解析基准）

**问题（父级实测，本刀已核）**：`TypeScript/TypeScriptIndexer.cs:74`/`:245`、`Python/PythonIndexer.cs:72`/`:244` 把脚本路径解析在**被索引工程**的 `ProjectPath/Scripts` 下；4 个已登记 scope 下都没有 `Scripts/` ⇒ 宿主路径必然 `Extraction script not found` ⇒ 符号库零 TS 符号。CLI 之所以能跑，是因为它自己在 `PuddingCodeIndexer.Cli/Program.cs` 里「向上找 `Scripts/` → 拷脚本进目标工程 → 进程级 `NODE_PATH` → 用完删除」——绕行逻辑长错了层。

**改动**：
- 新增 `Extractors/`：`IExtractorAssetResolver.cs`（`ExtractorAssetKind` · `ExtractorAssetFailure{None,AssetMissing,NodeModulesMissing}` · `ExtractorAssetResolution`）、`ExtractorAssetResolver.cs`（基准 = 程序集目录，构造可注入；**禁止** `descriptor.ProjectPath` / 当前目录 / 向上搜索）、`ExtractorSubprocessEnvironment.cs`（R3 接缝：`NODE_PATH` 只进子进程 `ProcessStartInfo.Environment`）。
- 两个索引器的两处解析点全部改走解析器（构造函数新增**可选**解析器参数，默认 `new ExtractorAssetResolver()`，既有调用点无需改签名），失败仍返回 `CodeIndexResult(false, Failed, <含期望路径的可读消息>)`——保持「不静默」。
- `NODE_PATH` 只在 `RunProjectExtractionAsync` / `RunExtractionScriptAsync` 写子进程环境；Python 提取器只用标准库（`ast`/`json`/`os`/`sys`），不设 `NODE_PATH`。
- `PuddingCodeIntelligence.csproj` 以 `Link` 把三个资产（`extract-ts-symbols.js` · `extract-py-symbols.py` · `node_modules\**`）随组件发布到输出目录 `Scripts/`，物理副本仍只有 `PuddingCodeIndexer.Cli/Scripts/` 一份。
- `PuddingCodeIndexer.Cli/Program.cs` 删除 TS/Python 两侧的「向上搜索 + 拷贝 + 进程级 `NODE_PATH` + 事后清理」，只保留 `Console.WriteLine` 语义与顺序。

**验证**（详见 `temp/B4-REPORT.md` 与 `temp/b4-evidence/`）：`PuddingCodeIntelligence` / `PuddingCodeIndexer.Cli` Release 构建 0 错误 0 警告；`PuddingCodeIntelligenceTests` 全绿（95 → 106，新增 11 用例）；A1~A5 断言各自可独立取红（M1 改回 `descriptor.ProjectPath` ⇒ A4 红；M2 改回 `Environment.SetEnvironmentVariable` ⇒ A5 红）；三个输出目录实测均含 `Scripts/` 三项资产。

**留白（R6，后续切片，本刀不做）**：把 22.5 MB 的 `Scripts/node_modules/typescript` 换成**构建期自包含 bundle**（esbuild 之类）以减小发布体积。动机：`.gitignore:317` 的 `node_modules/` 规则使该资产**不在版本控制内**，新克隆机器上 wildcard 展开为空 ⇒ 解析器 fail-closed 报 `NodeModulesMissing`（比「静默零符号」好，但仍需一张后续卡把资产变成可复现的构建产物）。

**未决**：`Scripts/check-ts.js` 与本切片无关，未动；`PuddingCodeIndexer.Cli.csproj` 仍保留自己的 `Scripts\extract-ts-symbols.js` 单文件发布项（与传递项同源同目标，实测无告警）。
