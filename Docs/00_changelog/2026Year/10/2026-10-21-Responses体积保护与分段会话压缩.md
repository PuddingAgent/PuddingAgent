---
title: 2026-10-21 Responses体积保护与分段会话压缩
author: hyfree
date: 2026-10-21
last_reviewed: 2026-10-21
status: archived
description: "修复大窗口模型仅按token触发压缩、摘要重放过大及压缩失败后仍上传大请求的路径。"
categories: [docs, changelog]
tags: [responses, deepseek, compaction, diagnostics]
related_docs: [Docs/08_how_debuge/05-常见症状.md, Docs/00_changelog/2026Year/10/2026-10-21-LLM重试事实与执行身份修复.md]
related_files: [Source/PuddingRuntime/Services/LlmRequestTextBudget.cs, Source/PuddingRuntime/Services/BoundedCompactionSummary.cs, Source/PuddingRuntime/Services/WarmPrefixCompaction.cs, Source/PuddingRuntime/Services/AgentExecutionService.cs]
slug: changelog-2026-10-21-responses-bounded-compaction
draft: false
---

# 2026-10-21 Responses体积保护与分段会话压缩

关联现场：`llm-6eb95759bf7541a3b4e0197b70de73f5`，上传体积 1,451,650 bytes。用户已验证关闭 TUN 仍失败；本次未修改网络设置。

## 原因与改动

- 原发送前门禁主要使用 token 预算；软阈值维护延后至 canonical 终态，大窗口模型可能允许 MB 级文本继续增长。同步摘要还会重放完整历史及工具头，摘要自身上传失败后，原始历史被保留，后续主请求仍可能过大。
- `LlmRequestTextBudget.cs` 对 DeepSeek Responses 增加独立的 512 KiB 文本上传运行预算，包含正文、思考、续传状态与工具定义的保守 JSON 估算。它是客户端策略，不是 DeepSeek 官方请求上限；图片二进制仍走视觉预检及 Files 预算。
- `LlmRequestBudgetGuard.cs` / `WarmPrefixCompaction.cs` 同时按 token 和文本体积选择安全跨度；体积触发后尽量降至半预算。Buffered/Streaming 每轮重新检查，不受软维护次数封顶或少量消息条数限制。主请求实际发送前再复核，超限则停止发送并保留原历史。
- `BoundedCompactionSummary.cs` 对选中历史分段总结并归并，单段文本预算 128 KiB，最多 64 次摘要调用；源文本不截断，工具记录作为引述材料，摘要请求不携带可调用工具定义。图片保持实际内容部件与冻结路由。每次摘要分别记录 usage，最后通过收益、双预算、身份与持久代次 CAS 后才提交。
- `AgentExecutionService.cs` 保留小请求暖前缀路径；大请求采用 `provider_segmented`。任何分段失败、空结果、工具调用或截断均不得提交部分摘要；传输失败与截断不降级为本地摘录。
- 诊断增加 `local.context_compaction_required`、文本体积/预算/工具定义体积/未发送事实；上传连接重置不再直接归因为网络或代理不稳定。

## 验证

- 独立分段组件：6 项通过；体积策略接入前独立测试：3 项通过。
- WarmPrefix、分段摘要及体积门禁回归：72 项通过。
- 新增轮内增长、工具定义体积及既有请求预算/终态报告回归：36 项通过（与前组分开执行，不作去重总数）。覆盖 Buffered/Streaming、大窗口未触及 token 阈值、约 1.44 MB 历史、截断时零提交与零超大主请求。
- 诊断叶子组件：32 项通过，包括新增稳定码场景与组件依赖边界。全仓 Docs Front Matter 检查 1,714 个文件，违规 0；文档链接检查仍有 25 处既有失效引用，本次修改文件没有新增失效目标。
- 裸 Python 标准库 HTTPS 合成请求：32,148 / 128,148 / 512,148 bytes 均得到 `response.completed`；1,450,148 bytes 在上传阶段约 29 秒中断。该实验支持体积相关性，不能证明固定服务端上限。
- 真实 Pudding `ResponsesLlmGateway` + 分段组件：1,518,126 字符合成历史，13 次分段 + 1 次合并全部完成，最终摘要 1,893 字符，总耗时 108,776 ms。初次探针在第 2 段遇到提前断连；加入与产品相同次数的传输重试后重新执行，成功这一轮未发生重试。探针不包含用户会话数据，密钥仅在内存使用。
- 参考 [DeepSeek Responses 指南](https://api-docs.deepseek.com/zh-cn/guides/responses_api)：无状态全量历史由客户端维护，不依赖服务端自动压缩。实际安装的 Harness 默认适配器使用 Messages 协议；其成功不能直接等同 Responses 相同大请求成功。本次继续使用 Responses。

## 未完成与边界

此记录对应代码验证；部署结果另行追加。未对用户原始会话发送测试消息，未改写生产数据库；原会话恢复需在新构建上实际重试验证。512 KiB 是文本估算预算，不包括视觉上传后的实际图片字节，也不保证消除所有瞬态传输故障。
