---
title: 2026-10-08 实测 -AcceptRetries 不能绕过未验证项（验收门完整性再证）
author: hyfree
date: 2026-10-08
last_reviewed: 2026-10-08
description: "验收脚本文档提到 `-AcceptRetries`（*Accepting retried suite results is the ACCEPTOR's decision*）⇒ 需确认它**不会**成为"洗白未验证项"的后门。**实测**：对**全 `not-run`** 的 facts 执行 `-SkipTemplate -Conclude -AcceptRetries` ⇒ **仍 `exit=2`**、输出 `NOT allowed: external-lifecycle-verified` ⇒ 该开关**只关乎套件重试**，**清单项仍逐项校验**。该结论已补进交接单 §10（外部看到该开关时不必怀疑它会降低门槛）。"
status: archived
categories: [docs, changelog]
tags: [b7, acceptance, verification]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-08-验收门三处反向验证.md]
related_files: [TestScripts/external-acceptance.ps1]
slug: changelog-acceptretries-verified
draft: false
---

# 2026-10-08 `-AcceptRetries` 实测

| 输入 | 结果 |
|---|---|
| 全 `not-run` facts + `-AcceptRetries` | **`exit=2`** · `NOT allowed: external-lifecycle-verified` ✓ |

**⇒ 该开关不降低门槛**（只关乎套件重试）—— **验收门完整性第 4 次证伪**（前三次：facts 全 not-run / manifest 缺失 / 结果值非法）。

## 诚实边界

- 仅验证**拒绝路径**；**通过路径需真机**（外部）；**未真机验证**。