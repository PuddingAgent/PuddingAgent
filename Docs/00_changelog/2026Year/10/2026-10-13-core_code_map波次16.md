---
title: 2026-10-13 PuddingCore code_map 波次 16：LLM 网关表列式改造
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: active
description: "PuddingCore「LLM 网关（Core/）」10 行改 v2 五列，长规格（ADR-077 图片/Files、V5 视觉合同、请求级预算账本）下沉为 5 行表后段落；该文件 error 61→50、warn 33→30，四波累计 88→50（−43%）。"
categories: [docs, changelog]
tags: [code-map, condensation, v2]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-13-core_code_map波次15.md]
related_files: [Source/PuddingCore/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-2026-10-13-puddingcore-code-map-wave16
draft: false
---

# PuddingCore code_map 波次 16

## 改了什么

`## LLM 网关（Core/）` **10 行**改为 v2 五列：三 Gateway（OpenAI 兼容 / Responses / Anthropic Messages）、`LlmVisualInputPlanner`、`VisionCapabilityContract`、`VisualInputRequestBudget`、`DeepSeekFilesApiClient`、两个值类型与 `IFileRefStore`。

**本表的难点是「单行 token 极密」**（如 Responses 行含 3 个 token、VisionCapabilityContract 行含 8 个），因此采用**表格精简 + 表后细节段落**：表格行的「约束」只留可判定的结论（如「上限只可收紧不可放宽」「越界整请求 fail closed」），而 ADR-077 图片/Files 语义、V5 视觉合同交集规则、请求级账本口径等**逐字下沉为 5 行段落**（每行 ≤300）。这是波次 11 起验证过的模式，本次首次用于「一行含 8 token」的极端情况。

## 验证

| 指标 | 波次 15 后 | 波次 16 后 |
|---|---|---|
error | 61 | **50** |
warn | 33 | 30 |
line-too-long | 16 | 13 |
field-too-long | 45 | 37 |
已改造节 | 10 | 11（+1） |

**PuddingCore 四波累计（13–16）：error 88 → 50（−43%）、warn 48 → 30。**
断言四条照旧：行数守恒 10/10、旧行 **39 个反引号 token 逐字零丢失**（含 `function_call_output.output`、`ImageTokenEstimatorVersion`、`RejectVisionLimitAboveCeiling` 等）、列长上限、行长 ≤300；结果态断言（标题 19 齐全 + 替换区块无超长行）全绿 ⇒ **dry-run 首轮即零违规**（连续第三波）。`git diff --numstat` = 26 插入 / 19 删除。

## 未做（诚实登记）

- PuddingCore 仍 error 50 / warn 30，剩余 6 节未改造：抽象层(12 err)、判定算子与分类契约(11)、配置 & 序列化(10)、Agent 定义(7)、改进落点契约(6)、子代理编排(4)；
- warn 侧未清零：`anti-pattern` 28（清单型 F 19 / 演进叙事 5 / 路线图 4）、`missing-fingerprint` 1、`header-repeat` 1。
