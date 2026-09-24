# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `root-scope:markdown` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent` |
| started (UTC) | 2026-09-24T02:15:49.8784477+00:00 |
| total elapsed (ms) | 3471 |
| cases | 22 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.0909 |
| recall@5 | 0.1591 |
| recall@10 | 0.1591 |
| MRR | 0.1515 |
| precision@5 | 0.0455 |
| precision@10 | 0.0227 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| Markdown | 22 | 0.0909 | 0.1591 | 0.1591 | 0.1515 | 0.0455 | 0.0227 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 22 | 15.39 | 24.643 | 44.527 | 177.29 | 177.29 | 31.82 |
| warm (all later calls) | 88 | 15.437 | 20.782 | 48.14 | 180.132 | 180.132 | 31.48 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 16.4575 | 1 | yes |  |
| 1 | 24.6433 | 16 | yes |  |
| 2 | 17.3509 | 0 | yes |  |
| 3 | 27.6627 | 0 | yes |  |
| 4 | 18.4053 | 0 | yes |  |
| 5 | 16.3247 | 0 | yes |  |
| 6 | 15.3896 | 7 | yes |  |
| 7 | 16.7147 | 0 | yes |  |
| 8 | 35.206 | 9 | yes |  |
| 9 | 34.0207 | 7 | yes |  |
| 10 | 44.5271 | 17 | yes |  |
| 11 | 27.1414 | 20 | yes |  |
| 12 | 26.4671 | 20 | yes |  |
| 13 | 30.6128 | 20 | yes |  |
| 14 | 41.3716 | 2 | yes |  |
| 15 | 34.7052 | 9 | yes |  |
| 16 | 16.7197 | 1 | yes |  |
| 17 | 177.2895 | 0 | yes |  |
| 18 | 24.6547 | 16 | yes |  |
| 19 | 19.7442 | 20 | yes |  |
| 20 | 16.9517 | 0 | yes |  |
| 21 | 17.6433 | 0 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | Markdown | PuddingCodeIndex | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 16.5113 | yes |
| 1 | Symbol | Markdown | InternalsVisibleTo | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 9 | 0 | 24.1853 | yes |
| 2 | Symbol | Markdown | CodeIndexMaintenanceHostedService | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 18.2047 | yes |
| 3 | Symbol | Markdown | IndexExcludePatterns | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 18.8201 | yes |
| 4 | Symbol | Markdown | MemoryRecallService | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 17.1977 | yes |
| 5 | Symbol | Markdown | SearchGrepTool | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 16.6256 | yes |
| 6 | Symbol | Markdown | NoiseDirNames | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 15.8672 | yes |
| 7 | Symbol | Markdown | IndexBasedLanguageServerService | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 17.1835 | yes |
| 8 | Intent | Markdown | why is the gitignore parser dead code | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 7 | 0 | 35.383 | yes |
| 9 | Intent | Markdown | how many different exclude rule sets exist in the repository | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 4 | 0 | 30.9408 | yes |
| 10 | Intent | Markdown | how should the full solution build gate be run after a namespace change | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 5 | 0 | 44.1668 | yes |
| 11 | Intent | Markdown | what does the architecture first principle say about dependency direction | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 27.7293 | yes |
| 12 | Intent | Markdown | where is the retrieval evaluation design described | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 27.5627 | yes |
| 13 | Intent | Markdown | what are the rules for temporary build artifacts | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 29.0727 | yes |
| 14 | Intent | Markdown | how is the index component delivered as an independent component | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 2 | 0 | 42.5573 | yes |
| 15 | Intent | Markdown | what are the U4 slices and in which order are they built | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 7 | 0 | 37.5764 | yes |
| 16 | Crossref | Markdown | PuddingCodeIndex | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 16.7954 | yes |
| 17 | Crossref | Markdown | search_grep | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 174.5634 | yes |
| 18 | Crossref | Markdown | InternalsVisibleTo | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 9 | 0 | 25.8343 | yes |
| 19 | Crossref | Markdown | 组件化交付规程 | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 20.4803 | yes |
| 20 | Crossref | Markdown | IndexBasedLanguageServerService | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 17.4867 | yes |
| 21 | Crossref | Markdown | MemoryRecallService | 0/2 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 18.3163 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | PuddingCodeIndex | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\code_map.md` |
| 1 | InternalsVisibleTo | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0残差glob统一验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\code_map.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Memory\2026-06-12.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\memory\goal-archive-20260818-p0-p1.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\memory\goal-archive-20260913-1915.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\code_map.md` |
| 2 | CodeIndexMaintenanceHostedService | `Docs/Conventions/组件化交付规程.md` | _(none)_ |
| 3 | IndexExcludePatterns | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | _(none)_ |
| 4 | MemoryRecallService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | _(none)_ |
| 5 | SearchGrepTool | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | _(none)_ |
| 6 | NoiseDirNames | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\code_map.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\code_map.md` |
| 7 | IndexBasedLanguageServerService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | _(none)_ |
| 8 | why is the gitignore parser dead code | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\ux-20260911-progressive-disclosure.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\02-tool-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\02-tool-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\10-context-assembly.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\10-context-assembly.md` |
| 9 | how many different exclude rule sets exist in the repository | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\ux-20260911-install-check.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\How-Debuge.md` |
| 10 | how should the full solution build gate be run after a namespace change | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-07-01-memory-v2-f3-worker-scheduling-plan.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\How-Debuge.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\assets\architecture-mermaid-full.md` |
| 11 | what does the architecture first principle say about dependency direction | `Agents.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-07-agent-chat-client-architecture-redesign-des...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\assets\architecture-mermaid-full.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\07架构\29ADR-028记忆图书馆基础设施重构ADR.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\01-query-engine.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\01-query-engine.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\.playwright\package\lib\tools\cli-client\skill\r...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\skillhub-agent-out\.playwright\package\lib\tools\cli-client\skill\...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-test-out\heartbeat-host\.playwright\package\lib\tools\cli-client\skill\r...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\compaction-status\core\.playwright\package\lib\tools\cli-client\sk...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\heartbeat-deploy-20260920\core\.playwright\package\lib\tools\cli-c...` |
| 12 | where is the retrieval evaluation design described | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-05-20-memory-library-infrastructure-refactor-desi...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Features\memory-design\learning-mechanism-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\specs\2026-06-30-hook-system-v2-design.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\skill-retrieval-industry-survey-2026-09-21.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-source-scope.csh...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-source-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-source-scope.typ...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\.playwright\package\lib\tools\cli-client\skill\r...` |
| 13 | what are the rules for temporary build artifacts | `Agents.md` | `E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\plan-version-core\default-data\benchmark-seeds\memory-contract-pac...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\skillhub-agent-out\default-data\benchmark-seeds\memory-contract-pa...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\default-data\benchmark-seeds\memory-contract-pack\material...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\compaction-status\core\default-data\benchmark-seeds\memory-contrac...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.tmp-build\heartbeat-deploy-20260920\core\default-data\benchmark-seeds\memory...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\superpowers\plans\2026-06-05-http-fetch-enhancement.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\07-permission-pipeline.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\ux-20260911-install-check.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\skills\vercel-react-best-practices\SKILL.md` |
| 14 | how is the index component delivered as an independent component | `Docs/Conventions/组件化交付规程.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md` |
| 15 | what are the U4 slices and in which order are they built | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\ux-20260911-target-size.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\ux-20260911-install-check.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\.firecrawl\hermes-prompt-assembly.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\architecture\04-plugin-system.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\claude-reviews-claude\docs\chapters\04-plugin-system.md` |
| 16 | PuddingCodeIndex | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Features/ADR-089-索引组件拆分设计-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\code_map.md` |
| 17 | search_grep | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Reports/ArchReview-GPT6Astra-2026-09-23.md` | _(none)_ |
| 18 | InternalsVisibleTo | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Reports/ADR-089-U0残差glob统一验收-2026-09-13.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Reports\ADR-089-U0残差glob统一验收-2026-09-13.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\code_map.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Docs\Memory\2026-06-12.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.markd...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-docs-scope.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\memory\goal-archive-20260818-p0-p1.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\memory\goal-archive-20260913-1915.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\code_map.md` |
| 19 | 组件化交付规程 | `Docs/Conventions/组件化交付规程.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | `E:\github\AgentNetworkPlan\PuddingAgent\Docs\Conventions\组件化交付规程.md` |
| 20 | IndexBasedLanguageServerService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | _(none)_ |
| 21 | MemoryRecallService | `Docs/Features/ADR-089-检索增强需求落地方案-2026-09-24.md`<br>`Docs/Features/ADR-089-前置设施施工计划-2026-09-23.md` | _(none)_ |
