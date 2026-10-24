---
title: 2026-10-24 业界 Agent Harness 实践证据（上下文 / 工具 / 成本）
author: hyfree
date: 2026-10-24
last_reviewed: 2026-10-24
status: active
description: "汇总 Anthropic / Google ADK / Manus / LangChain / Cognition / OpenAI 等 14 个来源的 38 条可核验实践，每条带 URL 与原文引文，并映射到 PuddingAgent 六条诉求；用于给 harness 改动提供外部参照，而不是按直觉选优化点。"
categories: [reports]
tags: [context-engineering, harness, token-economics, tool-orchestration, evidence]
related_docs: [Docs/14_reports/2026-10-24-DeepSeek-tokenizer调研.md, Docs/14_reports/2026-10-24-六条诉求收口审计.md]
related_files: [Source/PuddingCore/Platform/LlmOptions.cs, Source/PuddingRuntime/Services/AgentExecution/AgentExecutionService.Buffered.cs]
slug: report-2026-10-24-harness-practice-evidence
draft: false
---

# 业界 harness 实践证据

> 范围：Anthropic / OpenAI / Google ADK / Cognition / LangChain / Cursor / Manus / Chroma / HumanLayer 的公开资料。
> 只收录能给出可访问 URL 的条目；引文为原文照抄（英文源保持英文，Cursor 该页仅有中文版，故照抄中文）。
> 已知的四条（compaction / tool-result clearing / memory / 三者对应三类增长）不再重复。

## 一、上下文与压缩

