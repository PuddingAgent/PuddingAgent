---
title: 2026-11-24 code_map warn 现状与 header-repeat 豁免取证
author: hyfree
date: 2026-11-24
last_reviewed: 2026-11-24
status: active
description: "对 code_map_check 当前 14 条 warn 给出构成与归口：header-repeat 3 条经逐表实测为「大工程按子系统分节」的合理结构（32 张表全部有数据行、无空表、各属不同小节），anti-pattern 11 条沿用 2026-11-22 归因报告；并给出该告警变成真缺陷时的判据与可复现脚本。"
categories: [docs, reports]
tags: [code-map, lint, header-repeat, exemption, evidence]
related_docs: [Docs/10_conventions/code-map-规范-v2.md, Docs/14_reports/2026-11-22-code_map反模式命中归因.md, Docs/14_reports/2026-10-23-code_map规范v2条款机器覆盖核对.md]
related_files: [Tools/Docs/code_map_check.py, code_map.md, Source/PuddingCore/code_map.md, Source/PuddingPlatform/code_map.md, Source/PuddingRuntime/code_map.md]
slug: reports-2026-11-24-code_map-warn现状与header-repeat豁免
draft: false
---

# 2026-11-24 code_map warn 现状与 header-repeat 豁免取证

## 1. 结论（先给）

当前门禁 `error 0 / warn 14 / gate PASS`。**14 条 warn 全部是已取证的合理豁免，没有真缺陷**：

| 类别 | 数量 | 归口 |
|---|---|---|
| `header-repeat` | 3 | 本文 §4（大工程按子系统分节，32 张表全部有数据行） |
| `anti-pattern` 清单型 F | 10 | `2026-11-22-code_map反模式命中归因.md`（词表裁定 Q1） |
| `anti-pattern` 路线图「未来」 | 1 | 同上 |

本文只做一件事：把「`header-repeat` 是误报而不是待办」这句话变成**可复核的取证**，避免下一个会话重查一遍。

## 2. `header-repeat` 的判据是纯计数

`Tools/Docs/code_map_check.py`：

```python
LIMITS = { "MAX_TABLE_HEADER_REPEAT": 8, ... }        # L53

for key, cnt in sorted(header_counts.items()):        # L749
    if cnt > LIMITS["MAX_TABLE_HEADER_REPEAT"]:       # L751
        violations.append(_vio("header-repeat", rel, 0,
                               "header=%s count=%d limit=%d" % ...))
```

判据 = **同一文件内同一表头字符串出现次数 > 8**。

它**不看**这 8 张以上的表各自属于哪个小节、有没有内容、是否重复登记同一批文件。所以：文件越大、子系统越多，越必然命中 —— 这与「code_map 该不该分节」是两件事。

## 3. 三处逐表实测（2026-11-24）

对三个命中文件逐表统计「该表头出现位置 → 所属 `##` 小节 → 该表数据行数」：

| 文件 | 表头次数 | 数据行范围 | 空表 | 分节 |
|---|---|---|---|---|
| `Source/PuddingCore/code_map.md` | 13 | 1 ~ 25 | **0** | 13 个互不相同的小节 |
| `Source/PuddingPlatform/code_map.md` | 10 | 2 ~ 18 | **0** | 10 个互不相同的小节 |
| `Source/PuddingRuntime/code_map.md` | 9 | 11 ~ 102 | **0** | 9 个互不相同的小节 |

三个文件合计 **32 张表，全部有实际数据行，没有一张空表**，且每张表都挂在不同的小节标题下（例：`## 模型（Models/）`、`## LLM 网关（Core/）`、`## 内置工具（Tools/BuiltIns）`）。

最小的一张表也有 1 行数据（`PuddingCore` 的 `## 推理紧凑编解码（Services/）` 与 `## 存储管理契约（Storage/）`）—— 这是该子系统只有一个值得登记的对象，不是空壳。

## 4. 什么情况下它才是真缺陷

判据要能取红，所以必须说清反例：

1. **空表**：某张表只有表头 + 分隔行，`data_rows == 0` ⇒ 是残留骨架，该删。
2. **同一小节内重复**：两张相同表头出现在**同一** `##` 小节里 ⇒ 结构冗余，该合并。
3. **重复登记**：「文件 / 目录」列出现同一路径 ≥2 次 ⇒ 该去重（与表头无关，但同样表现为表数量虚高）。

本次实测三项均为 0。任一项出现，这条豁免即失效，应按上述方式修，而不是放宽 `MAX_TABLE_HEADER_REPEAT`。

## 5. 复现

```python
import io
HDR = "| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |"
for p in ("Source/PuddingCore/code_map.md",
          "Source/PuddingPlatform/code_map.md",
          "Source/PuddingRuntime/code_map.md"):
    t = io.open(p, encoding="utf-8").read().split("\n")
    idx = [i for i, l in enumerate(t) if l.strip().startswith("| 文件 / 目录 |")]
    for n, i in enumerate(idx):
        end = idx[n + 1] if n + 1 < len(idx) else len(t)
        data = 0
        for j in range(i + 2, end):
            s = t[j].strip()
            if s.startswith("|"):
                data += 1
            else:
                break
        sect = next((t[k][:70] for k in range(i, -1, -1) if t[k].startswith("## ")), "")
        print(p, "@%d" % (i + 1), "data_rows=%d" % data, sect)
```

或直接看门禁报告：`py -3 Tools/Docs/code_map_check.py --check --report temp/cm_rep.json`，然后取 `violations[]` 中 `rule == "header-repeat"` 的条目。

## 6. 未做的事（以及为什么）

- **不放宽 `MAX_TABLE_HEADER_REPEAT`**：那是改判据面，属于规范变更，需要单独裁定；而且放宽后「同一小节重复表头」这类真问题也会被一起放过。
- **不为消 warn 合并表格**：`PuddingRuntime` 的 9 张表最大一张有 102 行，合并成一张会超过 `MAX_ENTRY_ROWS = 400` 的可读边界，也会让「按子系统定位」失效 —— 那是用可读性换一个数字。
- **不改这 3 个 code_map**：本次是只读取证，未改动任何 `code_map.md`。
