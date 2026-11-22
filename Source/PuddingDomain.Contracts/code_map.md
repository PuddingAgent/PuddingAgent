# PuddingDomain.Contracts CodeMAP

> 源指纹: Source/PuddingDomain.Contracts/**=9-462658dd7bea · 条目数: 9 · 最近整理: 2026-11-22
> 定位：跨层共享的值类型（ADR-089 R2-01）。.NET 10 类库，只引 BCL。
> 只放被多层共同使用、且不属于任何单一上层的值类型；运行时目录与构建入口只在根 code_map.md。

## 入口与配置

依赖方向：上层 `PuddingCodeIndex`、`PuddingRetrieval.Contracts` 及其它任何层都依赖本组件；本组件不接受任何 `ProjectReference`。
反向依赖由 `PuddingDomain.ContractsTests/ComponentBoundaryTests` 运行时断言。

为什么单列一个组件：`CodeSymbolKind`（18 项）与 `CodeRelationKind`（9 项）被全仓大量文件使用，横跨 CodeIndex 存储、
CodeIntelligence 抽取、Runtime 工具与各路测试。留在 `PuddingCodeIndex` 会让上层为了用枚举而反向依赖存储组件；
放进 `PuddingRetrieval.Contracts` 又会让「检索契约」事实上变成全仓基础库。

## 文件

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `CodeSymbolKind.cs` | 符号种类枚举（18 项） | `CodeSymbolKind` | `CodeSymbolContracts.cs` | SQLite 按整数存储：调整成员顺序不报编译错，却让已有索引库的数据含义整体错位；新成员只能追加到末尾，`EnumStabilityTests` 钉死「名=数值」序列 |
| `CodeRelationKind.cs` | 符号关系种类枚举（9 项） | `CodeRelationKind` | `CodeRelationContracts.cs` | 同 `CodeSymbolKind`：整数存储，顺序不可重排，成员只能追加 |
| `CodeFileRecord.cs` | 索引中单个文件的记录 | `CodeFileRecord` | `CodeSymbolContracts.cs` | — |
| `CodeSymbolContracts.cs` | 符号、搜索请求与详情的契约类型 | `CodeSymbolRecord` / `CodeSymbolSearchRequest` / `CodeSymbolMatchTarget` / `CodeSymbolDetail` | `CodeSymbolKind.cs` | — |
| `CodeProjectContracts.cs` | 索引项目记录与增删请求的类型 | `CodeProjectRecord` / `CodeProjectStatus` / `CodeProjectAddRequest` / `CodeProjectRemoveRequest` | — | `CodeProjectRecord.StatusMessage` 必须留在参数列表末尾（可选参数）：插到中间会迫使全部 5 个构造点改签名；它承载最近一次状态变更的原因（如索引被中断） |
| `CodeRelationContracts.cs` | 符号关系记录的契约类型 | `CodeRelationRecord` | `CodeRelationKind.cs` | — |
| `CodeIndexScopeContracts.cs` | 索引范围的定义类型与范围状态 | `CodeIndexScope` / `ScopeState` / `ScopeSource` | — | — |
| `CodeSourceFingerprintContracts.cs` | 源指纹与已应用文件版本的值类型 | `SourceFingerprint` / `AppliedFileVersion` / `CodeSourceEntry` | — | — |
| `PuddingDomain.Contracts.csproj` | 项目文件：目标框架与依赖方向声明 | — | — | 只引 BCL；出现任何 `ProjectReference` 即破坏组件边界 |

## 本项目约束

- 命名空间**刻意**保持 `PuddingCodeIndex.Contracts`，与程序集名不一致：迁移要求零调用方改动（全部调用方的 using 一行不改），
  只修正程序集依赖方向。整理命名空间必须单独一步并同步全部调用方，不要顺手改。
- 现有 `CodeSymbolRecord` / `CodeFileRecord` / `CodeProjectContracts` / `CodeRelationContracts` / `CodeIndexScopeContracts`
  与 3 个源指纹值类型同在 `PuddingCodeIndex.Contracts` 命名空间下，按命名空间搜符号会落到本工程而非 CodeIndex。

## 测试

`dotnet test Source\PuddingDomain.ContractsTests\PuddingDomain.ContractsTests.csproj` —— 6 条：两个枚举的数值/顺序/成员数钉死、
边界反射断言、csproj 声明层断言。
