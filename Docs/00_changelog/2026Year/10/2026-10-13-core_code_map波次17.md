---
title: 2026-10-13 PuddingCore code_map 波次 17：抽象层改 v2 五列（error 50→38）
author: hyfree
date: 2026-10-13
last_reviewed: 2026-10-13
status: active
description: "把 Source/PuddingCore/code_map.md 的「抽象层」节改为 v2 五列（15 行），长细节下沉为表后换行段落；该文件自检 error 50→38、warn 30→28。含一次『工作树未提交改动的独立验收 + 缺陷修复』过程与一条新的工具环境事实。"
categories: [docs, changelog]
tags: [code-map, condensation, v2, verification]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-13-core_code_map波次16.md]
related_files: [Source/PuddingCore/code_map.md, Tools/Docs/code_map_check.py]
slug: changelog-2026-10-13-core-code-map-wave17
draft: false
---

# PuddingCore code_map 波次 17（抽象层）

## 起点：工作树里已有未提交的改动（先验收，不重做）

本轮开工先核磁盘：`git status --porcelain` 显示 **`Source/PuddingCore/code_map.md` 已在工作树被修改但未提交**（`git diff --numstat` = 23 插入 / 17 删除），内容是 `## 抽象层` 节已被改成 v2 五列。

- **来源未证实**：与波次 13–16 同一风格（五列 schema、`细节见后注` 指针、⛔ 标记保留），推测为**某个被中断的先前轮次**留下；**没有任何对应 changelog 文件**（`Docs/00_changelog/2026Year/10/` 下无波次 17 记录）。
- 处置：按纪律**先独立验收、再收口**，未重做该节。

## 独立验收（四项断言，我自己的脚本 `temp/hb17_verify.py`）

对照 `HEAD` 版本逐项取证：

| 断言 | 结果 |
|---|---|
A 数据行守恒（含表头行） | **15 = 15** ✓ |
B 旧行反引号 token 逐字零丢失 | **47/47 全部命中** ✓ |
C 结果态：恰好 1 个精确五列表头、无残留两列表头 | ✓ |
D 节与下一 `## ` 标题之间有空行 | ❌ **失败 → 已修** |

**发现并修复的缺陷**：改造时把原 `## 抽象层` 末尾的空行吃掉了，导致最后一段正文**直接贴着** `## 外部 API 安全合同（ADR-075 / ADR-082）` 标题。修复用 `temp/hb17_fix_blank.py`：锚点唯一性断言（count==1）+ 写前判「当前确实粘连且尚无空行」+ 写后断言（空行存在、字节增量恰 1）+ 读回逐字比对；`newline=''` 保持既有行尾风格。结果 `byte_delta=1`、`write_verified=true`、尺寸 29332 → 29333 B。

**修复后四项断言全绿**（A/B/C/D 均 True）——这也是修复的**取红对照**：修复前 D=False，修复后 D=True。

## 门禁实测（工具自报，`--check`）

| 指标 | HEAD（改造前） | 工作树（改造后 + 修空行） |
|---|---|---|
lines | 267 | 274 |
`entry_rows` | **136** | **136**（恒定 ✓） |
`fences` | 0 | 0 |
**error** | **50** | **38** |
warn | 30 | 28 |
line-too-long | 13 | **10** |
field-too-long | 37 | **28** |
anti-pattern | 28 | **26**（清单型 F 19→17） |
key-symbols 列 n | 74 | **88**（+14，即本节新增五列中的两列） |

`rules 其它项`（`entries-exceed` / `field-too-many-items` / `fence-forbidden` / `link-broken` / `layering-violation`）全程 0；`gate` 仍 FAIL（剩 38 err），符合预期。

## 手法

`Platform/ApprovalCode.cs`、`IRsiTrajectoryDataAccess.cs`、`RsiEventTimestamp.cs` 三行原本各含数百字长文（含 ⛔ 禁用清单与 `(TurnId, Type)` 索引证据）⇒ 表格「约束」列只留 `细节见后注` 指针，**原文逐字下沉为表后换行段落**（每行 ≤300），既满足「token 零丢失」又满足「行长 ≤300」。目录行（`Abstractions/`、`Core/`、`Platform/`）保留为行，其余列填 `—`。

## 新的工具环境事实（已存记忆）

- **`python` 在本机是 Microsoft Store 别名桩**（`C:\Users\hyfree\AppData\Local\Microsoft\WindowsApps\python.exe`），直接调用报 `Python was not found; run without arguments to install...`；
- **仓库内 `.venv\Scripts\python.exe` 不可用**（其 shebang 指向 `C:\Users\**huany**\...\Python312\python.exe`，属**另一个用户**创建，报 `No Python at ...`）；
- 可用调用 = **`py -3`**（本机为 **Python 3.14.7**）。
- `code_map_check.py` 的调用式是 `--check --paths <file>`（位置参数不被接受）。

## 交付卫生

- 白名单提交 2 文件：`Source/PuddingCore/code_map.md` + 本 changelog；提交前 `git diff --cached --name-only` 复核。
- 未触碰他人 WIP（`Docs/07_architecture/62ADR-062…`、`Source/PuddingAgent/PuddingAgent.csproj`、`TestScripts/flake-observations.txt` 均**未** stage）。
- 浏览器线（B 线）本轮未动，仍等用户裁定。

## 未做（诚实登记）

- PuddingCore 剩 5 节：判定算子与分类契约(11) / 配置 & 序列化(10) / Agent 定义(7) / 改进落点契约(6) / 子代理编排(4)；
- warn 侧 `anti-pattern` 26 条、`missing-fingerprint` 1、`header-repeat` 1 未处理（需逐条判定或跑 `--fingerprint` 取实测值）。
