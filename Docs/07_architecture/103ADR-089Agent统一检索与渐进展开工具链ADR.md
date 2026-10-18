---
title: ADR-089：Agent 统一检索与渐进展开工具链
author: hyfree
date: 2026-09-13
last_reviewed: 2026-10-18
status: proposed
description: "统一代码与文件检索的架构决策：会话多项目、精确 grep、代码地图、向量证据、组件边界及分阶段门禁。"
categories: [docs, architecture]
tags: [adr-089, retrieval, grep, code-map, vector]
related_docs: [Docs/12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md, Docs/12_features/增强索引分阶段施工计划-2026-10-18.md, Docs/14_reports/2026-10-18-增强索引设计与实现审阅.md, Docs/10_conventions/组件化交付规程.md, Docs/90_archive/adr_089_legacy_20261018/103ADR-089Agent统一检索与渐进展开工具链ADR.md]
related_files: [Source/PuddingCodeIndex/PuddingCodeIndex.csproj, Source/PuddingCodeIntelligence/PuddingCodeIntelligence.csproj, Source/PuddingFullTextIndex/PuddingFullTextIndex.csproj]
slug: architecture-103adr-089agent统一检索与渐进展开工具链adr
draft: false
---

# ADR-089：Agent 统一检索与渐进展开工具链

状态：**Proposed，正式设计基线，按阶段实施与验收**。本 ADR 定义决策；工具字段、模型和性能口径由[正式设计](../12_features/增强索引统一检索与代码地图设计方案-2026-10-18.md)唯一维护；工作包与退出门禁由[施工计划](../12_features/增强索引分阶段施工计划-2026-10-18.md)唯一维护。源码现状、复现证据和已完成事实分别进入[审阅报告](../14_reports/2026-10-18-增强索引设计与实现审阅.md)及 changelog，不在本 ADR 累积任务台账。

## 1. 背景与目标

已有路径索引、Lucene、代码符号/关系、Outline 和分块/向量试验，但入口、身份、覆盖和生命周期尚未形成一个可验证的检索系统。Agent 需要在会话内同时处理多个项目，并从代码结构、依赖和引用理解项目；全文匹配或文件树不能承担这一目标。

统一文件与代码检索语义，建立可渐进展开的代码地图，并在已加载、索引就绪的选择性查询上实现毫秒级响应。性能以完整性和真实源证据为前提；冷启动、宽扫描和 embedding 分别计时与设预算。

## 2. 架构决策

### 2.1 组件和依赖

保留 **CodeIndex 负责存储/维护，CodeIntelligence 负责语言智能/图查询** 的拆分；CodeIndex 不得反向引用 CodeIntelligence。新建薄的 Retrieval.Contracts、Retrieval、Grep 和适配层，分别承载跨组件合同、纯编排、精确匹配与现有能力连接。详细归属与编译依赖以设计 §2 为准；不把新业务搬入 Host 或 Desktop。

Contracts为BCL叶子，编排经端口使用provider。基础存储/编排测试不加载Roslyn/MSBuild/Runtime；语言测试单独运行。每个新组件遵守S1–S5，独立测试与依赖门禁通过后才登记解决方案/DI/Host。Runtime只做工具及权限边界，Platform提供配置与状态适配，Host只组合与管理Core生命周期。

### 2.2 统一 grep 与真实覆盖

