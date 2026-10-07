---
title: 2026-10-07 Session上下文维护ADR与施工方案
author: hyfree
date: 2026-10-07
last_reviewed: 2026-10-07
status: archived
description: "交付ADR-095与Session上下文维护施工方案，明确软维护时机、硬保护、提交恢复、计量来源及分阶段验收。"
categories: [docs, changelog]
tags: [adr, session, compaction, latency]
related_docs: [Docs/07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md, Docs/12_features/首Token等待与上下文指示器修复方案-2026-10-07.md, Docs/14_reports/首Token等待与上下文指示器诊断-2026-10-07.md, Docs/07_architecture/README.md]
related_files: [code_map.md]
slug: changelog-2026-10-07-session-context-maintenance-adr
draft: false
---

# 2026-10-07 Session上下文维护ADR与施工方案

## 改动

- 新增[ADR-095](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md)：Session候选级准入、软/硬保护分离、统一应用边界、锁序与持久恢复、请求归因和UI事实。保留ADR-090的收益/无收益约束，有限扩展异步候选代次校验。
- 扩展[施工方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)：不可变合同、提交协议、独立组件S1–S4和S5接入任务、F1–F6文件范围、竞争/故障门禁及灰度回退。初版可选摘要在终态和前台租约释放后生成，活跃Turn轮边界评估及硬保护不变。
- 更新`Docs/07_architecture/README.md`和根`code_map.md`的设计入口；未新增真实组件或修改运行调用链，无需改子项目代码地图。

## 验证

- 源码复核Streaming/Buffered的Turn级warm-prefix标记、终态前TrimHistory、Agent前台/后台准入及提交锁序；与ADR-090/084和组件交付规程核对。
- 本次4份Docs文档Front Matter定向检查通过；5份改动文档/索引共231个本地链接与48个Front Matter文件引用检查通过；`git diff --check`通过。
- 全Docs扫描753份：既有失效目标25个、失效锚点0个，与修改前逐项一致；全库Front Matter仍只有既有报告`Docs/14_reports/skill-merge-probe-rerun-2026-10-06.md`缺3个字段。本次未新增门禁问题。
- 未运行构建或代码测试：只修改设计文档与索引，未修改产品源码、前端版本、配置或数据库。

## 未完成与关联

方案尚未实施、部署或产品验收。393,216输出预算来源、请求计量误差、真实UI消费路径与协议checkpoint字段列为实施调查项；不承诺第二条消息在硬超限时无等待。已有他方WIP不随本次提交。

[运行证据](../../../14_reports/首Token等待与上下文指示器诊断-2026-10-07.md) · [ADR](../../../07_architecture/109ADR-095会话上下文维护与首增量延迟治理ADR.md) · [施工方案](../../../12_features/首Token等待与上下文指示器修复方案-2026-10-07.md)。
