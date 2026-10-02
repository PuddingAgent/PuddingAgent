---
title: 从 Docs/README.md 迁出的按日期进展记录（迁出日 2026-10-02）
author: hyfree
date: 2026-10-02
last_reviewed: 2026-10-02
status: archived
description: 〔原文第 5–8 行：2026-10-01 Chat 前端 UI / UX 现代化设计〕
categories: [docs, changelog]
tags: [docs, 迁出的按日期, 进展记录]
related_docs: [Docs/11_design/Chat-UI-UX-Modernization-Spec-2026-10-01.md, Docs/14_reports/Chat-UI-Modernization-Acceptance-2026-10-02.md, Docs/14_reports/Desktop-Build-Core-Bundle-Fix-2026-09-30.md, Docs/12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md, Docs/14_reports/Desktop-Shell-Recovery-2026-09-29.md, Docs/12_features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md, Docs/07_architecture/106ADR-092目标驱动执行与分层验证闭环ADR.md, Docs/14_reports/blocked-recovery-channel-decision-20260914.md, Docs/14_reports/blocked-one-way-latch-20260914.md, Docs/14_reports/task-bound-goal-settlement-deadend-20260914.md, Docs/14_reports/ADR-089-U0审阅与返工意见-2026-09-13.md, Docs/14_reports/ADR-089-U0返工验收-2026-09-13.md, Docs/14_reports/ADR-089-U0残差glob统一验收-2026-09-13.md, Docs/07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md, Docs/12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md, Docs/12_features/原生视觉与统一取图截图优化设计-2026-09-12.md, Docs/07_architecture/102ADR-088原生视觉取图与截图统一链路ADR.md, Docs/14_reports/原生视觉优化看板修订-2026-09-12.md, Docs/12_features/子代理弹性预算与双向交互设计-2026-09-12.md, Docs/14_reports/子代理弹性交互看板修订-2026-09-12.md]
related_files: [Docs/README.md, Docs/00_changelog/README.md, Docs/14_reports/PuddingAgent持续优化执行台账-2026-09-05.md, Docs/14_reports/PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md, Docs/14_reports/PuddingAgent效率与代码审计-2026-09-05.md, Docs/14_reports/PuddingAgent首轮修复与验证-2026-09-05.md, Docs/14_reports/PuddingAgent第二轮前端修复与发布验证-2026-09-05.md, Docs/14_reports/PuddingAgent第三轮部署与产品验收-2026-09-05.md, Docs/12_features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md, Docs/18_superpowers/specs/2026-06-06-runtime-steering-queue-design.md, Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md, Docs/12_features/AgentHarness兼容与工具调用效率修复设计方案.md, Docs/07_architecture/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md, Docs/12_features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md, Docs/07_architecture/97ADR-083Agent系统预制模板版本化快照与DeepSeek鲸鱼娘模板ADR.md, Docs/12_features/Agent消息交错内容流与最新行为组披露完整实施方案.md, Docs/07_architecture/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md, Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md, Docs/12_features/Chat图片消息回放与前端旧Bundle缓存修复方案.md, Docs/12_features/子代理活动轨迹实时回放与运行检查器修复方案.md]
slug: changelog-2026-10-02-docs-readme迁出的按日期进展记录
draft: false
---

# 从 Docs/README.md 迁出的按日期进展记录（迁出日 2026-10-02）

> **为什么在这里**：`Docs/README.md` 是文档索引，原先按日期堆叠的进展/状态段落，以及「当前主线文档」里各文档的状态批注与「当前实现状态说明」，全部迁出到本文件。日志规则见 `Docs/00_changelog/README.md`。
>
> **内容来源**：迁出前 `Docs/README.md` 的原文，**逐字保留，未做删改**。原文件快照可 `git show <迁出前提交>:Docs/README.md`。
> **路径约定**：链接目标已改写为**相对本文件**的可点击路径（原文以 `Docs/` 为基准），链接文字未变。
> **覆盖范围**：原文件第 5–221 行，共 14 个片段。

