# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `docs-scope:markdown` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Docs` |
| started (UTC) | 2026-09-24T00:51:06.5211019+00:00 |
| total elapsed (ms) | 505 |
| cases | 20 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.2500 |
| recall@5 | 0.4750 |
| recall@10 | 0.5250 |
| MRR | 0.4238 |
| precision@5 | 0.1300 |
| precision@10 | 0.0700 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| Markdown | 20 | 0.2500 | 0.4750 | 0.5250 | 0.4238 | 0.1300 | 0.0700 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 20 | 2.103 | 4.07 | 8.327 | 8.339 | 8.339 | 4.98 |
| warm (all later calls) | 80 | 1.903 | 4.034 | 8.748 | 13.857 | 13.857 | 4.99 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 4.0702 | 20 | yes |  |
| 1 | 2.1668 | 3 | yes |  |
| 2 | 2.8032 | 2 | yes |  |
| 3 | 2.5095 | 4 | yes |  |
| 4 | 3.245 | 20 | yes |  |
| 5 | 5.171 | 16 | yes |  |
| 6 | 2.103 | 1 | yes |  |
| 7 | 2.415 | 5 | yes |  |
| 8 | 7.0461 | 20 | yes |  |
| 9 | 6.4589 | 20 | yes |  |
| 10 | 8.1237 | 20 | yes |  |
| 11 | 8.1242 | 20 | yes |  |
| 12 | 7.18 | 20 | yes |  |
| 13 | 8.339 | 20 | yes |  |
| 14 | 4.0061 | 20 | yes |  |
| 15 | 8.3271 | 20 | yes |  |
| 16 | 2.6211 | 3 | yes |  |
| 17 | 6.9948 | 20 | yes |  |
| 18 | 3.1508 | 5 | yes |  |
| 19 | 4.7344 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | Markdown | PuddingCodeIndex | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 3.6963 | yes |
| 1 | Symbol | Markdown | InternalsVisibleTo | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 2.315 | yes |
| 2 | Symbol | Markdown | CodeIndexMaintenanceHostedService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.097 | yes |
| 3 | Symbol | Markdown | IndexExcludePatterns | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.1501 | yes |
| 4 | Symbol | Markdown | MemoryRecallService | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 7 | 0 | 3.5734 | yes |
| 5 | Symbol | Markdown | SearchGrepTool | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1429 | 0.0000 | 0.1000 | 0.0000 | 8 | 0 | 3.6659 | yes |
| 6 | Symbol | Markdown | NoiseDirNames | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 1.9029 | yes |
| 7 | Symbol | Markdown | IndexBasedLanguageServerService | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 3.4362 | yes |
| 8 | Intent | Markdown | why is the gitignore parser dead code | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 6.0897 | yes |
| 9 | Intent | Markdown | how many different exclude rule sets exist in the repository | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 6.821 | yes |
| 10 | Intent | Markdown | how should the full solution build gate be run after a namespace change | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 7.2384 | yes |
| 11 | Intent | Markdown | where is the retrieval evaluation design described | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 7.2794 | yes |
| 12 | Intent | Markdown | how is the index component delivered as an independent component | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 6.8339 | yes |
| 13 | Intent | Markdown | what are the U4 slices and in which order are they built | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 7.7828 | yes |
| 14 | Crossref | Markdown | PuddingCodeIndex | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 4 | 0 | 3.9861 | yes |
| 15 | Crossref | Markdown | search_grep | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 7.4129 | yes |
| 16 | Crossref | Markdown | InternalsVisibleTo | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 2.6729 | yes |
| 17 | Crossref | Markdown | 组件化交付规程 | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.8882 | yes |
| 18 | Crossref | Markdown | IndexBasedLanguageServerService | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 4.3087 | yes |
| 19 | Crossref | Markdown | MemoryRecallService | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 7 | 0 | 4.3552 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | PuddingCodeIndex | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-索引组件拆分设计-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md` |
| 1 | InternalsVisibleTo | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0残差glob统一验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Memory\2026-06-12.md` |
| 2 | CodeIndexMaintenanceHostedService | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md` |
| 3 | IndexExcludePatterns | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-索引组件拆分设计-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md` |
| 4 | MemoryRecallService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-05-21-adr-028-memory-library-correction.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-20-memory-library-infrastructure-refactor-desi...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\48ADR-047记忆图书馆知识图谱演进ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\上下文Token效率缓存命中与分级压缩优化设计方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Tasks\2026-08-06-工作总结与下一步计划.md` |
| 5 | SearchGrepTool | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\上下文Token效率缓存命中与分级压缩优化设计方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0审阅与返工意见-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0返工验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0残差glob统一验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\Agent统一检索与渐进展开工具链设计-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\README.md` |
| 6 | NoiseDirNames | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md` |
| 7 | IndexBasedLanguageServerService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\测试套件健康与缺陷类诊断-2026-09-17.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md` |
| 8 | why is the gitignore parser dead code | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\10-context-assembly.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\10-context-assembly.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-06-11-code-intelligence-mvp.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\02-tool-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\02-tool-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\IndepthCoding-Guide\OpenHarness 架构深度解析.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-06-12-code-intelligence-core-mvp.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\16-infrastructure-config.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\16-infrastructure-config.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\DISCLAIMER.md` |
| 9 | how many different exclude rule sets exist in the repository | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\DISCLAIMER.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-30-hook-system-v2-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\IndepthCoding-Guide\OpenHarness 技术架构深度解析.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\IndepthCoding-Guide\OpenHarness 架构深度解析.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\03-coordinator.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\03-coordinator.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\14-ui-state-rendering.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\14-ui-state-rendering.md` |
| 10 | how should the full solution build gate be run after a namespace change | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-07-01-memory-v2-f3-worker-scheduling-plan.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-20-memory-library-infrastructure-refactor-desi...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-07-agent-to-agent-message-fabric-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-18-data-config-e2e-foundation-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-06-agent-first-main-session-chat-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-30-hook-system-v2-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\assets\architecture-mermaid-full.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\04-plugin-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\04-plugin-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-06-runtime-steering-queue-design.md` |
| 11 | where is the retrieval evaluation design described | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-20-memory-library-infrastructure-refactor-desi...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-30-hook-system-v2-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\memory-design\learning-mechanism-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\00-overview.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\overview.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\00-overview.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\skill-retrieval-industry-survey-2026-09-21.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-06-agent-first-main-session-chat-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-07-agent-to-agent-message-fabric-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-16-agent-runtime-skill-filesystem-design.md` |
| 12 | how is the index component delivered as an independent component | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\26ADR-025验收阻塞修复与执行闭环方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\23运行时可观测性闭环与E2E验证基线ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\14-ui-state-rendering.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\14-ui-state-rendering.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-05-24-agent-template-storage-chain.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\14-ui-state-management.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\14-ui-state-management.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-06-06-agent-first-main-session-chat.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\85通用Agent编排交付测试与运维验收图册.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\09-session-persistence.md` |
| 13 | what are the U4 slices and in which order are they built | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\04-plugin-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\04-plugin-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-30-hook-system-v2-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-05-21-memory-library-page-manager.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\05-hook-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\05-hook-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-06-agent-first-main-session-chat-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-06-11-code-intelligence-mvp.md` |
| 14 | PuddingCodeIndex | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Features/ADR-089-索引组件拆分设计-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-索引组件拆分设计-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md` |
| 15 | search_grep | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Reports/ArchReview-GPT6Astra-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\scheduler-noise-closeout-r2.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\skill-portfolio-G1-2026-09-21.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\04工具与技能\Pudding工具系统增强-CodeWhale参考设计.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\50ADR-049代码语义索引与LSP编辑服务ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-04-fact-first-memory-library-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-03-file-search-provider-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\deepseek-reference-architecture-master-plan-2026-08-14.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\deepseek-tui-research\研究报告.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\AgentHarness兼容与工具调用效率修复设计方案.md` |
| 16 | InternalsVisibleTo | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Reports/ADR-089-U0残差glob统一验收-2026-09-13.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0残差glob统一验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Memory\2026-06-12.md` |
| 17 | 组件化交付规程 | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md` |
| 18 | IndexBasedLanguageServerService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-前置设施施工计划-2026-09-23.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\测试套件健康与缺陷类诊断-2026-09-17.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md` |
| 19 | MemoryRecallService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-05-21-adr-028-memory-library-correction.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-20-memory-library-infrastructure-refactor-desi...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\30ADR-029记忆图书馆ADR-028纠偏与验收闭环方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\48ADR-047记忆图书馆知识图谱演进ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\上下文Token效率缓存命中与分级压缩优化设计方案.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Tasks\2026-08-06-工作总结与下一步计划.md` |
