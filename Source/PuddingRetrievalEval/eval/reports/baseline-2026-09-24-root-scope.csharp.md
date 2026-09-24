# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `root-scope:csharp` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent` |
| started (UTC) | 2026-09-24T02:15:42.2492207+00:00 |
| total elapsed (ms) | 4901 |
| cases | 36 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.6389 |
| recall@5 | 0.6944 |
| recall@10 | 0.7222 |
| MRR | 0.7389 |
| precision@5 | 0.1611 |
| precision@10 | 0.0833 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 36 | 0.6389 | 0.6944 | 0.7222 | 0.7389 | 0.1611 | 0.0833 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 36 | 14.711 | 16.232 | 71.7 | 88.606 | 88.606 | 26.99 |
| warm (all later calls) | 144 | 14.764 | 16.689 | 73.314 | 84.08 | 86.576 | 27.27 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 14.8027 | 20 | yes |  |
| 1 | 15.0496 | 20 | yes |  |
| 2 | 15.3546 | 20 | yes |  |
| 3 | 15.8692 | 20 | yes |  |
| 4 | 14.9174 | 20 | yes |  |
| 5 | 15.7497 | 20 | yes |  |
| 6 | 15.8001 | 20 | yes |  |
| 7 | 15.9578 | 20 | yes |  |
| 8 | 15.4944 | 20 | yes |  |
| 9 | 15.0619 | 20 | yes |  |
| 10 | 16.2587 | 20 | yes |  |
| 11 | 17.4294 | 20 | yes |  |
| 12 | 16.8171 | 20 | yes |  |
| 13 | 16.3077 | 20 | yes |  |
| 14 | 15.8731 | 20 | yes |  |
| 15 | 15.5852 | 20 | yes |  |
| 16 | 15.6028 | 20 | yes |  |
| 17 | 15.881 | 20 | yes |  |
| 18 | 15.5868 | 20 | yes |  |
| 19 | 14.7107 | 20 | yes |  |
| 20 | 57.255 | 1 | yes |  |
| 21 | 32.8864 | 3 | yes |  |
| 22 | 88.606 | 1 | yes |  |
| 23 | 31.2275 | 12 | yes |  |
| 24 | 71.6999 | 2 | yes |  |
| 25 | 63.7544 | 12 | yes |  |
| 26 | 53.9336 | 6 | yes |  |
| 27 | 48.5587 | 5 | yes |  |
| 28 | 53.0126 | 0 | yes |  |
| 29 | 57.1728 | 2 | yes |  |
| 30 | 15.9286 | 20 | yes |  |
| 31 | 16.7156 | 20 | yes |  |
| 32 | 16.2321 | 20 | yes |  |
| 33 | 16.7697 | 20 | yes |  |
| 34 | 16.2924 | 18 | yes |  |
| 35 | 17.4665 | 13 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | SearchGrepTool | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 14.7758 | yes |
| 1 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 16.3565 | yes |
| 2 | Symbol | CSharp | LuceneSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.2471 | yes |
| 3 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 17.4158 | yes |
| 4 | Symbol | CSharp | IFullTextSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.6475 | yes |
| 5 | Symbol | CSharp | FullTextIndexOptions | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 16.1217 | yes |
| 6 | Symbol | CSharp | RetrievalGlobMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 16.8618 | yes |
| 7 | Symbol | CSharp | RetrievalMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.7591 | yes |
| 8 | Symbol | CSharp | SearchAttemptLedger | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.7362 | yes |
| 9 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.9091 | yes |
| 10 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.9566 | yes |
| 11 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 16.0281 | yes |
| 12 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 17.0873 | yes |
| 13 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 17.0083 | yes |
| 14 | Symbol | CSharp | ComponentBoundaryTests | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 16.6576 | yes |
| 15 | Symbol | CSharp | IndexBasedLanguageServerService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.6636 | yes |
| 16 | Symbol | CSharp | CodeQueryService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.4585 | yes |
| 17 | Symbol | CSharp | RoslynCSharpIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.675 | yes |
| 18 | Symbol | CSharp | PlainTextExtractor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.1074 | yes |
| 19 | Symbol | CSharp | JiebaAnalyzer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.299 | yes |
| 20 | Intent | CSharp | where is the list of directories the grep tool skips by default | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 51.4864 | yes |
| 21 | Intent | CSharp | code that reads gitignore files and turns them into exclude patterns | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 36.2491 | yes |
| 22 | Intent | CSharp | how does the full text engine parse a query against content and file name | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 86.5756 | yes |
| 23 | Intent | CSharp | which sqlite implementation stores code symbols and relations | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 7 | 0 | 30.3776 | yes |
| 24 | Intent | CSharp | how are file system changes coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 2 | 0 | 73.3135 | yes |
| 25 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1000 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 66.4128 | yes |
| 26 | Intent | CSharp | where is the project root detected for the code index scope | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 5 | 0 | 54.6851 | yes |
| 27 | Intent | CSharp | how is the code index maintenance loop driven by the host | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 4 | 0 | 47.7431 | yes |
| 28 | Intent | CSharp | where does the code query service run a symbol search | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 50.6741 | yes |
| 29 | Intent | CSharp | which file extracts plain text content for full text indexing | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 2 | 0 | 58.0893 | yes |
| 30 | Crossref | CSharp | RetrievalGlobMatcher | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.8001 | yes |
| 31 | Crossref | CSharp | IndexExcludePatterns | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.6472 | yes |
| 32 | Crossref | CSharp | JiebaAnalyzer | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.5885 | yes |
| 33 | Crossref | CSharp | ICodeIndexStore | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 15.3865 | yes |
| 34 | Crossref | CSharp | IFileContentExtractor | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 15.5566 | yes |
| 35 | Crossref | CSharp | ILanguageServerService | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 5 | 0 | 17.0346 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | SearchGrepTool | `Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Search\SearchGrepTool.cs` |
| 1 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 2 | LuceneSearchEngine | `Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs` |
| 3 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 4 | IFullTextSearchEngine | `Source/PuddingFullTextIndex/Contracts/IFullTextSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Contracts\IFullTextSearchEngine.cs` |
| 5 | FullTextIndexOptions | `Source/PuddingFullTextIndex/FullTextIndexOptions.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\FullTextIndexOptions.cs` |
| 6 | RetrievalGlobMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalGlobMatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalGlobMatcher.cs` |
| 7 | RetrievalMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalMatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalMatcher.cs` |
| 8 | SearchAttemptLedger | `Source/PuddingRuntime/Services/Search/SearchAttemptLedger.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Search\SearchAttemptLedger.cs` |
| 9 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs` |
| 10 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs` |
| 11 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 12 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs` |
| 13 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 14 | ComponentBoundaryTests | `Source/PuddingCodeIndexTests/ComponentBoundaryTests.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\ComponentBoundaryTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\ComponentBoundaryTests.cs` |
| 15 | IndexBasedLanguageServerService | `Source/PuddingCodeIntelligence/Lsp/IndexBasedLanguageServerService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs` |
| 16 | CodeQueryService | `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Services\CodeQueryService.cs` |
| 17 | RoslynCSharpIndexer | `Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\CSharp\RoslynCSharpIndexer.cs` |
| 18 | PlainTextExtractor | `Source/PuddingFullTextIndex/Infrastructure/Search/PlainTextExtractor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\PlainTextExtractor.cs` |
| 19 | JiebaAnalyzer | `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Text\JiebaAnalyzer.cs` |
| 20 | where is the list of directories the grep tool skips by default | `Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\RetrievalMetricsTests.cs` |
| 21 | code that reads gitignore files and turns them into exclude patterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Abstractions\IAgentRuntimeProfileResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\WarmPrefixCompaction.cs` |
| 22 | how does the full text engine parse a query against content and file name | `Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\SmartWorkflow\SmartExploreTool.cs` |
| 23 | which sqlite implementation stores code symbols and relations | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PluginManifestOnlyTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\CSharp\RoslynCSharpIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\TypeScript\TypeScriptIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\Orchestration\SqliteAgentOrchestrationStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreTests.cs` |
| 24 | how are file system changes coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 25 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\DesktopParentProcessMonitor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\TokenUsageRebuildService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\ConversationAcceptanceStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexRemovalCorrectnessTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs` |
| 26 | where is the project root detected for the code index scope | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexRemovalCorrectnessTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Core\GitSnapshotService.cs` |
| 27 | how is the code index maintenance loop driven by the host | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\GitCommitArgsSerializationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Tests\PuddingHost.Tests\Hosting\CodeIndexMaintenanceHostCompositionTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\MessageFabric\MessageFabricSchemaBootstrapper.cs` |
| 28 | where does the code query service run a symbol search | `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs` | _(none)_ |
| 29 | which file extracts plain text content for full text indexing | `Source/PuddingFullTextIndex/Infrastructure/Search/PlainTextExtractor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\src\HarnessAgent\Core\Connectors\Feishu\MessageMapper.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\TestScripts\PuddingWsTest\Program.cs` |
| 30 | RetrievalGlobMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalGlobMatcher.cs`<br>`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalGlobMatcher.cs` |
| 31 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 32 | JiebaAnalyzer | `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Text\JiebaAnalyzer.cs` |
| 33 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 34 | IFileContentExtractor | `Source/PuddingFullTextIndex/Contracts/IFileContentExtractor.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Contracts\IFileContentExtractor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\PlainTextExtractor.cs` |
| 35 | ILanguageServerService | `Source/PuddingCodeIntelligence/Contracts/ILanguageServerService.cs`<br>`Source/PuddingCodeIntelligence/Lsp/IndexBasedLanguageServerService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Contracts\ILanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\NoOpLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\DependencyInjectionTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\DependencyInjection.cs` |