**〔原文第 5–8 行：2026-10-01 Chat 前端 UI / UX 现代化设计〕**

## 2026-10-01 Chat 前端 UI / UX 现代化设计

[实施规格](../../../11_design/Chat-UI-UX-Modernization-Spec-2026-10-01.md)：保留现有 Chat 功能与入口，规定视觉 token、布局、消息/过程渲染、输入交互、示例代码、实施切片及验收门禁。设计已交付，产品实现与验收待完成。已补浅/深色实图分析（§13）及滚动条缺陷 SCROLL-001：前端与 Shell 修复方案、分工和关闭门禁（§14）。P0 批次代码已完成：SCROLL-001 WEB/SHELL（`2430f97`、`6bf5b16`，见 §14.6）、IMG01/IMG04 色彩统一（`28dca85`）、IMG03 顶部工具栏分层（`7e38284`，见 §13.7）；Desktop 构建与测试证据已补齐（重定向输出构建 0 错误、Desktop 259 + Foundation 57 测试通过）。**§13.6 列出的代码项已全部落地**：前端七批（IMG06/IMG07 `7c2ad16`、IMG08 `5d53030`、IMG09/IMG10 `97fb4d2`、IMG03 帮助归组 `1c97f57`、Web 主题范围披露 `95055f1`、IMG07 标签表修正 `4ab177f`，见 §13.7–§13.10，前端版本 **6.1.6** + 页角版本徽标 `4749fd9`）与 Shell 两批（IMG11/IMG12 `96d5356`、IMG05 `53c2f1b`，见 §13.11–§13.12）。**深浅两套主题的静态外观已获像素复核**（[验收记录](../../../14_reports/Chat-UI-Modernization-Acceptance-2026-10-02.md)：底色收敛到 §3、滚动条白条消失、浅色 `#f7f8fa` 与 §3 基准一致、页角徽标与卡片可用性标签实测可见），但 §12 V/F/S/A/P/SEC/D、§13.6 IMG-V*、§14.5 滚动条验收表所需的**交互、DPI/缩放与性能**仍未实测。


**〔原文第 9–15 行：2026-09-29 WinUI Shell + Web UI + 独立 Core 恢复〕**

## 2026-09-29 WinUI Shell + Web UI + 独立 Core 恢复

[2026-09-30 普通构建缺少 Core 修复](../../../14_reports/Desktop-Build-Core-Bundle-Fix-2026-09-30.md)：补齐 Build 配套子进程与自动发现验证。

[权威 ADR](../../../12_features/ADR-Desktop-Shell-WebUI-Separate-Core-2026-09-29.md) · [分支恢复与验收记录](../../../14_reports/Desktop-Shell-Recovery-2026-09-29.md)。原桌面开发完整保存在 B；master 从 765b964 恢复，只接入 WinUI Shell 外观与必要 Core 修复，保留 Web 业务界面和独立进程启动器。



**〔原文第 16–19 行：2026-09-17 Goal模式重新规划〕**

## 2026-09-17 Goal模式重新规划

[简化设计](../../../12_features/Goal目标驱动执行与分层验证闭环设计-2026-09-15.md) · [ADR-092第二版](../../../07_architecture/106ADR-092目标驱动执行与分层验证闭环ADR.md)：Goal专属存储、单一状态机与决策入口、Agent回合/证据检查两类工作；移除Goal步骤树与两级Verifier，Task可选适配。Agent自身goal.md独立。设计已更新，产品实现及新构建验收待完成。


**〔原文第 20–23 行：2026-09-14 Blocked 卡 canonical 恢复通道（卡 813ad427）〕**

## 2026-09-14 Blocked 卡 canonical 恢复通道（卡 813ad427）

