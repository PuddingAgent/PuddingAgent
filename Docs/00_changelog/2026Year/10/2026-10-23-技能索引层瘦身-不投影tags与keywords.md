---
title: 2026-10-23 技能索引层瘦身：不投影 tags 与 keywords
author: hyfree
date: 2026-10-23
last_reviewed: 2026-10-23
status: active
description: "常驻 SKILL 索引每行只保留 skillId + 一句话摘要，移除 tags=/keywords= 投影，降低每轮请求的固定上下文体积。"
categories: [docs, changelog]
tags: [context, prompt-cache, skill, progressive-disclosure]
related_docs: [Docs/12_features/技能加载与检索-渐进披露-遥测与负信号设计-2026-09-21.md]
related_files: [Source/PuddingRuntime/Services/ContextPipelineLayers.cs, Source/PuddingRuntimeTests/Services/ContextPipelineSkillLayerTests.cs]
slug: changelog-2026-10-23-skill-index-projection-slim
draft: false
---

# 2026-10-23 技能索引层瘦身：不投影 tags 与 keywords

## 背景

用户提出：常驻技能目录应只加载「目录 + 索引 / 摘要 / 标签 / 关键词」，并采用**渐进性披露**（L0 只放
「skillId + 一句话摘要」，正文按需 `agent_skill read_file` 取）。

对齐既有设计文档 [`Docs/12_features/技能加载与检索-渐进披露-遥测与负信号设计-2026-09-21.md`](../../../12_features/技能加载与检索-渐进披露-遥测与负信号设计-2026-09-21.md)
§5「反模式清单」第 2 条：**把工具名 / 治理标签当技能关键词**（实测 89.1% 噪声）；§9.3 亦指出
`keywords[]`（`file_read` 被 62 条技能声明）是反模式的直接来源。

## 改动

- `Source/PuddingRuntime/Services/ContextPipelineLayers.cs`：常驻 SKILL 索引（`L9-SKILL-CATALOG`）每行不再追加
  `tags=...` 与 `keywords=...`，只投射 `skillId` + `CompactSummary(...)` 一句话摘要；注释改写为渐进性披露口径。
  **只改提示词投影**：`manifest.json` 里的 tags/keywords 仍保留，技能注入匹配与 Hub 检索不受影响。
- `Source/PuddingRuntimeTests/Services/ContextPipelineSkillLayerTests.cs`：原断言「索引包含 `tags=notes, workflow`」
  改为**反向断言**（索引不得含 `tags=` / `keywords=`），其余「摘要可见、正文与版本号/路径不可见」的断言保持不变。

## 验证

- 变异取红：改动前用新断言跑同一测试 → **失败 1 / 通过 4**（`temp\a0-red.txt`）。
- 复原后跑同一测试 → **失败 0 / 通过 5 / 总计 5**（`temp\a0-green.txt`，484 ms）。

## 影响面与体积

- 体积（本会话早前按同一技能集合实测）：287 条技能索引层 ≈ **96,334 字符**；仅 id + 摘要 ≈ **38,125 字符**；
  行内 id 独有部分 ≈ 12,523 字符 ⇒ 本次移除的 tags/keywords ≈ **58,209 字符（约 60%）**。
- 生效条件：源码变更，需重新构建并重启 Core 才是运行态事实（本机运行中的宿主锁定 `bin\Debug` 产物，未在本轮重启）。
- 风险：索引字节改变 ⇒ 一次性 warm-prefix 失效（一轮全额 cache miss），此后每轮固定收益。

## 后续（未做）

- L0 摘要仍有 100 字符上限（`CompactSummary`），可再压到 ≤40 字符并引入层预算。
- 技能检索通道缺失：索引只给 name + 摘要，`agent_skill list` 会全量倾倒，需要一个 `search` 入口（对应设计文档 T4/T5）。
