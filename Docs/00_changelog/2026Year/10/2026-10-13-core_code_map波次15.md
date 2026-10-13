---
title: 2026-10-13 PuddingCore code_map 波次 15：4 张小表 + 节前空行归一
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: active
description: "PuddingCore 的 外部 API 安全合同/推理紧凑编解码/检索合同/自动调度契约 共 12 行改 v2 五列，并顺带补回波次 13 遗留的 10 处节前空行；该文件 error 70→61、warn 36→33。glob 长规格下沉为表后三段换行段落。"
categories: [docs, changelog]
tags: [code-map, condensation, v2]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-13-core_code_map波次14.md]
related_files: [Source/PuddingCore/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-2026-10-13-puddingcore-code-map-wave15
draft: false
---

# PuddingCore code_map 波次 15

## 改了什么

1. 四节共 **12 行**改为 v2 五列：`外部 API 安全合同`(2)、`推理紧凑编解码`(1)、`检索合同`(3)、`自动调度契约`(6)；
2. **顺带修复波次 13 的遗留瑕疵**：整段替换会把「节末 → 下一 `##` 标题」之间的空行一并吃掉，本次统一补回 **10 处**节前空行（脚本内做全文件归一 + 断言）；排查时也发现这会连带引起 `header-repeat` 波动，已记录。

**长规格的承载**：`RetrievalGlobMatcher` 的逐条边界（通配符语义、分隔符归一、`*.*` ≡ `*` 规范 3a、`ignoreCase`、缓存 512 等）**下沉为表后三段换行段落**（每行 ≤300、逐字保留），表格行只留「优先级以漏检优先为准」的指针——沿用波次 11 起的成熟模式。

## 验证

| 指标 | 波次 14 后 | 波次 15 后 |
|---|---|---|
error | 70 | **61** |
warn | 36 | 33 |
line-too-long | 17 | 16 |
field-too-long | 53 | 45 |
已改造节 | 6 | 10（+4） |

**PuddingCore 三波累计（13–15）：error 88 → 61（−31%）、warn 48 → 33。** 断言四条照旧（行数守恒 2/1/3/6、旧行 **45 个反引号 token 逐字零丢失**、列长、行长 ≤300）。
dry-run 实报 1 个 token 丢失（`NoMatch/Match/Timeout`——我把它压缩成了 `NoMatch`）⇒ 折回后清零。`git diff --numstat` = 24 插入 / 13 删除。

## 未做（诚实登记）

- PuddingCore 仍 error 61 / warn 33，剩余 7 节未改造：判定算子与分类契约(11 err)、抽象层(12)、LLM 网关(11)、配置 & 序列化(10)、Agent 定义(7)、改进落点契约(6)、子代理编排(4)；
- 本波**未清零**的 warn：`missing-fingerprint` 1、`anti-pattern` 31（清单型 F 22 / 演进叙事 5 / 路线图 4）——anti-pattern 需逐条判定，量大，建议单列一波专门处理。
