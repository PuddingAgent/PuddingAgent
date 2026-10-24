---
title: 2026-10-24 harness 与 RSI 外部证据基线
author: hyfree
date: 2026-10-24
last_reviewed: 2026-10-24
status: active
description: "把两份外部调研从 temp 迁入 Docs/14_reports：业界 harness 38 条可核实践（14 来源、39 URL）与 9 个自我改进系统的证据强度对比。两份材料共同支撑一个判断——harness 改动的收益极小且假提升率高，因此 commit 步骤必须给出 per-decision 假接受上界。"
categories: [changelog]
tags: [harness, rsi, evaluation, evidence, docs]
related_docs: [Docs/14_reports/2026-10-24-harness-context-practice-evidence.md, Docs/14_reports/2026-10-24-rsi-evidence-and-accept-gate.md]
related_files: [Tools/Docs/front_matter.py]
slug: changelog-2026-10-24-harness-rsi-evidence
draft: false
---

# 2026-10-24 harness 与 RSI 外部证据基线

## 做了什么

把两份此前落在 `temp/`（已被 gitignore）的调研迁入受版本控制的位置：

| 文件 | 规模 | 内容 |
|---|---|---|
| `Docs/14_reports/2026-10-24-harness-context-practice-evidence.md` | 20,077 B | 六节 38 条实践，14 个独立来源，39 个 URL |
| `Docs/14_reports/2026-10-24-rsi-evidence-and-accept-gate.md` | 16,569 B | 9 个具名自我改进系统，按证据强度排序 |

## 为什么现在做

放在 `temp/` 里的东西不是证据，只是草稿：`temp/` 被 gitignore，一次清理就没了。而这两份材料要支撑的判断会持续影响后续取舍，必须可追溯。

## 两份材料共同支撑的判断

1. **harness 类改动的收益比直觉小得多**：held-out 增益 +0.6pp（含 +0.0）；无单测反馈时 harness evolution 甚至低于直接采样。
2. **朴素准入会骗人**：现有 RSI promote 方案的假提升率最高 20.7%，且"候选依赖先前评估结果"这种依赖关系无法用 Bonferroni 类校正覆盖。
3. **被测者会主动绕过度量**：DGM 论文自述实测到 objective hacking。

结论：commit 这一步不是记账，是被忽视的测量环节。后续任何 harness 改动，都必须先声明 per-decision 假接受上界，且不得用本会话数据作为准入证据。

## 核验

- `Tools/Docs/front_matter.py --check` → `不合规 0 个`（两份均通过）。
- 报告正文随子代理交付写入，父代理未逐字复核全部 39 个 URL；其中「20.7% 假提升率」一条经父代理独立检索交叉印证，其余二手条目已在原文「未确证清单」标注。

## 未做

- 未改动任何运行时代码。
- 未推送（用户 2026-10-24 指示：先不需要推送）。
