# PuddingRetrievalEval — 检索评测组件（叶子）

> 上游：`Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` §4（U4-0）。
> 组件化交付规程：S1 独立工程 → S2 独立测试 → S3 无宿主验证 → S4 边界断言 → S5 接入（本刀未接入）。

## 职责

**测量**检索质量（准确率 + 性能），**不改**任何检索行为。它回答"某一版检索比上一版好还是差"，
因此 U4-1（统一忽略）、U4-2（作用域/类型）、U4-4（向量）、U4-5（并行）的收益都由它判定。

## 关键设计：只依赖端口

```
PuddingRetrievalEval ──依赖──▶ ISearchProbe（本组件定义的端口）
                                      ▲
                                      └── 实现：测试里的替身 / temp/U4-0-probe 的 LuceneFullTextProbe
```

- 组件**不引用任何检索引擎**（`ProjectReference = 0`、`PackageReference = 0`），因此换引擎不改评测。
- 真实检索引擎只出现在**适配器**里；适配器不属于组件边界，放在 `temp/U4-0-probe/`（见
  `eval/reports/README.md`）。

## 文件

| 文件 | 内容 |
|---|---|
`Contracts/EvalCase.cs` | `EvalCase` / `EvalKind{Symbol,Intent,Crossref}` / `EvalLanguage{CSharp,TypeScript,Markdown}` / `EvalSet`。`Unknown = 0`，加载器永不产出 `Unknown` |
`Contracts/SearchProbeContracts.cs` | 端口 `ISearchProbe` + `SearchProbeRequest` / `SearchScope` / `SearchProbeHit` / `SearchProbeOutcome`。耗时由**探针**测量（`double` 毫秒，保留亚毫秒分辨率）|
`Services/PathIdentity.cs` | 路径比较规则：大小写不敏感、`\`≡`/`、`./` 去除、期望命中支持后缀匹配（仓储相对路径 vs 绝对命中）、可选 `#Symbol`、`DistinctByFile`（同一文件多行命中只算一次）|
`Services/NoiseDirectoryRules.cs` | **噪声目录**定义 = 现有三套规则的**并集**（`SearchGrepTool.DefaultExcludeDirs` 12 + `IndexExcludePatterns.NoiseDirNames` 28 + `FullTextIndexOptions.ExcludedDirectoryNames` 33 ⇒ 46 个去重段名）。取并集是**保守**方向（宁可多报噪声），因为 U4-1 才是"选哪套"的裁决者 |
`Services/RetrievalMetrics.cs` | `recall@k`(1/5/10) / `MRR` / `precision@k` / `noiseRate@k` + 分子分母原始计数。**分母已在 XML 文档逐条写明** |
`Services/LatencyStatistics.cs` | 最近秩（nearest-rank）百分位 + `LatencySummary`（min/p50/p95/p99/max/mean）；空桶返回 `Empty`（"无样本"，不是"很快"）|
`Services/EvalSetLoader.cs` | 受版本控制标注集（`eval/sets/*.json`）的加载器，**fail-closed**：未知 `kind`/`language`、空 query、空/重复 `expectedHits`、非法 JSON 一律抛 `EvalSetFormatException` |
`Services/EvalRunner.cs` | 每个 case 的调用序列 = **1 次冷调用** → `WarmupRepetitions` → `MeasuredRepetitions`；冷/热分开计时，准确率取**第一个已测重复**；并检测重复间是否一致（不可复现的准确率必须被看见）。不吞异常：空用例集 / `MaxResults < 10` / 负 warmup 一律拒绝 |
`Services/EvalRun.cs` | 运行结果模型（逐 case 分数 + 分层聚合 + 冷/热 `LatencySummary` + 冷调用原始样本）|
`Services/EvalReportWriter.cs` | Markdown + JSON（`ToMarkdown` / `ToJson` 纯函数；`Write` 落盘 UTF-8 无 BOM）。报告内**显式声明不设阈值** |
`eval/sets/seed-v1.json` | **种子标注集**：80 条（C# 36 / TS-TSX 22 / md 22；symbol 36 / intent 26 / crossref 18），共 98 个 (query, 期望文件) 对 |
| `eval/reports/*.md,*.json` | 首份真实基线（Lucene 全文索引面），见 `eval/reports/README.md` |
| `eval/reports/u4-3-frozen-{f32,i8}-{all,p0}.*` | **U4-3 四变体矩阵**（同一 1,352 块语料：float32/int8 × 全 tier/仅 P0），报告 `temp/U4-3-REPORT.md`。`.probe.json` 里记 `VectorFormat` / `VectorTierFilter`，与索引 JSON 的 `VectorFormat` 对得上 |
| `eval/reports/u4-3-{f32,i8}-{all,p0}.*` | 同四变体在**活仓库语料**（U4-2a 提交后 61 文件 / 2,177 块）上的稳健性复跑 |

