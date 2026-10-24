---
title: 2026-10-24 deepseek-harness token 计量机制对照
author: hyfree
date: 2026-10-24
last_reviewed: 2026-10-24
status: active
description: "拉取 DeepSeek 官方客户端 deepseek-harness（22ddee3792），核对它的上下文计量与压缩策略。结论：它的 token meter 是每 token 4 字符的固定启发式并自认对 CJK 低估，不是精确词表；真正值得借鉴的是「provider 实测作锚 + 只估增量」的 anchoring 机制，以及「分项永不当总量」的显示口径。同时证伪本方两个怀疑：压缩阈值 0.8 与官方一致，精确词表也已命中。"
categories: [report]
tags: [context, tokenizer, compaction, deepseek-harness, evidence]
related_docs: [Docs/00_changelog/2026Year/10/2026-10-24-context-health-modelid.md]
related_files: [Source/PuddingTokenization/ModelTokenizerCatalog.cs, Source/PuddingCore/Platform/LlmOptions.cs]
slug: report-2026-10-24-deepseek-harness-token-meter
draft: false
---

# deepseek-harness token 计量机制对照

## 取数范围

`F:\github\deepseek\deepseek-harness`，`git pull --ff-only` 由 `687f282e86` 快进到 `22ddee3792`，
工作树干净。以下引用均取自该 commit 的工作副本。

## 一、官方不是精确分词，是固定启发式

`packages/llm/token-meter/src/estimate.ts`：

```ts
const CHARS_PER_TOKEN = 4   // :13
const BLOCK_OVERHEAD = 4    // :16
export const ROLE_OVERHEAD = 4  // :19
```

所有文本一律 `ceil(len / 4)`，每 block 加 4，每条消息再加 4。`compaction-basic/README.zh.md:268`
自己写明：

> tokenizer 精确测量，暂缓——token meter 每 token 四字符的启发式对 CJK 文本与 JSON Schema
> 定价偏低；精确 token 化仍是测量服务的开放方向。

**推论**：中文场景下官方的数字是**低估**的，它不能作为 token 真值，也不能用来反推 provider 口径。
我们磁盘上的 `deepseek-v4.1-flash.tokenizer.json` 在这一点上比官方更准。

## 二、真正值得借鉴的是 anchoring

`docs/subsystems/token-meter.zh.md` 给出的核心公式：

```
projectedTokens = pressureTokens + heuristic(surface 相对锚点的增减)
```

- `pressureTokens` = **provider 最近一次报告的 prompt 规模**（uncached input + cache read + cache write，**排除 response output**）
- 只对**增量**用启发式重定价，因此数字始终钉在 provider 实测上，同时能在压缩遮住一段内容时立即反应
- `baseline.kind === 'usage'` 仅在**最近一次成功调用的 envelope 与当前请求完全一致**时复用；否则整包重估

我们当前的做法是**每次从零估算整个请求，再用一个校准系数整体缩放**。两者差别在误差结构：
整体缩放只能消掉乘性偏差，消不掉「哪些消息真的被发出去」这类加性差异。

## 三、分项永不当总量

`packages/llm/token-meter/src/projection.ts:52-61`：

> 三个数字都用固定密度估算，因此**不会等于**锚定 provider 的 `projectedTokens`：估算器系统性
> 低估 CJK 与 JSON Schema，**而这正是 anchoring 要挡在占用数字之外的误差**。分项只能作为
> 构成近似呈现，永不当作总量。

这条与 2026-10-24 早些时候面板修复（`f5ca102`，把图例分项强行统一到标题分母）方向相反。
官方答案不是「让分项和标题同源」，而是「分项只作构成近似、总量锚定 provider 实测、两者不相加」。

## 四、两条被证伪的怀疑

| 怀疑 | 结论 | 证据 |
|---|---|---|
| 压缩阈值 0.8 设得太早 | **否**。官方同为 0.8 | `compaction-basic/src/config.ts:20` `DEFAULT_THRESHOLD_RATIO = 0.8`；压缩后保留 `DEFAULT_RETAIN_RATIO = 0.16`（:23） |
| 精确词表没被用上（`deepseek` 走回退） | **否**。精确别名已命中 | `Source/PuddingTokenization/ModelTokenizerCatalog.cs` 的 `ExactMap` 显式含 `["deepseek-flash"] = "deepseek-v4.1-flash"`；该表刻意不做前缀猜测 |

阈值一致 + 词表已命中 ⇒ 重启后的 `1.1378`（raw 57,776 ÷ provider 50,777）**只剩两种解释**：
报文在估算之后被改写，或估算口径与 provider 计费口径不同。二者只能靠同一份报文的
`bodyTokens` 判定——该仪器目前被「进程环境块不可变」挡着，属未决项，不在此处下结论。

## 五、可操作的下一步（未实施）

1. 把「从零估算 + 系数缩放」改为「provider 锚 + 增量重定价」，即移植 anchoring。
2. 面板维持两套口径但**显式标注不可相加**：总量锚定 provider，分项标注为构成近似。
3. `bodyTokens` 仪器恢复后，优先判定「改写 vs 口径」，再决定是否动估算器。
