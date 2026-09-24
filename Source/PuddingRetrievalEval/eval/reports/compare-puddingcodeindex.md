# U4-1a 对比：plain 全文 vs outline 优先分块 + 过滤（小目录）

> scope = `Source/PuddingCodeIndex`（37 个 `.cs` / 237,586 B，C# 单一语言）
> 标注集 = `Source/PuddingRetrievalEval/eval/sets/small-puddingcodeindex.json`（28 条：symbol 20 + intent 8）
> 运行 = `--warmup 1 --measured 3`，每变体从**空索引根**开始；六个变体 `repetitionStable=True`、失败用例 0。
> 本文件是**汇总表**；每个变体的逐用例原始数字在其同名 `.md/.json` 里，原始 stdout 在 `temp/U4-1a-logs/`。

## 1. 四轴（主轴：plain vs outline）

| 轴 | plain（现状） | outline（P0+P1+P2 + 双规则） | outline-p0p1（只 P0+P1） |
|---|---|---|---|
| 索引文档数 | 引擎不报告（每非空行一条） | 1,352（Outline 475 / Doc 160 / Code 717） | 635（475 / 160 / 0） |
| **索引体积** | **283,159 B**（276.5 KiB，6 文件） | **149,556 B**（146.1 KiB，5 文件）＝ **−47.2%** | **89,769 B**（87.7 KiB，5 文件）＝ **−68.3%** |
| **索引耗时**（harness） | **1,987 ms** | **2,037 ms**（语料 340 + 写入 ≈1,697） | 2,261 ms（语料 358） |
| 引擎自报写入 | 1,972 ms | 1,684 ms | 1,888 ms |
| **检索质量** `recall@1` | 0.7500 | 0.7500 | 0.7500 |
| `recall@5` | 0.8214 | 0.8214 | 0.8214 |
| `recall@10` | 0.9286 | 0.9286 | 0.9286 |
| `MRR` | 0.7885 | **0.7941** | **0.7941** |
| `precision@5` | 0.1643 | 0.1643 | 0.1643 |
| `precision@10` | 0.0929 | 0.0929 | 0.0929 |
| `noiseRate@10` | 0.0000 | 0.0000 | 0.0000 |
| **查找延迟** 冷 `p50/p95/p99` | 3.674 / 7.821 / 546.517 ms | 4.058 / 6.556 / 550.015 ms | 4.345 / 9.608 / 774.696 ms |
| 热 `p50/p95/p99` | 3.702 / 5.984 / 8.510 ms | 3.871 / 6.130 / 7.751 ms | 4.454 / 7.286 / 9.650 ms |

> 冷 `p99`（≈0.5 s）在**两个策略里都是**“本进程首次查询”的 Jieba 词典冷启动，不是策略差异度量。
> plain 的文档数不可得：`BuildIndexAsync` 只报文件数，**表里留空而不是填估算值**。

## 2. 过滤规则各自贡献（同 strategy `p0p1p2`，只切 `--filter`）

| 规则 | 文档数 | 入库 token | 索引字节 | `recall@1/@5/@10` | `MRR` | `precision@5` | 热 `p50/p95` |
|---|---|---|---|---|---|---|---|
| `none`（基准） | 1,363 | 21,216 | 173,627 B | 0.7500 / 0.8214 / 0.8929 | 0.7949 | 0.1643 | 3.506 / 5.845 |
| 只关短 token（`length`） | 1,363 | 19,323 | 165,519 B（−4.7%） | 0.7500 / **0.8571** / 0.9286 | **0.7971** | **0.1714** | 3.542 / 5.902 |
| 只关关键字（`stopwords`） | 1,352 | 16,685 | 155,430 B（−10.5%） | 0.7500 / 0.8214 / 0.9286 | 0.7902 | 0.1643 | 4.060 / 9.522 |
| 两条同开（`both`） | 1,352 | 15,331 | 149,556 B（−13.9%） | 0.7500 / 0.8214 / 0.9286 | 0.7941 | 0.1643 | 3.871 / 6.130 |

语料共 **21,533 token**：长度规则删 **2,172**、关键字规则删 **3,741**（同一 token 同时命中时按“先长度”归因）。

## 3. 结论

1. **outline 用 −47.2% 的索引体积换来不差的质量**（283,159 → 149,556 B；七项质量指标持平，`MRR` 0.7885 → 0.7941 略升）；代价是索引耗时 +2.5%、热 `p50` +4.6%（热 `p99` 反而 −8.9%）。
2. **只索引 P0+P1 是本次最划算的配置**：89,769 B = plain 的 31.7%、outline 的 60.0%，而质量指标与 outline **逐项相同** ⇒ 在这批标注上 P2 代码正文没有贡献召回（适用范围见 ADR-089 §5.5，不外推）。
3. **两条过滤规则都有效且都不伤质量**：−4.7%（长度）/ −10.5%（关键字）/ −13.9%（两条）；两条同开时 `recall@10` 0.8929 → 0.9286。

## 4. 文件清单

| 文件 | 内容 |
|---|---|
| `compare-puddingcodeindex-plain.{md,json}` | S1 现状（引擎目录遍历）逐用例结果 |
| `compare-puddingcodeindex-outline.{md,json}` | S2（分块 + 双规则）逐用例结果 |
| `compare-puddingcodeindex-outline-nofilter.{md,json}` | 关掉两条规则的对照 |
| `compare-puddingcodeindex-outline-nostopwords.{md,json}` | 只开长度规则的对照 |
| `compare-puddingcodeindex-outline-nolength.{md,json}` | 只开关键字规则的对照 |
| `compare-puddingcodeindex-outline-p0p1.{md,json}` | 只索引 P0+P1（不要代码正文）的对照 |
| `temp/U4-1a/<variant>.index.json` | 每变体的索引侧原始数字（字节数、文档数、token 归因、耗时）；`temp/` 不入版本控制 |
| `temp/U4-1a-logs/*.txt` | 每步原始 stdout |

复现：`pwsh -NoProfile -File temp/run-u4-1a-matrix.ps1`（6 变体）后 `pwsh -NoProfile -File temp/summarize-u4-1a.ps1`（汇总）。