内置文件/代码内容检索统一走同一literal/regex、路径过滤、范围、预算和完整性合同。Lucene分词命中不等于精确grep；采用自研字符trigram候选与原文验证，没有可证明的必要字面量时扫描。借鉴 [tgrep](https://github.com/microsoft/tgrep) 的候选原理，不依赖其程序或额外搜索守护进程。

过滤必须先于最终TopK；候选/扫描预算耗尽必须报告partial。no_match需要该请求范围、语义与源版本的完整性证明。未提交修改、新文件、忽略规则、配置和解析器版本均参与失效，Git HEAD不能独自决定负缓存。任意Shell管道及记忆/日志领域检索保持各自执行和授权合同。

### 2.3 多项目会话与物理库

会话持有可同时加载的项目集合，提供load/unload/list；搜索默认作用于该集合，可显式收窄。加载不改变cwd、不提升权限、不表示索引已ready；卸载只释放该会话绑定与租约，**不得删除索引或影响其它会话**。集合变更用scope_revision/CAS，执行身份来自可信工具上下文。

保留每物理根一个ProjectLocal库、中央仅登记的目标。worktree、库、编译项目、符号实体、声明/引用occurrence、逻辑scope分别建模。DocumentationCommentId不能单独作为跨项目身份，同名不等于同实体；重叠scope不构成多份独立证据。旧中央代码库通过版本化重建切换，不建立长期双写/兼容层。

### 2.4 代码地图与增量语义

增强索引以outline和持久符号/关系图为基础，支持contains/imports/references/calls/inherits/implements/overrides及分层地图投影。精确编译绑定、语法提取、推断和未解析关系分别披露；方法组引用不是调用，动态/反射推断不能当静态确定边。

增量失效覆盖新增声明、重载组、lookup、未解析引用和编译依赖；不能只遍历旧符号incoming边。无法证明最小影响范围时安全重绑定受影响编译项目，完成前不得提升current水位。地图从清单/图读取，不在每次查询重新扫描整个仓库。

### 2.5 向量、模型边界与融合

outline驱动分块，保留symbol/parent/span/source_version；向量补充自然语言召回，回到代码证据打开。关键词/分块优先级只影响语义层，不改变精确文本与图覆盖。资源池按配置路由提供embedding实例；索引组件不读取密钥配置或直连模型HTTP。

向量必须支持持久upsert/delete、模型版本隔离和重建切换。float为质量基线，int8是压缩而非ANN；ANN按规模、Recall和延迟门禁引入。融合按符号/块身份去重，保留来源，不因重叠scope重复加权。

### 2.6 统一工具与渐进展开

workspace_search统一文件、符号、精确文本、语义与关系查询；workspace_open统一excerpt/outline/summary/relations/map/status。所有证据引用绑定身份、版本与授权范围，但不授予权限。项目集合工具与运维工具分离，purge不进入默认查询权限。

有界返回首屏、游标与exact/lower_bound/unknown计数，不要求每次同步遍历/导出全部命中。各provider独立报告能力、覆盖和新鲜度；auto按意图选择分支，不强制全引擎并跑。旧工具在调用方/模板/权限/遥测迁移后退役，过渡期也只允许一个底层查询服务。

### 2.7 维护、快照与资源

复用CodeIndex变更协调与持久dirty账本；Watcher仅提供提示，完整扫描负责校准。每库/provider单writer并有fencing，不引入跨全机单线程瓶颈。各provider独立提交generation，查询保持有界快照租约；不同源版本不得拼接成伪一致证据。

reader刷新先退休，最后读者释放后才Dispose/GC。离线或权限失败不触发批量删除。ProjectLocal默认1 GiB可配置，数据库/WAL/staging/向量/退休代际都计费，同时执行全局资源上限；配额不足保留可读代际并报告降级。

## 3. 取舍与被替代约束

引入公共合同、候选索引、图投影和项目集合，增加身份与水位管理成本；收益是减少工具选择、重复扫描及无法解释的错误否定。选择按组件独立门禁逐步交付，避免一次性将试验工程接入宿主。

原“不新建trigram引擎”“单库会话绑定”“所有分支必跑”“精确总量和全量落盘必需”“重叠scope增强置信度”不再有效。保留现有CodeIndex拆分、渐进展开、ProjectLocal、模型服务边界及异步维护。完整旧文见[历史快照](../90_archive/adr_089_legacy_20261018/103ADR-089Agent统一检索与渐进展开工具链ADR.md)，其它旧方案原路径仅作历史导航；新需求不得同时修改多套规范。

## 4. 实施与验收原则

按施工计划R1–R6执行：**已确认P1先修 → 多项目和身份 → 统一grep → 代码地图 → 生产向量 → 工具退役及产品验收**。第一阶段同时完成reader静态风险的可控并发验证，不将它混写成已复现事故。

正确性、依赖边界、选择性查询p95/p99、内存/句柄/磁盘、增量与故障恢复、真实Agent检索质量均设硬门禁，阈值与数据集在设计 §9 唯一定义。未通过不得以“索引建立成功”或“单测绿灯”代替产品结论。源码就绪、进程外部署、新构建内功能smoke、进程外生命周期验收分别出证据。
