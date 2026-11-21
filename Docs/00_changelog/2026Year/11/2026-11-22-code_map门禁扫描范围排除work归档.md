---
title: 2026-11-22 code_map 门禁扫描范围排除 .work 归档快照
author: 鲸鲸
date: 2026-11-22
last_reviewed: 2026-11-22
status: archived
description: code_map 门禁长期 error 774 是假象——563 条（72.7%）来自 .work/ 下被 gitignore 的仓库快照副本；把 .work 补进 §4 exclude 后，被扫文件 658→166、error 774→211，差集证明未掩盖任何活树错误；活树 211 条债仍未修。
categories: [docs, changelog]
tags: [code-map, gate, scope, gitignore, false-failure, triage]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-23-代码地图源指纹刷新工具refresh.md]
related_files: [Tools/Docs/code_map_check.py, Tools/Docs/code_map_check_selftest.py, Docs/10_conventions/code-map-规范-v2.md]
slug: changelog-codemap-gate-exclude-work-2026-11-22
draft: false
---

# 2026-11-22 code_map 门禁扫描范围排除 .work 归档快照

## 现象

`py -3 Tools/Docs/code_map_check.py` 长期 `gate: FAIL`，`error 774 / warn 1532`。
而 `2026-10-23` 的变更记录写着当时是 `error 0 / stale 0 / gate PASS`。一个无人能修的门禁等于没有门禁。

## 归因（机器可读报告 + 分组）

用 `--report` 出 JSON 后按路径分组：

| 范围 | 被扫 code_map 文件 | error |
|---|---|---|
| `.work/**`（gitignore 归档快照） | **492**（74.8%） | **563**（72.7%） |
| 活树（`Source/` `Tests/` `Samples/` 根） | 166 | 211 |

`.work/` 是 `.gitignore:461` 定义的统一临时/验证目录，里面躺着整份仓库的三个快照副本
（`asr-realtime/20261115/release-source`、`schedule-service/20261118/verified`、`voice-hold/20261116/source`）。
它们是历史冻结件，永远不会被修。

根因是**规范与现实的漂移**：§4 的 `exclude` 名单是手写清单（`bin/ obj/ node_modules/ .pudding/ temp/ dist/ dist-dev/ external/references/`），
`.work/` 从没被登记进去；而 `code_map_check.py` 用 `os.walk` 全盘枚举，只认那张清单。

## 改动（三处，各 1 行）

1. `Docs/10_conventions/code-map-规范-v2.md` §4 `exclude` 行 → 加 `` `.work/` ``
2. `Tools/Docs/code_map_check.py` `LIMITS["EXCLUDE_DIR_NAMES"]` → 加 `".work"`
3. `Tools/Docs/code_map_check_selftest.py` 的**独立枚举清单** → 加 `".work"`（它是第二实现，故意不复用检查器的常量）

## 验证

**单变量 before/after**（同命令、同解释器 `py -3` = 3.14.7、同 HEAD）：

| 指标 | before | after |
|---|---|---|
| files_scanned | 658 | **166** |
| error | 774 | **211** |
| warn | 1532 | **304** |
| `.work` 内被扫文件 | 492 | **0** |

**未掩盖证明**（对 `rule|file|line` 三元组做集合差）：
`only-in-before = 563`（全部 `.work/`）、**`only-in-after = 0`** ⇒ 活树一条错误都没被吞掉、也没有新增。

**自测**：
- `code_map_check_selftest.py` → **72 passed / 0 failed**（改前 71/1，唯一失败项正是 `independent=658 checker=166` —— 双实现一致性控制抓到了这次改动，说明它有效）
- `code_map_fingerprint_selftest.py` → **13 PASS / 0 FAIL**（该工具复用同一 `LIMITS`）

## 诚实边界

门禁**仍然是 FAIL**：活树 211 条错误未修，分布为
`line-too-long 117` / `field-too-long 70` / `fence-forbidden 18` / `link-broken 5` / `field-too-many-items 1`，
集中在 `Source/PuddingPlatform/code_map.md`(47)、根 `code_map.md`(23)、`Source/PuddingCore/code_map.md`(15)。
本次只修「扫描范围错了」，**没有减少一分活树债**。

另：`.work` 被排除后，`stale-fingerprint` 在快照里的必然失真也一并消失（那些快照的 `Source/**` 指纹声明本就与当前源码不同）。

## 第二处：`front_matter.py --paths` 不认 Windows 分隔符

**现象**：`py -3 Tools/Docs/front_matter.py --check --paths Docs\00_changelog\2026Year\11` 报
`ERROR: --paths 显式给出的前缀未匹配到任何 .md 文件`，而该目录确实有 md。

**根因**：`iter_md` 用 `rel.startswith(p)` 比较，`rel` 是正斜杠相对路径，而 `p` 来自 argv 是反斜杠
（本机 Windows 的自然写法）⇒ **逐字比较恒不命中**。fail-closed 本身是对的（没静默报绿），
但诊断把矛头指向“目录里没有 md”，而真因是分隔符。

**改动**：argparse 之后把 `--paths` 归一化为正斜杠（1 行 + 3 行注释）。

**三态验证**（同解释器、同 HEAD）：

| 输入 | 改前 | 改后 |
|---|---|---|
| `Docs\00_changelog\2026Year\11\2026-11-21-shell-auto` | `ERROR: 未匹配到任何 .md` | `扫描 1 个 md`（EXIT=1，该文件本就不合规） |
| 同上但用 `/` | `扫描 1 个 md` | `扫描 1 个 md`（行为一致） |
| `Docs\no_such_dir_zz` | `ERROR: 未匹配到任何 .md` | **仍 `ERROR`**（fail-closed 未被削弱） |

副产物：本文件自身的前置元数据就是用修后的命令验的 —— `扫描 1 个 md；不合规 0 个；EXIT=0`。

## 附带发现（未修的缺陷线索）

`file_patch` 对本次的 `.py` 文件走了 **`css-aware` 宽容匹配**（`LooksLikeCssSource` 判定误触发），
于是我用写错的锚点（行尾多了个 `:`）也被「宽容」匹配上并原样落盘，产生 `SyntaxError`。
是随后的 `ast.parse` 校验才发现的——**宽容匹配对任意文本文件生效，且不会因为「匹配策略不是 exact」而拒绝**。
建议后续把 CSS 宽容策略限制在确实的相关扩展名，或对非 `.css/.scss` 文件的容差匹配一律按 `exact` 之外视为需确认。
