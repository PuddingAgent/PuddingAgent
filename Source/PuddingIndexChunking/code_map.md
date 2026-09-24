# PuddingIndexChunking CodeMAP（U4-1a）

> 分块 + 过滤组件：把“一个源文件 → 若干可独立索引的块”做成**可独立测试的纯逻辑**，而不是塞进探针脚本。
> 边界（ADR-089 §2.1）：**叶子** —— `ProjectReference = 0`、`PackageReference = 0`（未新增 NuGet）。
> **关键设计**：outline 由**端口 `IOutlineSource`** 从外部喂入，因此本组件**不引用** `PuddingCodeIntelligence`
> （语言 outliner 所在层）、也不引用 `PuddingRuntime` / `PuddingHost` / `PuddingAgent` / `PuddingCodeIndex` /
> `PuddingRetrievalEval`；测试用替身 outline 即可驱动全部行为。
> 命名空间：`PuddingIndexChunking`

## 模型（ChunkModels.cs）

| 类型 | 用途 |
|------|------|
| `SourceLanguage` | 语言分层（`Unknown=0` / CSharp / TypeScript / Python / Markdown / PlainText），决定过滤规则的取值 |
| `ChunkKind` | 索引层：`Outline=0` / `DocComment=1` / `CodeText=2`，**枚举值即优先级**，对应 ADR-089 C1 的 P0/P1/P2 |
| `ChunkPriorities` | `Of(kind)` → P0/P1/P2；`BoostOf(kind)` → 2.0 / 1.5 / 1.0（**优先级语义在本组件，加权数值由检索侧消费**）|
| `IndexChunk` | 块文本 + 来源文件 + 行范围 + 优先级；**构造即校验**（空文本 / 行号 <1 / 末行早于首行都会抛）|
| `OutlineSymbolKind` / `OutlineSymbol` / `OutlineSymbolSet` | 端口 DTO：符号种类、名字、行范围、签名、修饰符、容器、文档摘要；`Error` 是**数据**不是异常 |
| `IOutlineSource` | **唯一端口**：`GetOutlineAsync(filePath, sourceText, language, ct)` |
| `ChunkAssembly` | 一次装配的结果（行数、符号数、块列表、`OutlineError`）|

## 规则与装配

| 文件 | 用途 |
|------|------|
| `ChunkingOptions.cs` | `ChunkFilterOptions`（开关/阈值覆盖/大小写）+ `ChunkingOptions`（三层开关、`MaxCodeLinesPerChunk`、是否把文档摘要并入 P0）+ `FilterReport` / `FilteredChunkText` |
| `ChunkFilterRules.cs` | C2 默认数据：按语言最小 token 长度（C#/TS/Python=3，md/plain=2）、按语言关键字停用词（C# 全保留字 + 常见上下文关键字；TS；Python；**Markdown 为空集** —— 语言差异是刻意可观察的）|
| `ChunkTokenizer.cs` | 确定性分词：字母/数字/`_` 的极大串；只**整 token 删除**，不合并也不切分标识符 |
| `ChunkFilter.cs` | 规则求值：`IsShortToken` / `IsStopWord` / `ShouldKeep` / `FilterTokens` / `Apply` / `ApplyDetailed`（带 `FilterReport`）。**归因顺序固定：先长度、后关键字**，同一 token 同时命中只算长度。纯函数，无 IO |
| `FileChunkAssembler.cs` | 装配：代码文件 → 每符号一个 **P0 块**（声明文本，**永不包含函数体**）+ 整行注释块 → **P1 块** + 剩余代码行 → **P2 块**（按 `MaxCodeLinesPerChunk` 切段）；非代码文件 → 非空块 → P1 块（无 P0/P2）。`IOutlineSource` 仅在**代码语言且 P0 层开启**时才被咨询 |

## 两个不变量（测试断言）

1. **层级内不重叠**：P1/P2 块范围内不重复覆盖同一行（P0 允许嵌套：类型的行范围本就包含成员的行范围，而 P0 块文本只是声明）。
2. **声明行不复用**：被 P0 消费的声明首行不会再进 P2；被并入 P0 的文档注释块也不会再作为 P1 出现。唯一允许的“有 token 却不在任何块里”的行是那类被 P0 接管的注释块（测试逐行精确断言）。

## 测试工程（Source/PuddingIndexChunkingTests，30 用例）

- `ComponentBoundaryTests`：检测器自检（**阳性对照**）+ 进程未加载 8 个禁用程序集（含 `PuddingCodeIndex` / `PuddingFullTextIndex` / `PuddingRetrievalEval`）+ `deps.json` 依赖闭包 + **读盘校验组件 csproj 的 `ProjectReference`/`PackageReference` 必须为 0**、`InternalsVisibleTo` 只指向自身测试。
- `ChunkFilterTests`：语言相关规则、大小写开关、阈值覆盖、**过滤前后 token 集合的逐项差异**（`Before_And_After_Token_Sets_Differ_Exactly_By_Removed_Tokens`）与**逐规则归因**（关掉某条规则后计数如何移动）。
- `FileChunkAssemblerTests`：按符号切块且不含函数体、文档块不被重复索引、三层可单独关闭、空文件/纯注释文件/空白文件、非代码语言的块划分、切段上限、行号越界钳制、层级内不重叠不变量、优先级映射与 boost 单调、非法输入 fail-closed。

## 接入（S5）

- `Source/PuddingRetrievalEvalProbe` 引用本组件，并作为 **`IOutlineSource` 适配器**（`RoslynCSharpOutlineSource.cs`，经 `PuddingCodeIntelligence` 传递 Roslyn，不新增 NuGet）。
- **未接入 Host/DI**：生产路径把“分块+过滤”固化进索引流程属 ADR-089 §3 的 **U4-3**，本刀只到评测台。
