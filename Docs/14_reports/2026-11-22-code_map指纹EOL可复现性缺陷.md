---
title: 2026-11-22 code_map 源指纹的 EOL 可复现性缺陷
author: hyfree
date: 2026-11-22
last_reviewed: 2026-11-22
status: active
description: "code_map 源指纹按工作树字节哈希，而 core.autocrlf=true 让 git checkout 把索引里的 LF 写成 CRLF：同 commit 在 CI（autocrlf=false）与本地会算出不同指纹，任何 checkout 都会让 warn 无端 +1。实测本仓 1970 个文件索引 LF/工作树 CRLF、289 个混合、涉及 125 个项目目录。含两种修法与未实施的理由。"
categories: [docs, reports]
tags: [code-map, fingerprint, reproducibility, eol, evidence]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/14_reports/2026-11-22-L2覆盖盘点.md]
related_files: [Tools/Docs/code_map_check.py, Tools/Docs/code_map_fingerprint.py, code_map.md]
slug: reports-2026-11-22-fingerprint-eol-reproducibility
draft: false
---

# code_map 源指纹的 EOL 可复现性缺陷

## 1. 现象

`git checkout -- Tools/Docs/code_map_check.py` 之后（**逻辑内容未变，同一提交**），门禁立刻变红：

```
fingerprint_check: verified=400 mismatched=1
stale-fingerprint  code_map.md  glob=Tools/**  declared=153f3ef4e171  computed=1fd1ea7630b2
warn 52 → 53
```

`tool_sha256` 也从 `72308e38…` 变成 `45874376…`。

**回退一次代码，门禁就多一条 warn** —— 这不该发生。

## 2. 根因

```
$ git ls-files --eol Tools/Docs/code_map_check.py
i/lf    w/crlf  attr/text=auto
```

- 仓库索引存 **LF**
- 本仓 `core.autocrlf=true` ⇒ `git checkout` 把它写成 **CRLF**
- `code_map_check` 的子指纹是**按工作树字节**哈希的

⇒ 同一逻辑内容，只要工作树 EOL 不同，指纹就不同。

## 3. 影响面（本仓实测）

```
$ git ls-files --eol  →  8583 个受跟踪文件

i/lf -> w/lf        6224
i/lf -> w/crlf      1970      ← 索引 LF、工作树 CRLF
i/lf -> w/mixed      289      ← 工作树同一文件内 EOL 混杂
i/-text -> w/-text    62
i/none -> w/none      34
```

按目录聚合：**407 个项目目录里 125 个**含此类文件（最多的：`PuddingPlatformAdmin` 355/890、
`PuddingPlatform` 206/714、`PuddingCore` 188/444）。

### 3.1 对 CI 的后果

CI 通常 `core.autocrlf=false` ⇒ 工作树是 LF ⇒ **那 1970 + 289 个文件的字节与本机不同**
⇒ 落在它们作用域内的每一个源指纹都会算出不同的值。

根 `code_map.md` 声明了 `Source/**`、`Tests/**`、`Tools/**`、`TestScripts/**` 四条全仓级 glob，
几乎必然全部失配 ⇒ **CI 上会出现「整仓 stale」**，而本地 `warn 52`。

### 3.2 已直接证实的因果链

| 状态 | `Tools/**` 实测值 |
|---|---|
| 工作树 LF | `153f3ef4e171` |
| 工作树 CRLF（checkout 后） | `1fd1ea7630b2` → 刷新后 `20f7238eb40e` |

同一提交 `6f8bd52`，只有 EOL 不同。这已经足够证明缺陷，不需要再构造实验。

## 4. 两种修法（均未实施，需裁定）

### 4.1 哈希前归一化 EOL（推荐）

在聚合前把每个文件内容里的 `\r\n` 归一为 `\n`，使指纹只反映**内容**。

- 优点：根治；CI 与本地一致；`git checkout` 不再影响指纹
- 代价：**全部既有指纹失效**，需一次全量 `--refresh --apply`（186 个 code_map、401 条 glob）
- 风险：刷新一次全仓指纹，需与「他人 WIP 分支」协调，否则会互相冲掉

### 4.2 把检查器自身排除出 `Tools/**`

只解决「改工具就让指纹变」这一种情形，不解决整个 EOL 问题。

- 优点：改动最小
- 缺点：`Source/**` 下的 1970 个文件仍然 EOL 敏感，CI 与本地照旧不一致

## 5. 为什么本轮没做

1. 两种修法都要改指纹**语义**，属于规范级变更；影响 401 条 glob、186 个文件的声明值
2. 4.1 需要一次全量刷新，而工作树里还有**他人未提交的改动**（`Tools/CodexPlugins/**`、根 `code_map.md` 1 行）
   —— 全量刷新会把他们的行一起改写进我的提交
3. 按纪律「不确定时先说明假设」：本文只给证据与方案，不做未经授权的语义变更

## 6. 立即可以做的缓解（不需要改语义）

任何一次 `git checkout` / `git restore` 触及指纹作用域内的文件后，**必须重跑门禁**：

- 若只有 `stale-fingerprint` 且计数为 1–2，先确认是否 EOL 引起（`git ls-files --eol <file>`）
- 确认是 EOL 后，用 `code_map_fingerprint.py --refresh --apply` 刷新，**不要**去改业务代码找原因

本轮就是这样处理的（提交 `2fc10fe`）。
