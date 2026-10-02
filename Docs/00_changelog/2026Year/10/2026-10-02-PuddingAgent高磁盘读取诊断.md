---
title: 2026-10-02 PuddingAgent 高磁盘读取诊断
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 完成进程采样和只读查询诊断，定位全量代码索引逐符号关系清理与长会话边界扫描的读取放大。
categories: [docs, changelog]
tags: [diagnostics, sqlite, disk-io]
related_docs: [Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md, Docs/08_how_debuge/05-常见症状.md]
related_files: []
slug: changelog-puddingagent-high-disk-read-2026-10-02
draft: false
---

# 2026-10-02 PuddingAgent 高磁盘读取诊断

## 改动

- 新增 `Docs/14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md`：现场、调用栈、只读查询计划与修复优先级。
- 更新 `Docs/08_how_debuge/05-常见症状.md`：添加高磁盘读取分诊步骤。
- 未修改业务代码、运行时配置或数据库；没有新增/移动/删除关键代码文件或改变链路，code_map 无受影响条目。

## 验证

用户启动实例后取得六次进程采样、四次托管调用栈；25.96 秒进程读取增长约 26.7 GB。用户补充发送消息后平台库近 1 GB 读取截图；日志与后两次栈证实 164 万事件的旧会话从 cursor=0 开始 SSE 回放，并执行 GetBoundsAsync。只读探针对比逐符号 OR 谓词与 Source/Target 索引查询，以及三个长会话的 MIN/MAX 与首尾查询。完整数字和限制见[报告](../../../14_reports/2026-10-02-PuddingAgent高磁盘读取诊断.md)。

`python Tools/Docs/front_matter.py --check` 扫描 649 篇、0 不合规，通过；`python Tools/Docs/check_md_links.py` 扫描 649 篇，25 个既有失效目标、0 失效锚点，返回 1。失效来源均在既有 architecture/tasks/qa 文档，本任务三篇文档不在失败列表中；本任务未修复其他历史文档链接。

## 未完成与影响

未实施/部署性能修复；WPR 因权限不足未取得文件级 ETW。未运行业务测试或构建，因为本任务只有诊断文档变更。生产数据没有由诊断探针写入。撤销此提交即可撤销本任务文档。
