---
title: 2026-11-23 front matter tags 消费者核查（Q4 决定不改的取证）
author: hyfree
date: 2026-11-23
last_reviewed: 2026-11-23
status: active
description: "核查 front matter tags 的生成与消费链路，结论是除生成与「非空」校验外没有任何消费者（Admin 前端只剥离不解析），因此改变 CJK 切词粒度无可测收益，决定不改并给出将来重测的触发条件。"
categories: [docs, reports]
tags: [front-matter, tags, cjk, decision]
related_docs: [Docs/10_conventions/code-map-规范-v2.md]
related_files: [Tools/Docs/front_matter.py, Source/PuddingPlatformAdmin/src/pages/chat/documents/frontMatter.ts]
slug: reports-2026-11-23-front-matter-tags-consumers
draft: false
---

# front matter tags 消费者核查

## 问题

`Tools/Docs/front_matter.py:224` 的 `cjk_chunks(run, size=6)` 把**文件名**里的汉字串切成 ≤6 字块
（`索引新鲜度信号` → `索引新鲜度信` + `号`），再由 `tags_for()` 组装成 tags（上限 8）。

待决问题：改成按词切分是否值得？影响面据称涉及 721 个 md。

## 核查（四条正向取证）

| # | 取证点 | 结果 |
|---|---|---|
| 1 | `front_matter.py` 内部用法 | tags 只做两件事：`--fix` 生成、`--check` 校验**非空**（L410/420）；无筛选、无索引 |
| 2 | `PuddingPlatformAdmin/.../documents/frontMatter.ts` | `splitFrontMatter()` 返回 `{ frontMatter /* 原始字符串 */, body }`，**不解析任何字段** |
| 3 | `PuddingPlatformAdmin/src/pages/chat/**` grep `tags` | **0 命中**（唯一相关是测试里的 "lookalike tags"，无关） |
| 4 | 仓库级 grep `front_matter` | 只命中工具自身与 `.work/**` 归档副本 |

第 2 条是关键：前端确实会读带 front matter 的文档，但**只为把头部剥掉再渲染正文**，
字段一个都不解析 —— 所以 tags 的「好坏」在 UI 上是不可见的。

## 结论

**不改切词粒度。**

理由：tags 生成后没有被任何检索、索引或 UI 使用 ⇒ 改切词方式的影响面 ≈ 0，
收益无法测量。为一个不可观测的差异去改 721 个文件不成立。

## 将来何时重做

**触发条件**：文档检索真的用上 tags（例如按 tag 过滤或 tag 参与打分）时。

届时测量口径（沿用但必须换样本来源）：

- 样本：**真实查询日志**取 50 条，不用人工编造
- 判据：词切相对 6 字切，命中数变化 ≤ -20%（即不显著变差）且新增误报 ≤ 10%
- 若判据不满足：保留 6 字切，并把这次测量结果存档

## 不要混淆的两件事

`cjk_chunks` 的 docstring 记录过一个**已修**的历史缺陷：原实现用贪婪正则 `{2,6}`，
在「长度 ≡ 1 (mod 6)」的汉字串上会丢 1 字余尾（实测 127/721 文件命中，如
`索引新鲜度信号` 丢「号」）。那是**丢字**问题，已修；本文讨论的是**切词粒度**，两者无关。
