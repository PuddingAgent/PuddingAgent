---
title: 2026-10-08 波次 1：PuddingCodeIndex code_map 契约表改造为规范 v2 列式 schema
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
status: archived
description: "把 Source/PuddingCodeIndex/code_map.md 的「契约」表从 2 列（文件/用途）改造成规范 v2 的 5 列 schema：用途压回一条职责命题（≤80 字符），把此前塞在用途里的演进叙事剥离出去，把不可从代码推断且删掉会导致误改的约束移入独立「约束」列；关键符号/关联两列按规范化身份填写。检查器 error 50 → 35、用途列中位数 86 → 30 字符。"
categories: [docs, code-map, condensation]
tags: [code-map, spec-v2, schema, condensation]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-07-code_map规范v2与自检.md]
related_files: [Source/PuddingCodeIndex/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-code-map-puddingcodeindex-contracts-table-2026-10-08
draft: false
---

# 2026-10-08 波次 1：PuddingCodeIndex code_map 契约表改造为规范 v2 列式 schema

## 背景

`Source/PuddingCodeIndex/code_map.md` 是全仓 `error` 最集中的三个文件之一（基线 50 error）。根因不是内容错，而是**schema 走形**：

- 表只有两列 `| 文件 | 用途 |`，于是「关键符号」「关联」「约束」三类信息**全挤进用途单元格**；用途列中位数 **86 字符**、p90 **384**、最大 **1129**（规范 §2 硬上限 80）。
- 用途里混着**演进叙事**（`U3-B1`/`U3-B3`/`D2`/`D3`/`D4` 标记与日期、`增…`、`不在这里写库` 等），而规范 §6 明示历史应从 `Docs/00_changelog/` 读、索引里不写。

## 改动（本卡 = 波次 1，只动「契约（Contracts/）」这一张表）

按规范 §2 的固定列序改成 5 列 `| 文件 | 用途 | 关键符号 | 关联 | 约束 |`，24 行逐行**手写重写**（规范 §2 明禁工具自动压缩/截断，修复必须由模型读代码后手写）：

| 列 | 处置 |
|---|---|
用途（F） | 每条压成**一条职责命题**（如「索引存储端口」），全部 ≤80 字符；剥离演进叙事 |
关键符号（A） | 填该文件对外稳定入口（≤5 项且 ≤100 字符）——契约类文件的「符号」正是其正文，这一列把原先挤在用途里的符号名**原样接住** |
关联（R） | 只写规范化身份（如 `Source/PuddingCodeIntelligence/code_map.md`），其余留 `—` |
约束（S） | 只写**两个问题都答"是"**的内容（不可从代码推断 且 删掉会导致误改），例如「水位按消费者分别推进」「`Complete=false` 的行不得当作已应用」；不满足则留 `—` |

## 验证（检查器同版本 `tool_sha256=2d1ae2a1…`，改前/改后各跑一次）

| 指标 | 改前 | 改后 |
|---|---|---|
`error` | 50 | **35**（−15） |
`warn` | 17 | **14** |
`line-too-long` | 19 | **13** |
`field-too-long` | 31 | **22** |
`anti-pattern` | 16（清单型 F=10 / 演进叙事=2 / 路线图=4） | **13**（清单型 F=7 / 演进叙事=2 / 路线图=4） |
用途列（F） | median 86 / p90 384 / max 1129 | **median 30** / p90 371 / max 1129（长尾在**未动的其它表**） |
关键符号（A）/关联（R）/约束（S） | n=0（列不存在） | **n=24**，A median 43 / max 87，S max 87 |
字节 | 25335 | 24451 |
行数 / 表数 / 条目行 | 106 / 4 / 58 | **106 / 4 / 58（结构不变）** |

写入采用脚本精确块替换，**写前断言**全部通过才落盘：表头锚点唯一（candidates=1）、旧行数=新行数=24、块内 23 个反引号文件名 token **零丢失**、新块每格不超该列硬上限、最长行 ≤300；写后 `readback_match=True`。

> 注：写前断言**实际拦下一次违规**——首版有 5 个「关键符号」格 >100 字符、最长行 305 >300，脚本 fail-closed 未写盘，按上限收敛后才落盘。

## 遗留（波次 2+）

1. 该文件另外 3 张表仍是 2 列（`变更捕获管线` 等），`error` 余量 35 全在这里；另有两行（L59/L60）是 CRLF、其余为 LF，属历史混用。
2. 缺 §5 头部源指纹：检查器已给出可核实的实测值（scope `Source/PuddingCodeIndex/code_map.md`，`file_count=86`，`digest=9dbf49ed441c`，`value=86-9dbf49ed441c`）；写入前需先从 `Tools/Docs/code_map_check.py` 读出**声明的确切语法**，避免把 warn 从 `missing-fingerprint` 换成 `stale-fingerprint`。
