# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `source-scope:csharp` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source` |
| started (UTC) | 2026-09-24T00:51:02.1514024+00:00 |
| total elapsed (ms) | 1488 |
| cases | 36 |
| failed calls | 0 |
| repetition-stable | yes |

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.6389 |
| recall@5 | 0.6944 |
| recall@10 | 0.6944 |
| MRR | 0.7335 |
| precision@5 | 0.1611 |
| precision@10 | 0.0806 |
| noiseRate@10 | 0.0086 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 36 | 0.6389 | 0.6944 | 0.6944 | 0.7335 | 0.1611 | 0.0806 | 0.0086 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 36 | 2.594 | 6.078 | 25.664 | 26.502 | 26.502 | 8.41 |
| warm (all later calls) | 144 | 2.175 | 6.699 | 23.649 | 28.945 | 30.291 | 8.18 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 7.2144 | 20 | yes |  |
| 1 | 7.3279 | 20 | yes |  |
| 2 | 6.0784 | 20 | yes |  |
| 3 | 7.6616 | 20 | yes |  |
| 4 | 6.5162 | 20 | yes |  |
| 5 | 6.7037 | 20 | yes |  |
| 6 | 6.5151 | 20 | yes |  |
| 7 | 7.8499 | 20 | yes |  |
| 8 | 10.5396 | 20 | yes |  |
| 9 | 5.6665 | 20 | yes |  |
| 10 | 3.3354 | 20 | yes |  |
| 11 | 4.018 | 20 | yes |  |
| 12 | 4.4765 | 20 | yes |  |
| 13 | 3.6759 | 20 | yes |  |
| 14 | 3.4497 | 20 | yes |  |
| 15 | 3.7114 | 20 | yes |  |
| 16 | 2.861 | 20 | yes |  |
| 17 | 3.0314 | 20 | yes |  |
| 18 | 5.5077 | 20 | yes |  |
| 19 | 5.8992 | 20 | yes |  |
| 20 | 26.5025 | 9 | yes |  |
| 21 | 12.2535 | 20 | yes |  |
| 22 | 25.6638 | 20 | yes |  |
| 23 | 11.4218 | 20 | yes |  |
| 24 | 18.7244 | 20 | yes |  |
| 25 | 19.9649 | 20 | yes |  |
| 26 | 19.8087 | 20 | yes |  |
| 27 | 16.8583 | 20 | yes |  |
| 28 | 11.5409 | 1 | yes |  |
| 29 | 7.8572 | 20 | yes |  |
| 30 | 3.2846 | 20 | yes |  |
| 31 | 2.5939 | 20 | yes |  |
| 32 | 5.1816 | 20 | yes |  |
| 33 | 2.6425 | 20 | yes |  |
| 34 | 2.7728 | 18 | yes |  |
| 35 | 3.7158 | 13 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | SearchGrepTool | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.9466 | yes |
| 1 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.4513 | yes |
| 2 | Symbol | CSharp | LuceneSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.2824 | yes |
| 3 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.8106 | yes |
| 4 | Symbol | CSharp | IFullTextSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.233 | yes |
| 5 | Symbol | CSharp | FullTextIndexOptions | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.8364 | yes |
| 6 | Symbol | CSharp | RetrievalGlobMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.7492 | yes |
| 7 | Symbol | CSharp | RetrievalMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.0477 | yes |
| 8 | Symbol | CSharp | SearchAttemptLedger | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 9.1807 | yes |
| 9 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 8.1826 | yes |
| 10 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.6194 | yes |
| 11 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 4.2994 | yes |
| 12 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.7475 | yes |
| 13 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.0774 | yes |
| 14 | Symbol | CSharp | ComponentBoundaryTests | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.0739 | yes |
| 15 | Symbol | CSharp | IndexBasedLanguageServerService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.0265 | yes |
| 16 | Symbol | CSharp | CodeQueryService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.019 | yes |
| 17 | Symbol | CSharp | RoslynCSharpIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.96 | yes |
| 18 | Symbol | CSharp | PlainTextExtractor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.9465 | yes |
| 19 | Symbol | CSharp | JiebaAnalyzer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.7362 | yes |
| 20 | Intent | CSharp | where is the list of directories the grep tool skips by default | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.1111 | 9 | 1 | 23.6487 | yes |
| 21 | Intent | CSharp | code that reads gitignore files and turns them into exclude patterns | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.1000 | 10 | 1 | 12.6091 | yes |
| 22 | Intent | CSharp | how does the full text engine parse a query against content and file name | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 26.2406 | yes |
| 23 | Intent | CSharp | which sqlite implementation stores code symbols and relations | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 9.0642 | yes |
| 24 | Intent | CSharp | how are file system changes coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.1000 | 10 | 1 | 18.3132 | yes |
| 25 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 0.0000 | 0.0000 | 0.0714 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 14.0072 | yes |
| 26 | Intent | CSharp | where is the project root detected for the code index scope | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 17.2095 | yes |
| 27 | Intent | CSharp | how is the code index maintenance loop driven by the host | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 16.6342 | yes |
| 28 | Intent | CSharp | where does the code query service run a symbol search | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 1 | 0 | 11.9734 | yes |
| 29 | Intent | CSharp | which file extracts plain text content for full text indexing | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 8.3892 | yes |
| 30 | Crossref | CSharp | RetrievalGlobMatcher | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.1863 | yes |
| 31 | Crossref | CSharp | IndexExcludePatterns | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.7841 | yes |
| 32 | Crossref | CSharp | JiebaAnalyzer | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.6947 | yes |
| 33 | Crossref | CSharp | ICodeIndexStore | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.5779 | yes |
| 34 | Crossref | CSharp | IFileContentExtractor | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 3.3137 | yes |
| 35 | Crossref | CSharp | ILanguageServerService | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.0000 | 5 | 0 | 3.56 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | SearchGrepTool | `Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Search\SearchGrepTool.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\SearchGrepToolTests.cs` |
| 1 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 2 | LuceneSearchEngine | `Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndexTests\FullTextIndexTests.cs` |
| 3 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 4 | IFullTextSearchEngine | `Source/PuddingFullTextIndex/Contracts/IFullTextSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Contracts\IFullTextSearchEngine.cs` |
| 5 | FullTextIndexOptions | `Source/PuddingFullTextIndex/FullTextIndexOptions.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\FullTextIndexOptions.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs` |
| 6 | RetrievalGlobMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalGlobMatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalGlobMatcher.cs` |
| 7 | RetrievalMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalMatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalMatcher.cs` |
| 8 | SearchAttemptLedger | `Source/PuddingRuntime/Services/Search/SearchAttemptLedger.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Search\SearchAttemptLedger.cs` |
| 9 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexer.Cli\Program.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\DependencyInjection.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndexFixture.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\CodeIntelligenceFixture.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Extensions\PuddingServiceCollectionExtensions.Platform.cs` |
| 10 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 11 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\MaintenanceHarness.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...` |
| 12 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\MaintenanceHarness.cs` |
| 13 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 14 | ComponentBoundaryTests | `Source/PuddingCodeIndexTests/ComponentBoundaryTests.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\ComponentBoundaryTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\ComponentBoundaryTests.cs` |
| 15 | IndexBasedLanguageServerService | `Source/PuddingCodeIntelligence/Lsp/IndexBasedLanguageServerService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs` |
| 16 | CodeQueryService | `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Services\CodeQueryService.cs` |
| 17 | RoslynCSharpIndexer | `Source/PuddingCodeIntelligence/CSharp/RoslynCSharpIndexer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\CSharp\RoslynCSharpIndexer.cs` |
| 18 | PlainTextExtractor | `Source/PuddingFullTextIndex/Infrastructure/Search/PlainTextExtractor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\PlainTextExtractor.cs` |
| 19 | JiebaAnalyzer | `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Text\JiebaAnalyzer.cs` |
| 20 | where is the list of directories the grep tool skips by default | `Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\RetrievalMetricsTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Agents\AgentWorkspaceGuard.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Debug\DebugBackendLauncher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\GitCommitArgsSerializationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Core\PromptTemplateLoader.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\GoalReadToolTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Bootstrap\DesktopBootstrapSignalParser.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexMaintenanceTestDoubl...` |
| 21 | code that reads gitignore files and turns them into exclude patterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Abstractions\IAgentRuntimeProfileResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\WarmPrefixCompaction.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\SseFrameBatchPump.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PluginManifestOnlyTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Debug\FrontendBuildDeployService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\AgentChat\AgentRunProjectionService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\AgentRuntimeProfileResolver.cs` |
| 22 | how does the full text engine parse a query against content and file name | `Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\SmartWorkflow\SmartExploreTool.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\ComponentBoundaryTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexMaintenanceServiceTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Storage\StorageMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files\FileTools.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Platform\BuiltInAgentTemplates.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Configuration\PuddingBuildOutputSync.cs` |
| 23 | which sqlite implementation stores code symbols and relations | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PluginManifestOnlyTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\CSharp\RoslynCSharpIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\TypeScript\TypeScriptIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\Orchestration\SqliteAgentOrchestrationStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Swarm\ContractManagerTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Storage\StorageMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Configuration\DesktopBootstrapSettings.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\WarmPrefixCompaction.cs` |
| 24 | how are file system changes coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Bootstrap\DesktopBootstrapSignalService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndexTests\FullTextIndexTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Core\PromptTemplateLoader.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingController\Services\InMemorySessionRepository.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\ViewModels\SettingsViewModel.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Debug\FrontendBuildDeployService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationTestDoubl...` |
| 25 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\DesktopParentProcessMonitor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\TokenUsageRebuildService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\ConversationAcceptanceStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexRemovalCorrectnessTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Bootstrap\DesktopBootstrapSignalService.cs` |
| 26 | where is the project root detected for the code index scope | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexRemovalCorrectnessTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\TypeScript\TypeScriptIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Core\GitSnapshotService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Abstractions\IGitSnapshot.cs` |
| 27 | how is the code index maintenance loop driven by the host | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\GitCommitArgsSerializationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\MessageFabric\MessageFabricSchemaBootstrapper.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\ToolApproval.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Extensions\PuddingServiceCollectionExtensions.Runtime.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\DesktopLifecycleEndpointExtensions.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationServiceTe...` |
| 28 | where does the code query service run a symbol search | `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeQueryTools.cs` |
| 29 | which file extracts plain text content for full text indexing | `Source/PuddingFullTextIndex/Infrastructure/Search/PlainTextExtractor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files\FilePatchTool.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationComponentContractsTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationEdgeValidationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationGraphCompilerTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformTests\Controllers\AgentOrchestrationRevisionApiControll...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformTests\Services\RemoteImageArtifactImportServiceTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\HttpFetchSkillTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PuddingToolInfrastructureTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\ZhihuSearchToolTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\MessageFabric\MessageQueueProjectionService.cs` |
| 30 | RetrievalGlobMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalGlobMatcher.cs`<br>`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalGlobMatcher.cs` |
| 31 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 32 | JiebaAnalyzer | `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Text\JiebaAnalyzer.cs` |
| 33 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 34 | IFileContentExtractor | `Source/PuddingFullTextIndex/Contracts/IFileContentExtractor.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Contracts\IFileContentExtractor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\PlainTextExtractor.cs` |
| 35 | ILanguageServerService | `Source/PuddingCodeIntelligence/Contracts/ILanguageServerService.cs`<br>`Source/PuddingCodeIntelligence/Lsp/IndexBasedLanguageServerService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Contracts\ILanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\NoOpLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\DependencyInjectionTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\DependencyInjection.cs` |
