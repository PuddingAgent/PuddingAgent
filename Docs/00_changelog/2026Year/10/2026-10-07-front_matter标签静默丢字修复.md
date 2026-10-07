---
title: "2026-10-07 · 修复 `front_matter.py` 标签静默丢字（CJK 串长度 ≡1 mod 6）"
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: Docs 前置元数据工具在生成 tags 时会静默吞掉汉字串的末字（如 索引新鲜度信号 → 索引新鲜度信），Docs 下 721 个 md 中 127 个命中该形态。
categories: [docs, changelog]
tags: [docs, front-matter, tags, silent-data-loss, cjk, tools]
related_docs: [Docs/10_conventions]
related_files: [Tools/Docs/front_matter.py]
slug: changelog-2026-10-07-front-matter-tag-silent-loss
draft: false
---

# 2026-10-07 · 修复 `front_matter.py` 标签静默丢字

## 背景 / 动机

发现路径**不是代码审阅，而是"同一文件里两个字段自相矛盾"**：

`Docs/00_changelog/2026Year/10/2026-10-03-code_symbol_search索引新鲜度信号.md` 中

```
title: "2026-10-03 · `code_symbol_search` 增加索引新鲜度（index_freshness）诚实信号"   ← 完整
slug:  changelog-2026-10-03-code-symbol-search索引新鲜度信号                          ← 完整
tags:  [code, symbol, 索引新鲜度信]                                                  ← 丢「号」
```

三者同源，`tags` 却少一字 ⇒ 该字段的生成是有损变换。

## 根因（单行，已定位）

`Tools/Docs/front_matter.py` 原第 225 行：

```python
cjk = re.findall(r'[\u4e00-\u9fff]{2,6}', stem)
```

`{2,6}` 是**贪婪但非覆盖**的量词：对一段连续汉字，引擎从前往后每 6 字切一块，
**余尾只剩 1 字时不满足下限 2 ⇒ 无匹配 ⇒ 该字符被静默丢弃**。

**精确规则**：汉字串长度 `L`，当 `L ≡ 1 (mod 6)` 时**恰好丢 1 个尾字**。

| 文件名片段 | L | L mod 6 | 实测 tags | 结果 |
|---|---|---|---|---|
`索引新鲜度信号` | 7 | 1 | `索引新鲜度信` | **丢「号」** |
`迁出的变更记录` | 7 | 1 | `迁出的变更记` | **丢「录」** |
`高磁盘读取诊断` | 7 | 1 | `高磁盘读取诊` | **丢「断」** |
`瘦身与日志归档规范落地` | 11 | 5 | `瘦身与日志归` + `档规范落地` | 无损（切两段） |
`传输用量只读出口` | 8 | 2 | `传输用量只读` + `出口` | 无损 |

## 影响面（实测，非估计）

对 `Docs/**/*.md` 全量跑 `tags_for()`（721 个文件）：

```
files=721  lossy=127          # 127 = 109(会丢字) + 21(单字串原本不成 tag) - 3(两者兼有)
predicted_CHOP_files=109      # L>=7 且 L≡1 (mod 6)：token 尾部被截断
```

⇒ **109 / 721 ≈ 15% 的文档**其 tags 若由该工具生成，会**永久写错一个词**（且 `--check` **不会报错**，
因为它只校验"tags 非空"，不校验内容）——典型的"未知被报成正常"。

## 改动

`Tools/Docs/front_matter.py`（**仅此一处行为变更**）：

1. 新增 `cjk_chunks(run, size=6)`：仍按 6 字切块（**保证非丢字场景输出逐字不变**），
   仅当最后一块只剩 1 字时**并入前一块**（最多 7 字），从而不丢字。
2. `tags_for` 改为先 `re.findall(r'[\u4e00-\u9fff]+', stem)` 取整段，再逐段切块；
   **长度为 1 的独立汉字串仍不产生 tag**（保留原有 ≥2 下限 —— 与"截断 token 尾部"是两回事）。

## 验证（差分门禁，可复跑）

证据目录 `temp/`（已 gitignore）：`fm_tags_probe.py`（导出全量 tags）、`fm_tags_compare.py`（差分断言）。

```
git hash-object Tools/Docs/front_matter.py
  b05e6685c60d973be09a01703accd7903270509a   # 改动前
  16fb17aba45a9fc75ab0e2e22b861d11fb327fcb   # 改动后
python temp\fm_tags_probe.py . temp\fm-tags-before.txt
python temp\fm_tags_probe.py . temp\fm-tags-after2.txt
python temp\fm_tags_compare.py temp\fm-tags-before.txt temp\fm-tags-after2.txt
```

| 判据 | 期望 | 实测 |
|---|---|---|
改动文件数 | 只应有"会丢字"的那批 | `changed_total=109`，`changed_and_CHOP=109`，**`changed_and_NOT_CHOP=0`** |
**零副作用** | 非丢字文件的 tags 逐字节不变 | `non-CHOP changed files` 列表**为空** |
丢字清零 | 0 | `RESIDUAL_loss_before=109 → RESIDUAL_loss_after=0` |
恢复完整 | 109/109 | `CHOP_files_now_complete=109 / 109` |
CLI 未被改坏 | 可运行 | `CHECK：扫描 721 个 md；不合规 1 个`（既有，与本改无关） |

样本（before → after）：`索引…`/`迁出…`/`高磁盘…` 三个用例均从"少一字"变为完整。

## 诚实标注（本轮自查出的自身缺陷）

**第一版修复是错的，且是我自己写的门禁抓出来的。**

```python
chunks[-2] += chunks.pop()   # ❌
```

RHS 的 `pop()` **先缩短了列表**，随后 `STORE_SUBSCR` 仍用索引 `-2` ⇒ **指向错位元素**，
产生"重复 + 丢字"：`[A,B,C]` → `[B+C, B]`（A 丢失、B 重复）。
第一版门禁结果 **`RESIDUAL_loss_after=127`（比修复前的 109 更差）**，被判红。
改为两步 `tail = chunks.pop(); chunks[-1] += tail` 后方转绿。**已在源码注释中留下该陷阱说明。**

⇒ 教训：**"修复本身也要过同一条门禁"**；若只做"改动前后 diff 看起来对"的人工检查，这个 bug 会直接进库。

## 未做 / 待办

- 已存在的 109 个文件（含本会话多份 changelog）其 `tags` 里**已写入的截断值不会被本改动回填** ——
  需另跑一次 `python Tools/Docs/front_matter.py --fix --force --paths Docs`（会顺带规范其它字段），
  属独立动作，未在本刀内执行。
- `--check` 目前**不校验 tags 与文件名的一致性**，故这类丢字它能通过；是否新增该校验待定。
- 仍有 1 个不合规文件 `Docs/14_reports/skill-merge-probe-rerun-2026-10-06.md`（缺 author/date/last_reviewed），
  系既有状态，与本改无关。
