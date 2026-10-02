# 从 Source/PuddingRetrievalEval/code_map.md 迁出的历史变更记录（迁出日 2026-10-02）

> **为什么在这里**：`Source/PuddingRetrievalEval/code_map.md` 只保留索引（关键概念 · 组件 · 关键文件 · 用途）。原先按轮次/日期堆叠在其中的变更、门禁与验收记录迁出到本文件。日志规则见 `Docs/00Changelog/README.md`。
>
> **内容来源**：迁出前 `Source/PuddingRetrievalEval/code_map.md` 的原文，**逐字保留，未做删改**（节之间仅插入 `---` 分隔，不改变任何原文字）。原文件快照可 `git show <迁出前提交>:Source/PuddingRetrievalEval/code_map.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（链接文字未变）；正文反引号内的路径仍保持原文的仓库根口径。
> **覆盖范围**：原文件第 70–80 行，共 1 节；日期 2 ~ 4。

**〔原文第 70–80 行：U4-6 验收：根 scope 索引 + 全 80 条覆盖（2026-09-24）〕**

## U4-6 验收：根 scope 索引 + 全 80 条覆盖（2026-09-24）

| 文件 | scope | 用例数 | 说明 |
|---|---|---|---|
`u4-6-root-scope-2026-09-24.md/.json` | 仓库根（`.`）| **80** | U4-6 后根 scope 索引可行（**1.9 分钟 / 4,432 文件 / 97.8 MB**，旧实现 77 × 195 s ≈ 4.2 h）⇒ 覆盖度 78/80 → **80/80**（此前 2 条锚定仓储根文件 `Agents.md` / `Agents-Hygiene.md` 的用例「结构上不可测」）；recall@1 0.3000 / MRR 0.4217 / noiseRate@10 0.0000；冷 p50 12.077 / 热 p50 12.305，**热 p95 47.008 ms** |

⚠️ **分层 recall@1 = C# 0.6111 / TS 0.0682 / md 0.0227**（同批 md 用例在 `Docs/` 子 scope 下为 0.2500）
⇒ **检索面变大后词法召回被稀释**；这是 U4-2（作用域 + 文件类型合同化）与 U4-4（向量 + RRF 融合）的靶子。

⚠️ 仪器瑕疵：报告内 `## Reproduce` 段落打印的是 `temp/U4-0-probe/PuddingRetrievalEvalProbe/...`，
而活的探针工程在 `Source/PuddingRetrievalEvalProbe/`；复现按后者执行（待修）。

