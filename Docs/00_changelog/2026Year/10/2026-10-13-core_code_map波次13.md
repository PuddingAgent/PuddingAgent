---
title: 2026-10-13 PuddingCore code_map 波次 13：4 节表列式改造（跨项目开局）
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: active
description: "code_map 规范化跨出 PuddingPlatform：PuddingCore 的 工具契约/存储管理契约/任务系统契约/事件 & 观测 4 节共 17 行改为 v2 五列；该文件 error 88→81、warn 48→44。确立跨项目波次规程（结果断言须限定在本次替换区块）。"
categories: [docs, changelog]
tags: [code-map, condensation, v2]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-13-平台code_map波次12收官.md]
related_files: [Source/PuddingCore/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-2026-10-13-puddingcore-code-map-wave13
draft: false
---

# PuddingCore code_map 波次 13（跨项目开局）

## 为什么换项目

PuddingPlatform 已达 **error 0 / gate PASS**（波次 12）。全仓普查给出新靶子：44 个 code_map 合计 **error 387 / warn 219**，按文件排序最重的是
`Source/PuddingCore/code_map.md`（**88 err / 48 warn**）→ 本波起转入跨项目波次。

## 改了什么

`Source/PuddingCore/code_map.md` 四节共 **17 行**改为 v2 五列：

| 节 | 行数 | 原违规 |
|---|---|---|
`## 工具契约` | 2 | 1 err |
`## 存储管理契约（Storage/）` | 1 | 1 err |
`## 任务系统契约（Tasks/）` | 9 | 4 err |
`## 事件 & 观测` | 5 | 1 err |

## 验证

| 指标 | 改前 | 改后 |
|---|---|---|
error | 88 | **81** |
warn | 48 | 44 |
line-too-long | 19 | 18 |
field-too-long | 69 | 63 |
已改造节 | 0 | **4（17 行）** |

断言四条照旧（行数守恒 17/17、旧行 **34 个反引号 token 逐字零丢失**、列长、行长 ≤300），本轮 dry-run **首轮报 2 token 丢失**（`(no dependencies)`、`(cycle detected)`）⇒ 折回后清零。
`git diff --numstat` = 21 插入 / 22 删除（改动面限于 4 节）。

## 跨项目新规程（本次踩到并修正）

**结果态断言必须限定在「本次替换的区块」内**。首轮我把「全文无 >300 行」写成全文件断言 ⇒ 被该文件**别处固有的 18 行超长**判红、拒绝写盘（fail-closed 正确，但断言口径错）。修正为：**只校验被替换区块的每行长度 + 标题齐全 + 五列表头数**（该文件固有债务交由后续波次处理）。这与波次 11 的教训同源：**断言要断言"我改的东西"，而不是"整个世界"**。

## 未做（诚实登记）

- PuddingCore 仍 error 81 / warn 44，剩余 14 节未改造，按规模排序：判定算子与分类契约(11 err)、抽象层(12)、LLM 网关(11)、配置 & 序列化(10)、Goal 域(8)、Agent 定义(7)、子代理编排(4)、检索合同(4)、自动调度契约(3)、模型(3)、其余小节各 1；
- 本波**未处理**该文件的 `missing-fingerprint` 与 `header-repeat`（各 1 warn）。
