---
title: 2026-10-27 warm-prefix 触发回收核对
author: hyfree
date: 2026-10-27
last_reviewed: 2026-10-27
status: archived
description: 只读核对指定压缩记录，确认真实回收、窗口分母及前缀缓存恢复，并记录诊断展示不足。
categories: [docs, changelog]
tags: [compaction, diagnostics]
related_docs: [Docs/14_reports/2026-10-27-warm-prefix触发与回收诊断.md]
related_files: []
slug: changelog-compaction-trigger-reclaim-168198
draft: false
---

# 2026-10-27 warm-prefix 触发回收核对

- 新增 [诊断报告](../../../14_reports/2026-10-27-warm-prefix触发与回收诊断.md)，对应 `warm-prefix:16819807b91347d69a62703d5dc5a95d:r1:1beaa62e94464fe2bfd3889bcf5d9387`。
- 核对冻结现场、持久生命周期事件、边界日志和 Provider usage：提交时回收 29.7%，相邻实测请求减少 32.3%；下一请求缓存恢复 99.95%。截图 47.4% 使用有效窗口，折算注册窗口约 44.3%，已低于 45% 目标。
- 未运行代码测试：本次只读诊断，不改业务代码、配置或会话，不部署、不重启、不调用模型。没有代码概念或调用链变化，因此不改代码地图。
- 文档验证：两份新增文档的 Front Matter 校验通过；全 Docs 扫描 1,804 份，仍有 26 个历史失效目标、0 个失效锚点，本次新增文档没有失效链接。
- 未完成项：事件触发原因与轮次标签、UI 双分母解释及准备期增长预留，仅记录问题与建议，未实施修复。
