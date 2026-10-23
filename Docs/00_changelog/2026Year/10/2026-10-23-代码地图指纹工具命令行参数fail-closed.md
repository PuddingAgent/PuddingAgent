---
title: 2026-10-23 代码地图指纹工具命令行参数 fail-closed（补 --help 与未知参数拒绝，附 10 条契约自测）
author: hyfree
date: 2026-10-23
last_reviewed: 2026-10-23
description: "为看用法执行 `code_map_fingerprint.py --help`，该版本没有 argparse、也不认 --help，于是静默按 dry-run 跑了起来（打印 MAX_LINE_LEN = 300 | cache entries = 254）。它当时只写了 temp 缓存、0 份 code_map 未受影响，但同一条命令上顺手带一个 --apply 就会真写盘——这是一类「看一眼用法变成执行」的仪器缺陷。本轮给该工具加参数白名单：--apply / --fresh / -h(--help) 之外一律 exit 2 并把未知参数写进 stderr，--help 只打印说明、不哈希、不写缓存（提前 SystemExit，早于 load_cache）。同时新增 Tools/Docs/code_map_fingerprint_selftest.py 十条契约断言：--help/-h 退出 0 且不出现计划输出、未知参数退出 2 且被点名、--apply + 未知参数依旧 exit 2 且 stdout 为空、help/非法参数路径不覆写 temp/cm_fp_cache.json。变异取红：删掉 main() 首行 parse_argv 调用后 8 条 FAIL（其中 --help/-h 的 exit 0 仍绿，因为变异版正是静默跑完退出 0 —— 抓住它的是「不执行计划」与「缓存未被覆写」两条断言），复原后 sha256 3d98802d 逐位相同、10 PASS / 0 FAIL。根 code_map.md 的 Tools/** 源指纹随之刷新为 377b220beeb9（该行用 HEAD 原文 + hash-object + update-index --cacheinfo 单独入索引，避免把工作区里他人未提交的 ToolExposure 条目一并提交）。门禁 error 0 / warn 85 / gate PASS，stale 3 全部来自他人未提交 WIP；code_map_check.py sha256 逐位不变 bd06254d。"
status: archived
categories: [docs, changelog]
tags: [tooling, cli, fail-closed, selftest, mutation-testing, code_map, rsi]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/00_changelog/2026Year/10/2026-10-23-代码地图逐文件免检旁路补断言.md]
related_files: [Tools/Docs/code_map_fingerprint.py, Tools/Docs/code_map_fingerprint_selftest.py, code_map.md]
slug: changelog-2026-10-23-code-map-fingerprint-cli-fail-closed
draft: false
---

# 代码地图指纹工具命令行参数 fail-closed

**日期**：2026-10-23 · **范围**：`Tools/Docs/code_map_fingerprint.py`（+32 行）、新增 `Tools/Docs/code_map_fingerprint_selftest.py`、根 `code_map.md` 的 `Tools/**` 源指纹；`code_map_check.py` **未改**（sha256 逐位相同 `bd06254d…`）

## 问题：一次「看一眼用法」变成了执行

`code_map_fingerprint.py` 从落地起就没有 argparse，标志位靠 `'--apply' in sys.argv` 取。后果是**任何**参数都被当成合法参数：我为了看用法执行

```
py -3 Tools\Docs\code_map_fingerprint.py --help
```

它没有打印帮助，而是**按 dry-run 跑了起来**（输出 `MAX_LINE_LEN = 300 | cache entries = 254`，末尾 `--- 已写回 0 份，失败 0 份`）。

实测损失为零，但归因必须诚实：

- 48 份 `code_map.md` 全部已声明指纹 ⇒ 待补清单为空 ⇒ **0 份被写**（`git status` 复核无新增未跟踪文件）；
- 唯一的副作用是 `temp/cm_fp_cache.json` 被覆写（缓存，已 gitignore）；
- 真正的风险在同一行命令里：**只要那个标志位是 `--apply`**，这次「看用法」就会写盘。破坏性开关不该由「参数拼写是否恰好命中」来决定。

