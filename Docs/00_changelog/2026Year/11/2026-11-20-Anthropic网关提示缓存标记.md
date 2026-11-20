---
title: Anthropic 网关补上提示缓存标记
author: hyfree
date: 2026-11-20
last_reviewed: 2026-11-20
status: active
description: 在 Anthropic Messages 请求体里补上 prompt caching 的 cache_control 标记。此前该网关从不发送标记，导致走 Claude 路由时缓存命中率恒为 0。
categories: [changelog, llm, cost]
tags: [anthropic, prompt-caching, cache-control, cost]
related_docs: [Docs/07_architecture/持久子代理与团队协作规划-2026-11-20.md]
related_files: [Source/PuddingCore/Core/AnthropicMessagesLlmGateway.cs, Source/PuddingCoreTests/AnthropicMessagesLlmGatewayTests.cs]
slug: anthropic-prompt-cache-control
draft: false
---

# Anthropic 网关补上提示缓存标记

## 背景：命中率为 0 不是模型的问题

2026-11-20 实测 `Source/PuddingCore/Core/AnthropicMessagesLlmGateway.cs`（771 行）中
`cache_control` 出现 **0 次**，且 system 被 `string.Join` 拼成**纯字符串**后直接赋给
`root["system"]`。

Anthropic 的 prompt caching 是**显式 opt-in**：必须在 content block 上标记
`{"cache_control":{"type":"ephemeral"}}`，纯字符串形式无法挂标记。因此此前走 Claude
路由时缓存命中率只能是 0——这不是模型「不吃缓存」，是我们从不请求。

对照：平台早已能**读**缓存统计（`OpenAiLlmGateway.cs:657-667` 读
`prompt_cache_hit_tokens` 与 `prompt_tokens_details.cached_tokens`，
本网关 `:482-485` 读 `cache_read_input_tokens` / `cache_creation_input_tokens`），
只是从不**发**标记。

## 改动

两处断点，Anthropic 上限 4，本实现只用 2：

1. **system 改为 block 数组**：单元素 `{"type":"text","text":<system>,"cache_control":{"type":"ephemeral"}}`；
   system 为空时保持原行为（不设该字段）。
2. **tools 最后一个元素**打标记：循环构建 `toolNodes` 后，若非空则给末尾元素补
   `cache_control`。只标最后一个即可覆盖整段 tools；逐项标记会浪费断点且无额外收益。

未改动：OpenAI/DeepSeek 路径、响应解析、流式使用量采集、任何配置开关。
第一版不引入 `CachePolicy` 抽象——当前只有一条 Anthropic 路径，等第二个协议接入再抽。

## 验证

| 步骤 | 结果 |
|---|---|
| `dotnet test PuddingCoreTests --filter AnthropicMessagesLlmGateway` | 16 / 16 通过 |
| 变异（移除 system 的 cache_control） | **失败 2 / 通过 14**，非编译错误 |
| 还原 | SHA-256 比特一致 `C767F36E…9065601F`，重跑 16 / 16 通过 |
| `PuddingCoreTests` 全量 | **1259 总计 / 1258 通过 / 0 失败 / 1 跳过** |

新增用例断言：`system` 是数组、单元素、`type=text`、含 `cache_control.type=ephemeral`；
`tools` 两元素时**首元素不含** `cache_control`、末元素含。

## 限制（诚实记录）

- **端到端未验证**：尚未用真实 Claude 调用确认第二次请求的
  `cache_read_input_tokens > 0`。单测只能证明「我们发了标记」，不能证明
  「provider 认了这个标记」。下一层的真判据是同一 prompt 连发两次、第二次读缓存。
- **GPT 侧无法用同样方式改善**：OpenAI/Gemini 是服务端隐式缓存，不需要内联标记
  （opencode `packages/ai/README.md:1606` 明确说明其 auto 策略在这两个协议下是 no-op）。
  GPT 侧命中率取决于 provider，我们只能保证前缀稳定。
- 未验证 FastRouter 转售层是否透传 `cache_control`。

## 参考

`F:\github\deepseek\opencode`（HEAD `8e1c6a4`，depth=1）的实现：
`packages/ai/src/cache-policy.ts` 的 `AUTO = { tools: true, system: true, messages: { tail: 1 } }`、
`BREAKPOINT_CAP = 4`，断点放在最后一个工具定义、首个与末个不同 system part、对话尾部。
本实现取其中前两处，未取尾部断点。