[设计裁定](../../../14_reports/blocked-recovery-channel-decision-20260914.md)：`task_update` 的上下文重建门槛放宽为 `InProgress｜Blocked`（仍需 active assignment 归属调用方 + 版本 CAS），Blocked 下合法 disposition 仍由服务端状态机 fail closed 裁决（仅 `todo` → Ready 并释放 active assignment）；`task_claim` 门槛不变。回归 `TaskActiveTaskFourChainE2ETests` T12–T15，`FullyQualifiedName~Task` 123/123 通过。


**〔原文第 24–27 行：2026-09-14 Blocked 单向闩锁（缺陷卡 e2c35d6e）〕**

## 2026-09-14 Blocked 单向闩锁（缺陷卡 e2c35d6e）

[审计报告](../../../14_reports/blocked-one-way-latch-20260914.md)：`TaskExecutionRepairCoordinator`（生产 `Enabled=true` / `Mode=authoritative`）只写 `Blocked` + 置 `ActiveAssignmentId=null` + 释放 assignment，**从不 re-arm**；此后派发扫描只取 `Ready|Deferred`、Tracker 候选要求 `ActiveAssignmentId == attempt.AttemptId`，worker 侧（`task_claim`/`task_update`）反查又必须命中 active assignment → 该组合态唯一恢复通道是管理面 `resume`/`requeue`，与设计 §8「Blocked 逃生通道」冲突。现场取证：卡 `3bd2a4b0` seq44 `task.accepted` → seq45 `task.blocked` 仅 102 秒，`task_get` 返回 `task.not_found`；已用管理面 `resume` 恢复为 `Ready`（seq46 `task.ready` @ 2026-09-13T19:12:18Z）。


**〔原文第 28–31 行：2026-09-14 结算单向死胡同（缺陷卡 dc0ac9a8）〕**

## 2026-09-14 结算单向死胡同（缺陷卡 dc0ac9a8）

[调查报告](../../../14_reports/task-bound-goal-settlement-deadend-20260914.md)：`ConservativeGoalIterationVerifier`（非 completed / `evidence_incomplete` → Blocked）与 `GoalSettlementStore`（task-bound 时 Goal=Failed、release assignment 并置 `ActiveAssignmentId=null`）叠加后，与 `TaskAgentCommandService.ApplyDispositionAsync` 的归属校验（要求 `task.ActiveAssignmentId == assignment_id`）构成**硬矛盾**：状态机允许 `Blocked→Ready`，但结算后不存在任何存活路径满足归属校验，归属 Agent 无法 canonical 上报；同时 `TaskExecutionRepairCoordinator` 的 `tracker-legacy-blocked-*` 是第二个独立单向口。现场：卡 `3bd2a4b0` 两轮自动派发分别在 accepted 后 94 s / 108 s 被 `tgb-*`（Goal 结算）判 Blocked。修复顺序 F3（派发端到端携带 task/assignment 元数据，先补单测）→ F1（恢复性结局改 NeedsReview 且不释放 assignment）→ F2（有界 re-arm，需 ADR 对齐）。本轮为取证与方案定稿，未改代码。**增补 §2.6**：`ReleaseAssignment` 只清 `task.ActiveAssignmentId`、**不清** `binding.AssignmentId`（陈旧残留），故「ActiveTask=null」不能再用「门禁判空」解释，断点收窄到下游投递/投影环节；派发链末端已有测试覆盖（`AgentExecutionWakeupActiveTaskPreservationTests.cs:139`），F3 缺口只在 `GoalContinuationWorker → delivery → claimed.Metadata` 一段。


**〔原文第 32–35 行：2026-09-13 ADR-089 U0 正确性返工（R1–R4）〕**

## 2026-09-13 ADR-089 U0 正确性返工（R1–R4）

