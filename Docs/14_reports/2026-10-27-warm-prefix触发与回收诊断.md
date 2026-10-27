---
title: 2026-10-27 warm-prefix 触发与回收诊断
author: hyfree
date: 2026-10-27
last_reviewed: 2026-10-27
status: active
description: 核对指定后台压缩的持久事件、冻结现场和 DeepSeek 实测用量，区分准备时目标、提交时估算和后续请求用量。
categories: [docs, reports]
tags: [compaction, diagnostics, prefix-cache]
related_docs: [Docs/07_architecture/前缀缓存友好的会话压缩.md]
related_files: [Source/PuddingContextPolicy/PrefixCompactionPolicy.cs, Source/PuddingContextPolicy/ContextCandidateSelector.cs, Source/PuddingRuntime/Services/WarmPrefixCompaction.cs, Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Compaction.cs, Source/PuddingCore/Runtime/ContextUsageDisplay.cs]
slug: reports-compaction-trigger-reclaim-168198
draft: false
---

# 2026-10-27 warm-prefix 触发与回收诊断

## 结论

本次已真实、持久提交压缩。DeepSeek 相邻主请求实测输入从 652,979 降到 442,229 tokens，减少 210,750，约 **32.3%**。保留约 44 万 tokens 符合当前约注册窗口 45% 的目标；没有把整个会话全部替换成几千 token 的设计意图。

用户截图为后续快照：443.1K / 935.0K = 47.4%。它使用有效输入窗口作分母；注册窗口是 1,000,000，因此相同用量约为注册窗口 **44.3%**。截图不能直接与“压缩后目标为注册窗口 45%”比较。

## 定位与证据

- compactionId：`warm-prefix:16819807b91347d69a62703d5dc5a95d:r1:1beaa62e94464fe2bfd3889bcf5d9387`（UI 外层另有 `compaction:` 前缀）。
- traceId：`7a9747ceca764ba1a91bad1e3872e1d0`。
- `<DataRoot>/logs/diagnostics/compaction/20261027-89b29283068768b396021fad596c8367e58f03ffb87c26bbb746eb20850ad2d8.jsonl`：完整冻结计划、18 次摘要尝试与提交现场。
- `<DataRoot>/logs/system/pudding-20261027_035.log`：末段请求边界决定与持久提交。
- `<DataRoot>/databases/pudding_platform.db`：以 SQLite `mode=ro` 查询 `conversation_events` 的序号 2349824、2350198、2350202、2350203，以及同会话 `llm_gateway_usage_events` 相邻主请求；未写数据库。

## 触发与提交时间线（北京时间）

| 时刻 | 事实 |
|---|---|
| 21:56:46 | 当前执行第一轮准备候选，冻结 1,064 条消息；估算 637,907 tokens，即注册窗口 63.79%；文本 3,673,506 / 5,609,856 字节，即上传预算 65.48% |
| 22:00:30 | 14 个分段及分层归并完成，候选就绪；共 18 次摘要请求，其中一次归并超额度后重生成 |
| 22:00:36 | 第 18 个请求边界，`safe_boundary_commit`；tokens=653,096，字节压力 67.52%，`wait=false`，此前仍在正常执行工具与模型请求 |
| 22:00:38 | 持久提交：generation 415→416；替换 357 条，消息数 1,098→742；耗时 231,815ms |
| 22:00:38 发起的主请求 | Provider 实测输入 442,229 tokens |
| 22:00:51 发起的主请求 | Provider 实测输入 442,731 tokens，与截图 442.7K 对应 |

运行时配置文件没有覆盖 `contextCompaction`，代码默认比例为准备 60%、采用就绪候选 65%、等待 70%、目标 45%。token 轴按注册窗口计算；文本上传轴独立计算，默认等待比例 95%。本 ID 准备时两条轴均已超过准备阈值，文本轴同时超过提交阈值；候选尚未就绪，所以先保持旧前缀继续执行。现有证据不代表此前所有执行的第一次阈值评估；较早系统滚动日志已不在该目录。

## 回收量与保留范围

| 口径 | 压缩前 | 压缩后 | 回收 |
|---|---:|---:|---:|
| 同一次提交重新计量的完整上下文 | 653,096 | 459,036 | 194,060，29.7% |
| 完整请求文本字节（不等同最终 HTTP wire 字节） | 3,787,776 | 2,641,638 | 1,146,138，30.3% |
| 相邻主请求 Provider 实测输入 | 652,979 | 442,229 | 210,750，32.3% |

相邻 Provider 请求包含正常追加及协议投影，不能当作完全相同请求的 A/B 实验；提交日志的 29.7% 是同一次重计量口径。

选择器按完整执行单元扩展连续跨度，满足目标即停止；本次选择位置 1 起的 357 条。后面位置 358 起仍有完整、可选择的历史单元，并非 742 条全部受到硬保护。目标为约 450,000 tokens，选择器没有被要求把剩余历史全部压空。

准备期间消息数由 1,064 增到 1,098，新增 **34 条**完整保留。提交时旧估算比准备时增加 15,189 tokens，重计量结果为 459,036，比目标高约 9K，因此明确记录 `meetsTarget=false`，并非虚报达到目标。净收益准入允许有真实回收且低于硬上限的结果提交；不会为少量软目标偏差重复破坏前缀缓存。后续 Provider 实测使校准系数更新，约 459K 的提交估算与截图 443.1K 属于不同时间快照。

最终摘要本身约 4,013 本地 tokens、14,617 文本字节；截图“压缩后记忆”约 0.3% 仅是摘要大小，不是本次回收比例。剩余约 44 万主要是保留的对话和工具历史。

## 前缀缓存

压缩前最后一请求命中 652,544 / 652,979 tokens（99.93%）。压缩后第一请求命中 26,752 / 442,229（6.05%），体现前缀切换成本；紧接着第二请求命中 442,496 / 442,731（99.95%）。本次证据支持切换后缓存正常重建，没有持续失去命中的现象。

## 暴露的诊断不足与后续改进

1. `started`、`completed` 事件的 `reason=hard_input_budget_protection` 不准确：实际是后台准备与安全边界采用，边界日志 `wait=false`。`applying` 事件又统一写 `requiresSynchronousProtection=true`，需与“短暂原子提交”及“达到 70% 而等待”区分。
2. 完成事件的 `round=1` 是候选创建轮；实际应用边界是第 18 轮。应分别记录准备轮和提交轮。
3. UI “压缩判定同源”没有说明注册窗口与有效窗口两个分母；应明确展示触发轴、绝对阈值及注册窗口占比，成功卡同时展示前后用量、回收比例和是否达标。
4. 当前准备计划未为摘要生成期间的新增消息单独预留目标余量。可在保持冻结前缀和完整执行单元的前提下，引入有界增长预留；不宜仅为这次约 9K 的暂时软目标偏差立即再压一次。

本次仅进行只读诊断并保存报告，未调整阈值、未修改代码或原会话、未重启产品，也未触发新的模型请求。上述诊断字段/UI/增长预留改进尚未实现。

算法与预算口径参见[前缀缓存友好的会话压缩](../07_architecture/前缀缓存友好的会话压缩.md)。
