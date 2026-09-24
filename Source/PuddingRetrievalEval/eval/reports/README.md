# U4-0 检索评测基线报告（2026-09-24）

> 本目录下的报告由 `PuddingRetrievalEval`（测量仪器）+ `temp/U4-0-probe/PuddingRetrievalEvalProbe`（真实检索面适配器）
> 生成，**只报原始数字，不内置任何"达标阈值"**。阈值是产品决策（见 `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` §4），
> 不属于仪器。
>
> 每次报告都是 `Markdown`（人读）+ `JSON`（机器比对，用于回归）。JSON 里含每条 case 的命中列表与每次调用的耗时原始值。

## 本次基线覆盖的检索面

**Lucene 全文索引**（`Source/PuddingFullTextIndex`，`LuceneSearchEngine`）—— 它正是 `search_grep` 的**快速候选路径**
（`SearchGrepTool` 持有 `IFullTextSearchEngine`，先向它要候选文件，再用托管扫描复核，见
`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs:340`）。适配器 `LuceneFullTextProbe`
是唯一知道该引擎的地方；评测组件只依赖 `ISearchProbe`。

## 报告清单

| 文件 | scope | 用例数 | 说明 |
|---|---|---|---|
`baseline-2026-09-24-source-scope.md/.json` | `Source/` | 58 | C# 36 + TS/TSX 22，主基线（两语言混合）|
`baseline-2026-09-24-source-scope.csharp.md/.json` | `Source/` + `file_ext=.cs` | 36 | C# 分层（查询期类型过滤）|
`baseline-2026-09-24-source-scope.typescript.md/.json` | `Source/` + `file_ext=.ts;.tsx` | 22 | TS/TSX 分层 |
`baseline-2026-09-24-docs-scope.md/.json` | `Docs/` | 20 | markdown 分层 |
`baseline-2026-09-24-docs-scope.markdown.md/.json` | `Docs/` + `file_ext=.md` | 20 | markdown 分层（查询期类型过滤）|

## U4-1a 小目录策略对比（2026-09-24）

同一批报告目录下另有一组**策略对比**报告（scope = `Source/PuddingCodeIndex`，标注集 `eval/sets/small-puddingcodeindex.json`，28 条）：

| 文件 | 策略 |
|---|---|
`compare-puddingcodeindex-plain.{md,json}` | S1 现状：引擎目录遍历（全文、每非空行一条文档）|
`compare-puddingcodeindex-outline.{md,json}` | S2：outline 优先分块（P0+P1+P2）+ C2 双过滤规则 |
`compare-puddingcodeindex-outline-nofilter.{md,json}` | 同 S2，关掉两条过滤规则 |
`compare-puddingcodeindex-outline-nostopwords.{md,json}` | 同 S2，只开最小 token 长度规则 |
`compare-puddingcodeindex-outline-nolength.{md,json}` | 同 S2，只开关键字停用词规则 |
`compare-puddingcodeindex-outline-p0p1.{md,json}` | 同 S2，但只索引 P0（outline）+ P1（注释），不要代码正文 |

四轴汇总表与结论在 `compare-puddingcodeindex.md`（每个“更好/更差”的说法都带两组数字）；受版本控制的正式结论追加在
`Docs/Features/ADR-089-索引策略优先级-2026-09-24.md` §5；完整证据链 `temp/U4-1a-REPORT.md`。
运行方式：`pwsh -NoProfile -File temp/run-u4-1a-matrix.ps1`（会把每步原始 stdout 落到 `temp/U4-1a-logs/`）。

## 头部数字（原始值，未判定）

| scope | 用例 | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
Source（C#+TS） | 58 | 0.4224 | 0.6638 | 0.6638 | 0.5768 | 0.1621 | 0.0810 | 0.0290
Source（C#） | 36 | 0.6389 | 0.6944 | 0.6944 | 0.7292 | — | — | 0.0439
Source（TS/TSX） | 22 | 0.0682 | 0.6136 | 0.6136 | 0.3273 | — | — | 0.0045
Docs（md） | 20 | 0.2500 | 0.4750 | 0.5250 | 0.4238 | 0.1300 | 0.0700 | 0.0000

## 延迟（原始值，冷/热分离；单位 ms，3 位小数）

| scope | bucket | n | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|---|
Source | cold（每查询首次） | 58 | — | 7.126 | 27.216 | 625.385 | 625.385 | 21.264
Source | warm（其后全部） | 232 | — | 7.116 | 26.548 | 31.313 | 33.081 | 10.446
Docs | cold | 20 | — | 4.189 | 9.922 | 567.352 | 567.352 | 33.170
Docs | warm | 80 | — | 4.175 | 7.698 | 12.896 | 12.896 | 4.685

## 索引构建（同一引擎，同一 scope）

| scope | 可索引文件 | 可索引字节 | `BuildIndexAsync` 实测 |
|---|---|---|---|
`Source/` | 3,514 | 52,239,306 | **462,192 ms**（≈7.7 min） |
`Docs/` | 680 | 11,327,664 | **13,058 ms** |

仓储根 `E:\github\AgentNetworkPlan\PuddingAgent` 作为 scope **未建索引**：实测一次完整遍历 `walkMs = 195,536 ms`、
可索引文件 **28,178**（743 MB），而 `LuceneSearchEngine.BuildIndexAsync` 对**每一种扩展名各遍历一次**目录树
（`FullTextIndexOptions.PlainTextExtensions ∪ ParsedExtensions` = 77 个模式）⇒ 根 scope 的索引构建量级为小时级，
本刀不等待。明细见 `temp/U4-0-probe/corpus-inventory*.txt`。

## scope 与用例裁剪规则（为什么有两份 scope 报告）

一次 `ISearchProbe` 调用只能限定**一个根目录**。为了避免"把结构上不在 scope 内的用例算成 0 分"这种假数字，
适配器用 `--expected-under <prefix>` **只保留期望命中全部落在该前缀下的用例**，并打印被丢弃的用例清单：

- `Source/` scope：80 → 58（丢弃 22 条 markdown 用例）
- `Docs/` scope：80 → 20（丢弃 58 条 C#/TS 用例 + 2 条锚定在仓储根 `Agents.md` / `Agents-Hygiene.md` 的用例）

⇒ 两次运行合计覆盖 **78/80** 条用例。剩余 **2 条**（期望命中为仓储根文件）在当前引擎下**无法测量**：
它们的 scope 只能是仓储根，而根 scope 的索引构建不可行（见上）。**这正是 U4-1 的收益口径之一**：
统一忽略合同把根 scope 的可遍历规模压下来之后，这两条才可测。

## 复现

见每份报告内的 `## Reproduce` 段落（由适配器写入的完整命令行），以及
`temp/U4-0-probe/measure-source.txt` / `measure-docs.txt` 的原始 stdout。