[审阅意见](../../../14_reports/ADR-089-U0审阅与返工意见-2026-09-13.md) · [返工验收](../../../14_reports/ADR-089-U0返工验收-2026-09-13.md) · [U0 残差 glob 统一验收（G1–G4）](../../../14_reports/ADR-089-U0残差glob统一验收-2026-09-13.md) · [ADR-089](../../../07_architecture/103ADR-089Agent统一检索与渐进展开工具链ADR.md) · [详细设计](../../../12_features/Agent统一检索与渐进展开工具链设计-2026-09-13.md)。Lucene 候选只决定优先读哪些文件、必须按当前内容复核后输出；正则超时保留类型不降级为 `no_match`；单次调用唯一 deadline 覆盖候选/枚举/扫描并传播取消；覆盖完整性从 `errors==0` 起算（错误阈值只决定停止）且 `max_results` 统一作用于合并结果集。父级独立复跑 Retrieval 18/18、SearchGrepTool 45/45、FileSearchTool 21/21。


**〔原文第 36–39 行：2026-09-12 原生视觉与取图截图收敛〕**

## 2026-09-12 原生视觉与取图截图收敛

[完整方案](../../../12_features/原生视觉与统一取图截图优化设计-2026-09-12.md) · [ADR-088](../../../07_architecture/102ADR-088原生视觉取图与截图统一链路ADR.md) · [看板登记](../../../14_reports/原生视觉优化看板修订-2026-09-12.md)。先修当前主力模型缺失的视觉能力声明与过时图片预算，再收敛 Image Reader、流式制品传输，补齐 Web/Desktop 截图和主子代理视觉轨迹。ADR-077 V0–V3 已有实现；本次新增设计与真实运行验收尚待完成。


**〔原文第 40–44 行：2026-09-12 子代理后续修订〕**

## 2026-09-12 子代理后续修订

[子代理弹性预算与双向交互](../../../12_features/子代理弹性预算与双向交互设计-2026-09-12.md) · [看板修订](../../../14_reports/子代理弹性交互看板修订-2026-09-12.md) · [ADR-087](../../../07_architecture/101ADR-087子代理托管运行与持久问答ADR.md)。600不是固定上下界；系统托管任意正整数轮次，新增消息/停止/120秒问答与Web观察控制面，统一支持无Web无人值守。



**〔原文第 45–57 行：2026-09-12 下一阶段：缓存与长程自治〕**

## 2026-09-12 下一阶段：缓存与长程自治

[完整设计](../../../12_features/PuddingAgent长程自治与缓存99优化设计-2026-09-12.md) · [路线/任务/证据包](../../../14_reports/PuddingAgent-Next-Phase-2026-09-12/README.md)。ADR-084稳定最终请求与>99%验收、ADR-085 Memory主导长程状态仍为Proposed；ADR-086（已由 ADR-087 修订为弹性预算口径）的预算纠偏部分已实施（commit f096bc5）；ADR-087 双向交互/持久问答为最新权威且仍待实施。旧25–40轮建议被本次用户要求取代，历史审计数据保留。


2026-09-12 抖音续建：[douyin-creator-tools 调研、WebView2 复用与看板方案](../../../14_reports/DouyinCreatorTools-WebView2复用与看板方案-2026-09-12.md)。沿用 ADR-066/68，先真实 Agent 验收，再只读 Adapter、可靠回复；研究和登记完成不等于产品验收。

最后更新：2026-09-12（自主工作审计：8次心跳、12个子Run、898次请求、95.48%加权缓存命中；父级接续分叉、低产出心跳与终端/工具轨迹问题已登记；窗口外C01-A c89920f、授权接线b0cfa3a已提交，待独立验收及新构建验证）

2026-09-12 自主工作审计：[轨迹、效率、缓存与自改进设计](../../../14_reports/PuddingAgent-Autonomy-Audit-2026-09-12/01-自主工作轨迹与自改进审计.md)，[任务登记和实施顺序](../../../14_reports/PuddingAgent-Autonomy-Audit-2026-09-12/02-任务看板登记与实施顺序.md)。统计窗口BJT 09-11 00:00至09-12 06:24，窗口外进展单列；当前可局部自纠错，尚未证明明确新构建上的长期自主工作闭环。

