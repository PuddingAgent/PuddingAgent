# 从 Source/PuddingCodeIntelligence/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）

> **为什么在这里**：`Source/PuddingCodeIntelligence/code_map.md` 只保留索引（关键概念 · 组件 · 关键文件 · 用途）。原先按轮次/日期堆叠在其中的变更、门禁与验收记录迁出到本文件。日志规则见 `Docs/00_changelog/README.md`。
>
> **内容来源**：迁出前 `Source/PuddingCodeIntelligence/code_map.md` 的原文，**逐字保留，未做删改**（节之间仅插入 `---` 分隔，不改变任何原文字）。原文件快照可 `git show <迁出前提交>:Source/PuddingCodeIntelligence/code_map.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（链接文字未变）；正文反引号内的路径仍保持原文的仓库根口径。
> **覆盖范围**：原文件第 49–70 行，共 2 节；日期 2026-09-25 ~ 2026-09-25。

**〔原文第 49–53 行：变更（2026-09-25，ADR-089 U4-2b）〕**

## 变更（2026-09-25，ADR-089 U4-2b）

`Services/CodeQueryService.cs` 的 `SearchSymbolsAsync` 增加**跨 scope 去重**（按 `SymbolId`，保留首次出现者）—— ADR-089 硬约束 9/10「跨 scope 去重优先于过载判定」。根因：本仓有 4 个互相嵌套的已登记 project，同一符号被各索引一份，未限定 project 的检索必然返回重复（实测 10 条里 5 对）。门禁 `PuddingCodeIntelligenceTests` **95/95**（含 2 新用例）；变异取红与留白见 `Source/PuddingCodeIndex/code_map.md` 的 U4-2b 条目。

---

---

**〔原文第 55–70 行：变更（2026-09-25，B4+ 提取器资产归属与解析基准）〕**

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