## 改了什么

1. **参数白名单**：`KNOWN_FLAGS = ('--apply', '--fresh')` + `HELP_FLAGS = ('-h', '--help')`，其余参数 `exit 2` 并把未知参数写进 stderr（不是只报个数量）。
2. **`--help` 是真帮助**：`parse_argv` 打印模块说明 + 合法参数表后 `SystemExit(0)`，**早于** `load_cache()` —— 所以不哈希、不读报告、不写缓存。
3. **保留原调用形态**：没有改成 argparse。`--apply` 的语义（默认 dry-run，显式才写）与历史一致，改动只是「多一道闸」；函数 docstring 里写清了为什么不用 argparse（历史标志位形态 + 需要 fail-closed 而非 argparse 的宽松报错）。

为什么值得单独立项：这类缺陷没有任何东西会红 —— 工具照常工作，只是**在错误的时候工作**。

## 自测：十条契约断言，逐条能取红

新增 `Tools/Docs/code_map_fingerprint_selftest.py`（只读，不跑全量哈希）：

| # | 断言 |
|---|------|
| 1–3 | `--help` exit 0 · 打印合法参数表 · **不出现计划输出**（无 `[stage=` / `待写入`） |
| 4 | `-h` exit 0 |
| 5–7 | 未知参数 exit 2 · stderr 点名 `--bogus` · 不出现计划输出 |
| 8–9 | `--apply --bogus` 仍 exit 2 · stdout 为空（破坏性开关不得因拼错参数而生效） |
| 10 | help / 非法参数路径**不覆写** `temp/cm_fp_cache.json`（size + mtime_ns 不变） |

```
10 PASS / 0 FAIL
```

## 变异取红

删掉 `main()` 首行的 `parse_argv(sys.argv[1:])`（其余不动）后：

```
[mutated] rc=1   8 FAIL / 2 PASS
   PASS  --help exits 0            <- 变异版正是「静默跑完并退出 0」
   PASS  -h exits 0                <- 同上
   FAIL  --help prints accepted flags
   FAIL  --help runs no plan (no stage line)
   FAIL  unknown flag exits 2   <- rc=0
   FAIL  --apply + unknown flag still exits 2   <- rc=0
   FAIL  help/invalid paths leave the hash cache untouched
         before=(17856, 1792713986845479400) after=(17856, 1792714010200608400)
restored sha256=3d98802d… (equal=True)
[restored] rc=0   10 PASS / 0 FAIL
```

两个要点：

- 变异版里 `--help` / `-h` 的「exit 0」**仍然绿** —— 因为原始缺陷的现象就是「静默执行后正常退出」。真正抓住它的是第 3 条（不执行计划）和第 10 条（缓存未被覆写，mtime 从 `…986845479400` 变到 `…010200608400`）。**只看退出码的断言抓不到这个缺陷**。
- 变异脚本走硬备份 + `try/finally` + 复原后验 sha256（`3d98802d…` 逐位相同）。上一轮曾因 shell 超时把检查器停在变异态，这次不再重犯。

## 根 code_map 的 Tools/** 指纹

本改动落在 `Tools/**` 作用域内，故根 `code_map.md` 第 3 行的 `Tools/**` 源指纹必须随之刷新：`5f7daa2009f8` → `377b220beeb9`（28 个文件）。

难点是工作区里 `code_map.md` 同时含**他人未提交**的 ToolExposure 条目（§3 审批行与 §4 `ToolExposurePlanner` 行）。若直接 `git add code_map.md`，会把别人的 WIP 一起提交。做法是：

1. 工作区同步改这一行（保证本地文件自洽，门禁读的是工作区）；
2. 索引侧取 `git show HEAD:code_map.md` 原文 → 只替换这一处指纹 → `git hash-object -w` → `git update-index --cacheinfo`。

复核两条 diff 反向印证：