持续推进入口：`Docs/14_reports/PuddingAgent持续优化执行台账-2026-09-05.md`。第四轮源码与阶段证据：`Docs/14_reports/PuddingAgent第四轮有界查询与流空档修复-2026-09-05.md`；19项定向/96项扩展回归，264制品/148前端核验，同任务56.898s/预热13.253s；资源后续回升仍未收口。


**〔原文第 60–60 行：2026-09-11 GLM 前端首批复核：[交互审计与下一步](Reports〕**

2026-09-11 GLM 前端首批复核：[交互审计与下一步](../../../14_reports/GLM前端首批交互审计与下一步-2026-09-11.md)。结论 needs_changes；类型检查通过，相关现有测试 40/41，7 个隔离契约反例未满足预期；看板按 Steering、停止、受理恢复、基线与产品验收分别收口。

**〔原文第 62–62 行：2026-09-11 前端体验评估：[前端交互体验优化建议](Reports/前〕**

2026-09-11 前端体验评估：[前端交互体验优化建议](../../../14_reports/前端交互体验优化建议-2026-09-11.md)。基于当前源码提出发送/排队/停止、状态反馈、长会话连续性、导航与交付物衔接的分期建议；状态 Proposed，未实施产品代码或完成实机体验验收。

**〔原文第 78–151 行：当前主线文档〕**

## 当前主线文档

- [2026-09-12 GLM 实施进度复核与看板状态修订](../../../14_reports/PuddingAgent-GLM-Optimization-2026-09-11/06-实施进度复核与看板状态修订-2026-09-12.md)
	- 当前进度入口：F01真实界面消费者、S01-B持久请求身份/本地恢复待补；C01-A先验收，再C01-B/C02。五卡NeedsReview、C02 Backlog、缓存总卡InProgress；源码accepted不等于生命周期Completed或生产验收。附结构化回执。

- [2026-09-11 GLM 批次1独立审计与下一步](../../../14_reports/PuddingAgent-GLM-Optimization-2026-09-11/04-批次1独立审计与下一步.md)
	- 9月11日历史审计与缺陷设计；同目录 [当时看板回执](../../../14_reports/PuddingAgent-GLM-Optimization-2026-09-11/05-后续任务与看板回执.md) 保留六个新任务和四个既有任务更新。原10个失败探针已于9月12日转绿，剩余条款和当前状态以06为准。

- [2026-09-11 架构、代码整洁、稳定性与缓存优化审阅](../../../14_reports/PuddingAgent-GLM-Optimization-2026-09-11/01-代码审阅与优化设计.md)
	- 原始 15 个优化工作包及施工范围：水合并发、usage 原子聚合、Composition 提交/恢复、最终请求 manifest、增量归档、Chat 读模型、资源生命周期和唯一组合根。当前实施和验收状态以同目录06进度复核为准；01/02保留设计合同，03–05保留历史。

- `Docs/14_reports/PuddingAgent效率与代码审计-2026-09-05.md`
	- 2026-08-29–09-04 完整七日账本与 09-05 实时诊断：夜间有效吞吐、legacy claim 假忙、缓存、工具参数合同、Chat 401 重连、归档回放开销、干净提交/脏工作区/已部署 build 的验收差异；只读审计及后续五轮优化方案，未实施修复。
- `Docs/14_reports/PuddingAgent首轮修复与验证-2026-09-05.md`：调度终态/重试 fence、文件补丁、Chat 鉴权退避、执行监视与归档空转优化；已做定向测试，未部署；含七日统计预筛选修正。
- `Docs/14_reports/PuddingAgent第二轮前端修复与发布验证-2026-09-05.md`：修复失效样式、性能面板合同、重连计数传递；全量类型检查与 118 项测试通过，独立 Desktop/Core 发布包及 hash 已核验，尚未切换运行实例。
- `Docs/14_reports/PuddingAgent第三轮部署与产品验收-2026-09-05.md`：经既有 Desktop 主管部署新 Core/前端；264 受管制品与 148 前端源文件核验、1 次 file_read canary 成功。揭示 161.749 秒流事件空档、预热后 Private 回升和无可派库存；更正部署前 idle 误标，不宣称性能收益。
- `Docs/12_features/Chat独立插嘴按钮与当前Turn即时Steering设计方案.md` / `Docs/18_superpowers/specs/2026-06-06-runtime-steering-queue-design.md`
	- 复用既有 current-Turn durable Steering，为运行中 Composer 增加独立 `⚡` 直达入口；明确不进入普通待发队列、不创建第二个 Turn、202 后 compare-and-clear、409/失败保留草稿、图片 fail closed、单飞幂等和明确部署 smoke。当前仅设计，关联 P1 任务 `ed88185f1d3b4e16a70e9b9ea0f0e040`，尚未实施/部署。
- `Docs/18_superpowers/specs/2026-06-03-auto-tool-approval-design.md`
	- 自动权限审查唯一设计入口：合并确定性危险命令防火墙与三层漏斗，冻结用户审批最后手段、参数级风险分类、系统派生风险事实、重复审批熔断和 `save_memory upsert` 零审批；关联唯一 P1 任务 `e187a8bbd2d640bb87b96fd3cf548966`，尚未部署验收。
- `Docs/12_features/AgentHarness兼容与工具调用效率修复设计方案.md` / `Docs/07_architecture/95ADR-081AgentHarness兼容边界与工具协议适配ADR.md`
	- 冻结“canonical 工具唯一、统一执行边界前适配、短稳定提示、no-match 结构化、round-boundary 动态工具激活、discovery-only 熔断、可选而非默认 bundled rg”的 Harness 兼容边界；2026-08-28 已修复 216 次 `search_tools` 高命中零 Goodput 事故，进程外部署与真实模型验收未完成。
- `Docs/12_features/Agent系统预制模板完整快照与DeepSeek鲸鱼娘模板设计方案.md` / `Docs/07_architecture/97ADR-083Agent系统预制模板版本化快照与DeepSeek鲸鱼娘模板ADR.md`
	- 冻结系统预制目录包、完整 Creation Snapshot、版本/哈希/许可、显式升级与 Workspace 创建时全字段自动填充；重写通用助手并增加 `deepseek-whalechan` 原创文本社区角色模板。当前为 Proposed，未修改产品代码或运行数据。
- `Docs/12_features/Agent消息交错内容流与最新行为组披露完整实施方案.md`
	- Flash 可直接施工的代码级合同：canonical sequence、TextBlock ⇄ ActivityGroup、会话级唯一最新披露 owner、完整 reasoning 换行、工具详情懒加载、柔和收起/卸载、性能、逐文件任务卡、测试命令和双阶段真实验收。
- `Docs/07_architecture/93ADR-079Agent消息交错内容流与最新行为组披露ADR.md`
	- 冻结一个 AgentTurnCard 内真实交错、唯一正文源，以及“当前最新 Agent 回合最后行为组持续展开；最终正文不关闭；新行为/新回合才转移并收起旧组”的架构决策。Accepted 只表示设计决策冻结，不表示实现完成。
- `Docs/07_architecture/92ADR-077主代理原生视觉理解与多模态消息链路ADR.md`
	- 冻结主视觉模型直接消费 typed image content、Workspace Artifact、DeepSeek Responses `input_image`/图片型工具结果、Files API、多轮重启恢复、fail-closed 与视觉用量；Image Reader 改为默认只传路径的按需取图工具，支持 URL/任意绝对路径，并保留显式 helper 委派能力。当前为 Proposed。
- `Docs/12_features/Chat图片消息回放与前端旧Bundle缓存修复方案.md`
	- 记录 2026-08-26 图片占位故障的消息/Artifact/DOM/Bundle 证据链；修复 Agent-first `contentParts` 投影断点、localhost 旧 Service Worker 清理、静态资源缓存合同、build identity 与两段式产品验收。当前为 Proposed，关联任务 `ceba781342aa4353901654d1897092cb`。
- `Docs/12_features/子代理活动轨迹实时回放与运行检查器修复方案.md`
	- 记录 Run `run_20260826_111621_3a711865a9f8` 的 archive/cursor/canonical event/UI/Bundle 证据链；修复活动子代理未纳入 gap replay、空卡片无同步降级、聚合计数受有界详情截断与 build identity 不可见问题，坚持活动 Run 零归档轮询。当前为 Proposed，关联 P1 任务 `791d062fa6ea44f18bfe5027a37696d0`。
- `Docs/07_architecture/89ADR-074Goal持久目标自主续行与自动压缩ADR.md`
	- 冻结 GoalRun 持久续行、证据验证、Task-bound Goal、Agent 可用性感知与低峰自动派发；明确 Auto Task 以 Goal 为前置且不依赖 Heartbeat。
- `Docs/12_features/TaskBoundGoal与Agent状态感知自动派发代码级施工计划.md`
	- 2026-08-28 已部署的是五分钟 Shadow；后续源码已补结构化路由、auto-dispatch opt-in、Backlog refinement、版本化 WorkUnit、Acceptance/执行前双重 Plan/Node 围栏、顺序推进、round/tool/time/input/output/cost 硬预算和五分钟确定性 repair。新源码尚未进程外部署；AwaitHandle/checkpoint、blocked 重预约、动态模型反馈、authoritative 与七夜验收仍未完成。
	- 按 Core/Platform/Runtime/Host/Admin 列出类、表、事务、事件、文件、施工卡、测试、切换和生产验收门禁；2026-08-28 已进入五分钟 shadow 对账，修复终态 Assignment false-busy 与通用 PATCH 伪完成，authoritative 仍未开放。
- `Docs/12_features/Scheduler夜间有效调度与Execution生命周期闭环代码级实施方案.md`
	- 记录 2026-08-31 夜间“Intent 已 done 但无 durable decision、自动派发为 0、legacy execution claim 永久占用 Agent、Blocked 卡不可干预”的证据，给出 Tracker/Repair、task-scoped Outcome、staged mode、scan-run、Blocked UI、预算硬门禁和真实自动派发 smoke 的文件级施工步骤；状态为 Proposed，不代表源码或产品验收完成。
- `Docs/07_architecture/91ADR-076遥测与调试数据保留及Core存储管理ADR.md`
	- 冻结 Core + Web Admin `/storage` 边界、语义数据类型目录、唯一在线维护 writer、关键事实保护和禁止在线全库 VACUUM；当前为 Proposed。
- `Docs/12_features/遥测调试数据自动过期与Web存储管理设计方案.md`
	- 自动过期、缓存快照与后台增量估算、分类占比图/趋势报表、按类型/时间近似 Preview、异步清理作业、策略配置、文件级施工与验收方案。
- `Docs/07_architecture/87ADR-073任务看板优先的Agent工作台轨迹与实时指标施工ADR.md`
	- 当前产品施工入口；列出 30 项产品任务和 17 项 T00–T16 平台底座任务的目标、优先级、工作量、难度、依赖和设计位置，并把各专项 Phase 去重到唯一 Canonical Owner。产品顺序为任务看板 → Auto/Cron → 完整轨迹 → 实时指标 → 插件化收口。
- `Docs/07_architecture/86ADR-072工作区TODO峰谷Auto派发与定时任务第一阶段ADR.md`
	- WorkspaceTask、五列 Board、Failed/Reopen、手工/Auto 派发、受限 Cron、Task executionWindow、provider/model 价格时段 Resolver、Task Tools、Admin 和恢复的任务领域合同；不新增 `work-policy.json`。
- `Docs/19_references/deepseek-reference-architecture-master-plan-2026-08-14.md`
	- 本次会话设计总入口；以“模型、工具、技能、会话、Agent Loop、沙箱、存储、调度和 UI 均为插件”为第一原则，汇总组件级映射、文件级修改矩阵、任务图、T00-T16 施工卡、验收和风险边界。
- `Docs/19_references/deepseek_harness/deepseek-harness-pi-plugin-hook-event-architecture-2026-08-14.md`
	- 插件、Typed Hook、durable event 与统一生命周期的上位架构；覆盖心跳自主推进和事件驱动自学习闭环。
- `Docs/19_references/deepseek_harness/deepseek-harness-tool-system-alignment-2026-08-14.md`
	- 工具 canonical output、callId、结构化错误、执行 Hook、并发、spill 与 presentation 方案。
- `Docs/19_references/deepseek_harness/deepseek-harness-message-card-alignment-2026-08-14.md`
	- 消息、推理、工具调用与子代理过程的前端投影方案。
- `Docs/07_architecture/架构.md`
	- Pudding Agent Network 的架构总览与阅读入口。
- `Docs/07_architecture/README.md`
	- 按模块拆分后的架构分册目录。
- `Docs/15_tasks/Tasks.md`
	- 全局任务入口，任务状态通过 Todo API 管理。
- `Docs/07_architecture/18上下文缓存可观测性ADR.md`
	- LLM prompt cache hit/miss 解析、统计和前端可观测基线。
- `Docs/07_architecture/43ADR-042上下文自动压缩与主动Compact命令ADR.md`
	- 长会话 Compact、LLM 前置输入压缩、Headroom 研究结论和可逆取回边界。
- `Docs/12_features/上下文自动压缩与Compact命令设计方案.md`
	- Compact API、上下文健康状态、InputCompression 原型和验收计划。
- `Docs/12_features/上下文Token效率缓存命中与分级压缩优化设计方案.md`
	- 7 日 Token/工具重放/搜索失败/ZIP 基线，以及无损 artifact、分级压缩、Compact 覆盖门禁和 DeepSeek 缓存 `>99%` 的施工与验收合同；2026-08-28 追加 95.92% 事故基线、Harness warm-prefix checkpoint、prefix-v2 与审批控制面降耗实施记录。
- `Docs/12_features/服务商余额查询与多服务商计费适配器设计方案.md`
	- 聊天页主代理余额徽标（DeepSeek 首个落地）+ 前后端双注册表计费抽象：后端 `ILlmBalanceProvider` 查询适配器、前端 `providerBilling.ts` 展示适配器；含新服务商扩展步骤、刷新策略与 apiKey 安全约束。已实施（2026-08-24）。


**〔原文第 214–221 行：当前实现状态说明〕**

## 当前实现状态说明

- ADR-077 当前是基于现有多模态代码骨架形成的目标设计，不表示 `deepseek-v4-flash-vision-exp` 已通过当前轮、多轮、重启和 Files API 的真实模型验收。
- ADR-074 及 Task-bound Goal 代码级施工计划当前只是设计定稿，不是实现或生产验收证据；现有 `goal_queue.json`/`GoalModeService` 不等价于持久 GoalRun。
- Phase 1A Desktop Launcher、Phase 1B-R Runtime Center、Phase 1B-S Storage、Phase 2A-1/2 和 Phase 2A-3 确定性实现已于 2026-08-02 验收。Phase 2A-3 已交付 Snapshot、Locator、八项 Interact、Wait、版本化 ref、四项新 Agent Tools、真实 WebView2 TestSite、Release publish 与可见 Desktop 退出 smoke；结果见 76。真实 DeepSeek Agent 的工具选择 smoke 仍需用户明确选择测试 Agent/DataRoot，完成前不进入 Douyin Adapter。`dev-up.py` 保留为源码开发脚本，不进入最终产品。
- Phase 2A-3B 的真实 DeepSeek 工具选择验收按 77 执行；通过前不得开始 Douyin Adapter 实现。
- 当前源码中仍保留旧架构和开发脚本入口，阅读 Desktop 主线时以 68、69 实施规格为准。
- 任务状态通过 Todo API 管理，不在文档或代码中硬编码。