## 指标定义（分母固定，禁止口头约定）

- `recall@k` = top-k 命中的**去重**期望数 ÷ 期望总数（期望为空 ⇒ 0）
- `MRR` = 首个命中任一期望的命中位置的倒数（1-based；无命中 ⇒ 0）
- `precision@k` = top-k 中命中期望的条数 ÷ **k**（不是"实际返回条数"——返回太少是真实损失）
- `noiseRate@k` = top-k 去重文件中落在噪声目录的比例 ÷ top-k 去重文件数（窗口为空 ⇒ 0）
- 所有指标先对命中列表做**按文件去重**（同文件多行命中只算一次），否则一个"话多"的文件能占满整个 top-k

## 边界（S4，机器可验）

- 本工程 `ProjectReference = 0`、`PackageReference = 0`。
- 组件源码内对 `PuddingRuntime` / `PuddingHost` / `PuddingAgent` / `PuddingCodeIndex` / `PuddingCodeIntelligence` /
  `PuddingFullTextIndex` / `Lucene.*` / `Microsoft.CodeAnalysis.*` / `Microsoft.Build.*` 的**代码引用 = 0**
  （仅 `NoiseDirectoryRules.cs:11-13` 的 XML 注释里出现来源文件路径）。
- 运行时断言在 `Source/PuddingRetrievalEvalTests/ComponentBoundaryTests.cs`：进程内/依赖闭包中不得出现上述程序集，
  且**检测器自带阳性对照**（`Boundary_Detector_Flags_Forbidden_Names`）。
- 变异取红实测：临时给本工程加一条指向 `PuddingFullTextIndex` 的 `ProjectReference` ⇒ 断言变红并点出
  `Lucene.Net, Lucene.Net.Analysis.Common, ..., PuddingFullTextIndex` 6 个程序集；撤销后 `git hash-object`
  逐位相同（`19dd4f5946fb16468a9d7decf883a20227f67414`）且 78/78 全绿。

## 未做（诚实留白）

- **S5 未接入**：本刀不改 Host/DI，不注册进 DI 组合根，也没有宿主侧消费方。
- 评测组件未内置"毫秒级达标阈值"（by design；见 `eval/reports/README.md`）。
- 冷样本只是"该查询在本进程内的第一次调用"，不是"进程刚启动 / OS 页缓存冷"；跨进程冷启动未测（见 `temp/U4-0-REPORT.md` RISKS）。
- markdown 分层下 `noiseRate@10 = 0` 是真实值（`Docs/` 内无噪声段名），不代表 U4-1 无收益：收益在 `Source/` 与
  根 scope（`.pudding` 20,926 个可索引文件、`.tmp-build` 1,335 个）。

## U4-6 验收：根 scope 索引 + 全 80 条覆盖（2026-09-24）

| 文件 | scope | 用例数 | 说明 |
|---|---|---|---|
`u4-6-root-scope-2026-09-24.md/.json` | 仓库根（`.`）| **80** | U4-6 后根 scope 索引可行（**1.9 分钟 / 4,432 文件 / 97.8 MB**，旧实现 77 × 195 s ≈ 4.2 h）⇒ 覆盖度 78/80 → **80/80**（此前 2 条锚定仓储根文件 `Agents.md` / `Agents-Hygiene.md` 的用例「结构上不可测」）；recall@1 0.3000 / MRR 0.4217 / noiseRate@10 0.0000；冷 p50 12.077 / 热 p50 12.305，**热 p95 47.008 ms** |

⚠️ **分层 recall@1 = C# 0.6111 / TS 0.0682 / md 0.0227**（同批 md 用例在 `Docs/` 子 scope 下为 0.2500）
⇒ **检索面变大后词法召回被稀释**；这是 U4-2（作用域 + 文件类型合同化）与 U4-4（向量 + RRF 融合）的靶子。

⚠️ 仪器瑕疵：报告内 `## Reproduce` 段落打印的是 `temp/U4-0-probe/PuddingRetrievalEvalProbe/...`，
而活的探针工程在 `Source/PuddingRetrievalEvalProbe/`；复现按后者执行（待修）。