- **压缩带 overlap + retention 双旋钮** — 来源：《Compress agent context for performance》(https://adk.dev/context/compaction/)；引文："As part of the configuration, you must specify a compaction interval and overlap size (Python/Java) or a token threshold and event retention size (TypeScript)"；"The number of preceding invocations to include from the end of the last compacted range. This creates an overlap between consecutive compacted summaries, maintaining context."；解决：压缩后只剩一条摘要、相邻窗口之间语义断裂，压缩越频繁信息损失越大。
- **压缩由 token 阈值而非固定轮数触发** — 来源：《EventsCompactionConfig》(https://adk.dev/context/compaction/)；引文："The number of tokens above which compaction will be triggered. If null, no token limit will be enforced. It will trigger compaction within the invocation."；解决：按轮数压缩导致内容长短不一时要么压得太早、要么爆窗。
- **自动压缩在 95% 才触发** — 来源：《Context Engineering》(https://www.langchain.com/blog/context-engineering-for-agents)；引文："Claude Code runs “auto-compact” after you exceed 95% of the context window and it will summarize the full trajectory of user-agent interactions."；解决：过早压缩把可用空间反复扔掉，压缩次数暴涨。
- **可恢复压缩：只丢内容、保留指针** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："Our compression strategies are always designed to be restorable."；"the content of a web page can be dropped from the context as long as the URL is preserved"；解决：不可逆压缩一旦压掉后面要用的观察，只能重跑工具或猜。
- **把上下文当"注意力预算"而非容量** — 来源：《Effective context engineering for AI agents》(https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)；引文："good context engineering means finding the smallest possible set of high-signal tokens that maximize the likelihood of some desired outcome."；解决：只盯窗口上限，忽略 token 越多推理越钝。
- **Context Rot 实证** — 来源：《Context Rot: How Increasing Input Tokens Impacts LLM Performance》(https://www.trychroma.com/research/context-rot)；引文："As context length increases, performance consistently degrades across all models."；解决：以为"窗口没满就没事"，实际上性能早已开始退化。
- **用 todo.md 把目标复述到上下文末尾** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："By constantly rewriting the todo list, Manus is reciting its objectives into the end of the context."；解决：长循环里目标漂到上下文中段，被 lost-in-the-middle 吞掉。
- **保留失败轨迹，不清理错误** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："one of the most effective ways to improve agent behavior is deceptively simple: leave the wrong turns in the context."；解决：把报错擦干净换来的是同一个错误被反复重试。

## 二、工具结果与检索

- **少而整合的工具，而非端点级封装** — 来源：《Writing effective tools for AI agents—using AI agents》(https://www.anthropic.com/engineering/writing-tools-for-agents)；引文："More tools don’t always lead to better outcomes."；"Tools should enable agents to subdivide and solve tasks in much the same way that a human would, given access to the same underlying resources, and simultaneously reduce the context that would have otherwise been consumed by intermediate outputs."；解决：一个 API 一个工具，中间结果全灌进上下文。
- **响应详略由模型选：concise 省 2/3 token** — 来源：《Writing effective tools for AI agents—using AI agents》(https://www.anthropic.com/engineering/writing-tools-for-agents)；引文："Here’s an example of a detailed tool response (206 tokens)… Here’s an example of a concise tool response (72 tokens)… we use ~⅓ of the tokens with 'concise' tool responses."；解决：每次调用都返回全量字段和 UUID，token 花在不需要的信息上。
- **默认分页/过滤/截断，且给硬上限** — 来源：《Writing effective tools for AI agents—using AI agents》(https://www.anthropic.com/engineering/writing-tools-for-agents)；引文："We suggest implementing some combination of pagination, range selection, filtering, and/or truncation with sensible default parameter values for any tool responses that could use up lots of context. For Claude Code, we restrict tool responses to 25,000 tokens by default."；解决：一次工具调用就把半个窗口吃满，逼迫频繁压缩。
- **错误响应写成可执行的改进指令** — 来源：《Writing effective tools for AI agents—using AI agents》(https://www.anthropic.com/engineering/writing-tools-for-agents)；引文："you can prompt-engineer your error responses to clearly communicate specific and actionable improvements, rather than opaque error codes or tracebacks."；解决：模型看不懂报错，原地重试同一参数。
- **渐进披露：工具即文件，按需读取定义** — 来源：《Code execution with MCP: Building more efficient agents》(https://www.anthropic.com/engineering/code-execution-with-mcp)；引文："This reduces the token usage from 150,000 tokens to 2,000 tokens—a time and cost saving of 98.7%."；"Presenting tools as code on a filesystem allows models to read tool definitions on-demand, rather than reading them all up-front."；解决：几十个工具定义常驻上下文，还没开始干活就烧掉几万 token。
- **在沙箱里先过滤数据再回模型** — 来源：《Code execution with MCP: Building more efficient agents》(https://www.anthropic.com/engineering/code-execution-with-mcp)；引文："The agent sees five rows instead of 10,000."；解决：把十万行原始数据搬进上下文再让模型自己筛。
- **给工具描述本身做检索（RAG over tools）** — 来源：《Context Engineering》(https://www.langchain.com/blog/context-engineering-for-agents)；引文："Some recent papers have shown that this improve tool selection accuracy by 3-fold."；解决：工具描述重叠导致选错工具，一次错选后面全错。
- **代码检索不是纯 embedding：grep + 图谱 + rerank** — 来源：《Context Engineering》(https://www.langchain.com/blog/context-engineering-for-agents)；引文："Indexing code ≠ context retrieval … embedding search becomes unreliable as a retrieval heuristic as the size of the codebase grows … we must rely on a combination of techniques like grep/file search, knowledge graph based retrieval, and … a re-ranking step"；解决：单一向量检索在仓库变大后召回退化，模型反复找不到东西。
- **Just-in-time 检索替代预先塞满** — 来源：《Effective context engineering for AI agents》(https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents)；引文："primitives like glob and grep allow it to navigate its environment and retrieve files just-in-time, effectively bypassing the issues of stale indexing and complex syntax trees."；解决：预检索把大量无关片段塞进上下文，既贵又干扰判断。
- **索引增量同步 + 块级 embedding 缓存** — 来源：《Securely indexing large codebases》(https://cursor.com/blog/secure-codebase-indexing)（该页当前仅提供中文版，照抄中文）；引文："Cursor 使用 默克尔树 来构建它对代码库的初始视图，这使它可以在不重新处理所有内容的情况下，精确检测哪些文件和目录发生了变化。"；"Cursor 按块内容缓存 embeddings。未变化的块会命中缓存"；解决：每次改动全量重建索引，首查询等待数小时、检索长期不可用。

## 三、成本与 token 计量

- **KV-cache 命中率是生产级 agent 的第一指标** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："If I had to choose just one metric, I'd argue that the KV-cache hit rate is the single most important metric for a production-stage AI agent."；"In Manus, for example, the average input-to-output token ratio is around 100:1."；解决：agent 是重输入/轻输出形态，不度量缓存命中就等于不度量成本。
- **前缀稳定、append-only、显式缓存断点** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："A common mistake is including a timestamp—especially one precise to the second—at the beginning of the system prompt."；"Mark cache breakpoints explicitly when needed."；解决：系统提示里塞时间戳/动态字段，每次请求全量失效。
- **缓存读取 0.1x、命中免费续期、前缀顺序固定** — 来源：《Prompt caching》(https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching)；引文："Cache read tokens are 0.1 times the base input tokens price"；"The cache is refreshed for no additional cost each time the cached content is used."；"Prompt caching references the entire prompt: tools, system, and messages (in that order), up to and including the block designated with cache_control."；解决：不知道缓冲 TTL 与顺序约束，缓存写了却用不上。
- **隐式缓存默认开启 + 增量门槛 + 命中量可计量** — 来源：《Context caching》(https://ai.google.dev/gemini-api/docs/caching)；引文："Implicit caching is enabled by default for all Gemini 2.5 and newer models."；"Gemini caches the full previous request only if the new request adds at least 2,050 tokens."；"You can see the number of tokens which were cache hits in the response object's usage.total_cached_tokens"；解决：不懂增量门槛，小步追加导致每次都在重算前缀。
- **token 用量本身就是性能变量（也是成本大头）** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："token usage by itself explains 80% of the variance"；"agents typically use about 4× more tokens than chat interactions, and multi-agent systems use about 15× more tokens than chats."；解决：把 token 当纯成本项，忽略它同时是效果杠杆，也没做"值不值"的判断。

## 四、评测与失败分析

- **20 条真实查询就够开工** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："We started with a set of about 20 queries representing real usage patterns."；解决：等"大而全"的 eval，结果长期没有反馈闭环。
- **单一 LLM judge 出 0–1 分 + pass/fail** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："we found that a single LLM call with a single prompt outputting scores from 0.0-1.0 and a pass-fail grade was the most consistent and aligned with human judgements."；解决：多 judge 拼装反而不稳定，无法比较版本。
- **读 transcript 才认分数，并盯 eval 饱和** — 来源：《Demystifying evals for AI agents》(https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents)；引文："we do not take eval scores at face value until someone digs into the details of the eval and reads some transcripts."；"An eval at 100% tracks regressions but provides no signal for improvement."；解决：看数字不看轨迹，把评分器缺陷/题目歧义当成能力结论。
- **全链路 production tracing + 决策模式观测** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："Adding full production tracing let us diagnose why agents failed and fix issues systematically."；解决：只知道"没找到"，不知道是查询烂、来源差还是工具挂了。
- **SDK 默认开 tracing，span 粒度到 turn/tool** — 来源：《Tracing》(https://openai.github.io/openai-agents-python/tracing/)；引文："Tracing is enabled by default."；"Each model turn is wrapped in a turn_span()"；"Function tool calls are each wrapped in a function_span()"；解决：失败分析只能靠自述，缺少可回放的最小单元。
- **把 agent 切成 3–10 步的小单元** — 来源：《10. Small, Focused Agents》(https://github.com/humanlayer/12-factor-agents/blob/main/content/factor-10-small-focused-agents.md)；引文："By keeping agents focused on specific domains with 3-10, maybe 20 steps max, we keep context windows manageable and LLM performance high."；解决：单体 agent 步数无限增长，上下文与失败率一起爆。

## 五、子代理与隔离

- **委派任务书四要素：目标/输出格式/工具与来源/边界** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："Each subagent needs an objective, an output format, guidance on the tools and sources to use, and clear task boundaries."；解决：任务描述含糊导致子代理重复劳动或集体漏项。
- **把 effort 规则写进提示词，按复杂度缩放** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："Simple fact-finding requires just 1 agent with 3-10 tool calls, direct comparisons might need 2-4 subagents with 10-15 calls each, and complex research might use more than 10 subagents with clearly divided responsibilities."；解决：简单问题起 50 个子代理，复杂问题反而投入不足。
- **共享完整 trace，而不只是单条消息** — 来源：《Don’t Build Multi-Agents》(https://cognition.com/blog/dont-build-multi-agents)；引文："Share context, and share full agent traces, not just individual messages"；"Actions carry implicit decisions, and conflicting decisions carry bad results"；解决：子代理各自基于不同假设动手，产物拼不起来。
- **验证者用干净上下文（更短的上下文更聪明）** — 来源：《Multi-Agents: What's Actually Working》(https://cognition.com/blog/multi-agents-working)；引文："having a clean context makes the agent smarter because of the math of attention"；解决：让审阅者继承实现者的长上下文，它 inherits 同样的盲点。
- **在 agent 边界处做摘要交接** — 来源：《Context Engineering》(https://www.langchain.com/blog/context-engineering-for-agents)；引文："Cognition mentioned summarization at agent-agent boundaries to reduce tokens during knowledge hand-off."；解决：把整段轨迹原样传给下一个 agent，token 与噪声一起翻倍。
- **隔离即拆分：每个子代理自带工具/指令/窗口** — 来源：《Context Engineering》(https://www.langchain.com/blog/context-engineering-for-agents)；引文："Each agent has a specific set of tools, instructions, and its own context window."；解决：所有子任务挤在一个窗口里互相污染。

## 六、安全与自治边界

- **掩码工具而非移除工具** — 来源：《Context Engineering for AI Agents: Lessons from Building Manus》(https://manus.im/en/blog/Context-Engineering-for-AI-Agents-Lessons-from-Building-Manus)；引文："Rather than removing tools, it masks the token logits during decoding to prevent (or enforce) the selection of certain actions based on the current context."；解决：动态增删工具既打破缓存，又让模型引用消失的工具定义而乱造参数。
- **护栏用阻塞模式，恶意输入不烧贵模型** — 来源：《Guardrails》(https://openai.github.io/openai-agents-python/guardrails/)；引文："Blocking execution guarantees that the expensive model does not start"；"If the guardrail tripwire is triggered, the agent never executes, preventing token consumption and tool execution."；解决：先跑再说、事后拦截，token 与副作用都已经发生。
- **敏感数据在沙箱内 tokenize，永不进模型上下文** — 来源：《Code execution with MCP: Building more efficient agents》(https://www.anthropic.com/engineering/code-execution-with-mcp)；引文："The MCP client intercepts the data and tokenizes PII before it reaches the model"；解决：PII 直通上下文，既泄露又占 token。
- **彩虹部署 + 断点续跑，不打断运行中的 agent** — 来源：《How we built our multi-agent research system》(https://www.anthropic.com/engineering/multi-agent-research-system)；引文："we use rainbow deployments to avoid disrupting running agents, by gradually shifting traffic from old to new versions while keeping both running simultaneously."；"we built systems that can resume from where the agent was when the errors occurred."；解决：发版即杀掉进行中的长任务，重跑重付费。

## 与我们六条诉求的映射

| 诉求 | 直接对应的实践 |
| --- | --- |
| ① 压缩太频繁 | ADK token 阈值触发；LangChain/Claude Code 95% 才压缩；可恢复压缩（只丢内容留指针）；3–10 步小 agent |
| ② 压缩后空间小 | ADK overlap + event_retention；边界摘要交接；可恢复压缩；注意力预算/最小高信号集；clean-context 验证者；Context Rot |
| ③ 工具调用省 token | 少而整合的工具；concise/detailed 响应（~⅓）；默认分页/过滤/截断 + 25k 上限；渐进披露（150k→2k）；沙箱内过滤（10,000→5 行） |
| ④ 检索优化 | 工具描述 RAG（3x）；grep + 图谱 + rerank 组合；just-in-time 检索；Cursor 索引增量与块级 embedding 缓存 |
| ⑤ 低效调用 | 错误响应写成可执行指令；effort 按复杂度缩放；干净上下文的验证者；全链路 tracing；SDK 默认 tracing；护栏阻塞模式；3–10 步上限 |
| ⑥ 缓存命中 | KV-cache 命中率为第一指标；前缀稳定 + append-only + 显式断点；Anthropic 读 0.1x/免费续期/前缀顺序；Gemini 隐式缓存与 2,050 token 增量门槛；掩码而非移除工具 |