- `git diff --cached -- code_map.md` = **恰好 1 行**（`-5f7daa2009f8` / `+377b220beeb9`）；
- `git diff -- code_map.md`（工作区 vs 索引）= **只剩他人那 2 个 hunk**，没有第 3 行 ⇒ 索引与工作区在第 3 行上一致，不会在下次提交时被改回旧值。

## 验证结果

- 契约自测：`10 PASS / 0 FAIL`（变异态 `8 FAIL`，复原后 sha256 逐位相同再绿）。
- 门禁 `code_map_check.py --check`：`error 0 / warn 85 / gate PASS`，`files_scanned 48`、`fingerprint_check verified=145 mismatched=3`；`stale 3` 全部是**他人未提交 WIP** 的 `Source/PuddingCore/**`、`Source/PuddingRuntime/**`、`Source/**`，工具自身 `Tools/**` 已 verified。
- `code_map_check.py` sha256 逐位不变：`bd06254db6b53dc09445f8cd7e0672f9ed5406178f3df538f2ea4d15d420d16d`。

## 记一个外域观察（不是我的改动）

中途一次门禁是 `error 1`：`field-too-long`，`Source/PuddingCore/code_map.md` 第 133 行 `用途` 列 168 字符（限 80）。约 1 分钟后再跑已恢复 `error 0` —— 是并发作者正在改自己的条目。**不去动别人的半成品行**是对的：那属于他们的原子改动，我去“修”只会制造交叉覆盖。

## 遗留

- 工具仍然**只补缺失指纹、不刷新过期指纹**（`declared_fingerprint` 为空的才处理）。本轮之前我把它误当成“刷新工具”，这是判断错误；「刷新过期指纹」目前只能靠人改或另写工具，规范未要求，暂不实施。
- `stale 3` 待并发作者的 WIP 落地并合并后再刷新（刷新它们会把这三位 WIP 的文件集合做成假指纹，随后必然再次失配）。

## 操作事故与补救：并发提交差点让我回退别人 15 个文件（本轮真实发生）

这不是假设，是这一轮踩到的：

- **事实**：00:07:31 `git add` 我的三个文件时，索引基准是 `58ee620`；00:07:37 `git commit` 之前，并发作者提交了 `a00e3e5`（工具曝光预算）。`git commit` 只提交**索引内容**，于是我的提交（`0c49d5c`）相对新父提交变成了「把他人的 15 个文件回退成改动前形态」——19 files changed、**769 deletions**，连他们新建的 `ToolExposureBudgetTests.cs` 与 changelog 都被记成 `delete mode`。
- **取证**：`git add` 之后 `git diff --cached --name-only` 确实只有 4 个文件（说明我没有误 add 别人的东西）；`git log --oneline -6` 显示父提交已经是 `a00e3e5`，`git reflog` 里它落在 `HEAD@{1}` —— 所以问题是**索引与父提交不同步**，不是暂存范围错。
- **没有外泄**：`git push` 在同一条命令链里紧接着失败（`fatal: The current branch master has no upstream branch`），那次错误提交从未离开本机。
- **补救**：`git reset --mixed a00e3e5`（只移动 master 与索引，**不动工作区**）→ 重新 `git add` 三个文件 → `code_map.md` 用 `a00e3e5` 的原文替换一行、`hash-object -w` + `update-index --cacheinfo` → 重新提交。补救后的 `diff --cached --stat` 与首次逐字相同（4 files / 237 insertions / 1 deletion），他人工作区零改动（`git status` 仍是他们自己的 14 M + 2 ??）。
- **教训（写进流程）**：
  1. **`git add` 与 `git commit` 之间有窗口**。多 Agent 共用工作区时，commit 前必须重新核对「父提交还是不是 add 时的那个」，或干脆用 `git commit -- <paths>`，让 git 直接从工作区内容重建这几个路径。
  2. 但 `git commit -- code_map.md` 在这里**不能**用：工作区那份还含并发作者未提交的行，取工作区内容就会把别人的 WIP 一起带进来。含他人 WIP 的共享文件只能走「ref 原文 + 单行替换 + `update-index`」这条窄路。
