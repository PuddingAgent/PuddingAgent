# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `seed-v1` v1 |
| scope label | `source-scope` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source` |
| started (UTC) | 2026-09-24T00:50:58.3933847+00:00 |
| total elapsed (ms) | 3680 |
| cases | 58 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-0-probe\index" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\baseline-2026-09-24-source-scope" --label source-scope --warmup 1 --measured 3 --max-results 20 --expected-under Source/
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.4224 |
| recall@5 | 0.6638 |
| recall@10 | 0.6638 |
| MRR | 0.5768 |
| precision@5 | 0.1621 |
| precision@10 | 0.0810 |
| noiseRate@10 | 0.0290 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 36 | 0.6389 | 0.6944 | 0.6944 | 0.7292 | 0.1611 | 0.0806 | 0.0439 |
| TypeScript | 22 | 0.0682 | 0.6136 | 0.6136 | 0.3273 | 0.1636 | 0.0818 | 0.0045 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 58 | 4.534 | 7.126 | 27.216 | 625.385 | 625.385 | 21.26 |
| warm (all later calls) | 232 | 4.503 | 7.116 | 26.548 | 31.313 | 33.081 | 10.45 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 625.385 | 20 | yes |  |
| 1 | 8.3922 | 20 | yes |  |
| 2 | 5.5697 | 20 | yes |  |
| 3 | 6.4147 | 20 | yes |  |
| 4 | 6.5812 | 20 | yes |  |
| 5 | 5.8832 | 20 | yes |  |
| 6 | 6.3817 | 20 | yes |  |
| 7 | 5.3904 | 20 | yes |  |
| 8 | 5.9598 | 20 | yes |  |
| 9 | 5.5172 | 20 | yes |  |
| 10 | 4.5341 | 20 | yes |  |
| 11 | 6.2248 | 20 | yes |  |
| 12 | 5.7222 | 20 | yes |  |
| 13 | 5.5183 | 20 | yes |  |
| 14 | 6.6715 | 20 | yes |  |
| 15 | 5.4572 | 20 | yes |  |
| 16 | 5.0848 | 20 | yes |  |
| 17 | 5.0701 | 20 | yes |  |
| 18 | 6.852 | 20 | yes |  |
| 19 | 7.2107 | 20 | yes |  |
| 20 | 22.5243 | 20 | yes |  |
| 21 | 10.2882 | 20 | yes |  |
| 22 | 20.5626 | 20 | yes |  |
| 23 | 8.9895 | 20 | yes |  |
| 24 | 23.7907 | 20 | yes |  |
| 25 | 22.5566 | 20 | yes |  |
| 26 | 32.1015 | 20 | yes |  |
| 27 | 27.2156 | 20 | yes |  |
| 28 | 17.8002 | 20 | yes |  |
| 29 | 16.9634 | 20 | yes |  |
| 30 | 6.7492 | 20 | yes |  |
| 31 | 9.9473 | 20 | yes |  |
| 32 | 7.4959 | 20 | yes |  |
| 33 | 7.7367 | 20 | yes |  |
| 34 | 7.5741 | 20 | yes |  |
| 35 | 6.9852 | 17 | yes |  |
| 36 | 7.1265 | 20 | yes |  |
| 37 | 6.6262 | 20 | yes |  |
| 38 | 8.1083 | 20 | yes |  |
| 39 | 6.952 | 20 | yes |  |
| 40 | 5.9992 | 20 | yes |  |
| 41 | 7.7806 | 20 | yes |  |
| 42 | 6.8532 | 20 | yes |  |
| 43 | 7.3575 | 20 | yes |  |
| 44 | 22.2379 | 20 | yes |  |
| 45 | 16.1917 | 20 | yes |  |
| 46 | 22.5955 | 20 | yes |  |
| 47 | 15.1222 | 20 | yes |  |
| 48 | 16.5962 | 20 | yes |  |
| 49 | 17.9618 | 20 | yes |  |
| 50 | 20.8548 | 20 | yes |  |
| 51 | 14.1522 | 20 | yes |  |
| 52 | 8.0563 | 20 | yes |  |
| 53 | 6.6123 | 20 | yes |  |
| 54 | 6.7371 | 20 | yes |  |
| 55 | 6.4611 | 20 | yes |  |
| 56 | 7.0626 | 20 | yes |  |
| 57 | 6.737 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | SearchGrepTool | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 8.0585 | yes |
| 1 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.6553 | yes |
| 2 | Symbol | CSharp | LuceneSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 5.2106 | yes |
| 3 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.5017 | yes |
| 4 | Symbol | CSharp | IFullTextSearchEngine | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.283 | yes |
| 5 | Symbol | CSharp | FullTextIndexOptions | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.6134 | yes |
| 6 | Symbol | CSharp | RetrievalGlobMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.6656 | yes |
| 7 | Symbol | CSharp | RetrievalMatcher | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.509 | yes |
| 8 | Symbol | CSharp | SearchAttemptLedger | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.8048 | yes |
| 9 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 6.0102 | yes |
| 10 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 4.5296 | yes |
| 11 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 7.549 | yes |
| 12 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 5.8482 | yes |
| 13 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 8.6186 | yes |
| 14 | Symbol | CSharp | ComponentBoundaryTests | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 5.4943 | yes |
| 15 | Symbol | CSharp | IndexBasedLanguageServerService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.7448 | yes |
| 16 | Symbol | CSharp | CodeQueryService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 4.8815 | yes |
| 17 | Symbol | CSharp | RoslynCSharpIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.3824 | yes |
| 18 | Symbol | CSharp | PlainTextExtractor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.4926 | yes |
| 19 | Symbol | CSharp | JiebaAnalyzer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.6502 | yes |
| 20 | Intent | CSharp | where is the list of directories the grep tool skips by default | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 3 | 0 | 19.3807 | yes |
| 21 | Intent | CSharp | code that reads gitignore files and turns them into exclude patterns | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 12.3088 | yes |
| 22 | Intent | CSharp | how does the full text engine parse a query against content and file name | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.1000 | 10 | 1 | 21.3597 | yes |
| 23 | Intent | CSharp | which sqlite implementation stores code symbols and relations | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 8.9478 | yes |
| 24 | Intent | CSharp | how are file system changes coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.2000 | 10 | 2 | 24.8217 | yes |
| 25 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 0.0000 | 0.0000 | 0.0526 | 0.0000 | 0.0000 | 0.1000 | 10 | 1 | 22.1695 | yes |
| 26 | Intent | CSharp | where is the project root detected for the code index scope | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 32.3965 | yes |
| 27 | Intent | CSharp | how is the code index maintenance loop driven by the host | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 26.6918 | yes |
| 28 | Intent | CSharp | where does the code query service run a symbol search | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.8571 | 7 | 6 | 16.946 | yes |
| 29 | Intent | CSharp | which file extracts plain text content for full text indexing | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 16.464 | yes |
| 30 | Crossref | CSharp | RetrievalGlobMatcher | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.5908 | yes |
| 31 | Crossref | CSharp | IndexExcludePatterns | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 8.7136 | yes |
| 32 | Crossref | CSharp | JiebaAnalyzer | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 8.0799 | yes |
| 33 | Crossref | CSharp | ICodeIndexStore | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 7.1794 | yes |
| 34 | Crossref | CSharp | IFileContentExtractor | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.2000 | 5 | 1 | 7.5092 | yes |
| 35 | Crossref | CSharp | ILanguageServerService | 2/2 | 0.5000 | 1.0000 | 1.0000 | 1.0000 | 0.4000 | 0.2000 | 0.1250 | 8 | 1 | 6.6085 | yes |
| 36 | Symbol | TypeScript | PuddingDataTable | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 6.7839 | yes |
| 37 | Symbol | TypeScript | PuddingStatusBadge | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 7.0146 | yes |
| 38 | Symbol | TypeScript | TaskBoard | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 7.9542 | yes |
| 39 | Symbol | TypeScript | TaskCard | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 6.6449 | yes |
| 40 | Symbol | TypeScript | syncEngine | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 5.9516 | yes |
| 41 | Symbol | TypeScript | canonicalMerge | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 7.7049 | yes |
| 42 | Symbol | TypeScript | workspaceNavigation | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.9876 | yes |
| 43 | Symbol | TypeScript | autoReviewClassifier | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.9158 | yes |
| 44 | Intent | TypeScript | where is the chat client store that keeps local cache in sync | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 21.3992 | yes |
| 45 | Intent | TypeScript | how does the chat client merge canonical server state | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.1000 | 10 | 1 | 16.0271 | yes |
| 46 | Intent | TypeScript | where is the checkpoint store used when resuming a chat session | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 8 | 0 | 25.5338 | yes |
| 47 | Intent | TypeScript | which module renders the workspace task board columns | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 5 | 0 | 16.4778 | yes |
| 48 | Intent | TypeScript | where is the scheduler drawer for recurring tasks | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 14.183 | yes |
| 49 | Intent | TypeScript | how is the agent template settings drawer organized into sections | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 2 | 0 | 19.5768 | yes |
| 50 | Intent | TypeScript | where is the access token secret shown once modal | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 6 | 0 | 16.4217 | yes |
| 51 | Intent | TypeScript | which util builds the workspace navigation menu | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 13.9502 | yes |
| 52 | Crossref | TypeScript | canonicalMerge | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.4000 | 0.2000 | 0.0000 | 3 | 0 | 7.9504 | yes |
| 53 | Crossref | TypeScript | checkpointStore | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 6.2721 | yes |
| 54 | Crossref | TypeScript | TaskCard | 1/2 | 0.0000 | 0.5000 | 0.5000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 6.1202 | yes |
| 55 | Crossref | TypeScript | PuddingStatusBadge | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.4000 | 0.2000 | 0.0000 | 8 | 0 | 6.4659 | yes |
| 56 | Crossref | TypeScript | TaskEventTimeline | 1/2 | 0.5000 | 0.5000 | 0.5000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 6.5575 | yes |
| 57 | Crossref | TypeScript | PuddingAdminShell | 2/2 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.4000 | 0.2000 | 0.0000 | 5 | 0 | 6.2478 | yes |

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
| 20 | where is the list of directories the grep tool skips by default | `Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\RetrievalMetricsTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js` |
| 21 | code that reads gitignore files and turns them into exclude patterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Abstractions\IAgentRuntimeProfileResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\.gitignore`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\WarmPrefixCompaction.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\EdgeInspector.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__orchestration__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\SseFrameBatchPump.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...` |
| 22 | how does the full text engine parse a query against content and file name | `Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\$outputWwwroot\05793652-async.9017043a.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\SmartWorkflow\SmartExploreTool.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEvalTests\ComponentBoundaryTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexMaintenanceServiceTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Storage\StorageMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files\FileTools.cs` |
| 23 | which sqlite implementation stores code symbols and relations | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_1-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PluginManifestOnlyTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexer.Cli\Scripts\extract-ts-symbols.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexer.Cli\pub\Scripts\extract-ts-symbols.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\CSharp\RoslynCSharpIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\TypeScript\TypeScriptIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexer.Cli\Scripts\extract-py-symbols.py` |
| 24 | how are file system changes coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\default-data\benchmark-seeds\memory-contract-followup\draf...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_1-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Bootstrap\DesktopBootstrapSignalService.cs` |
| 25 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\DesktopParentProcessMonitor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\CSharp\RoslynCSharpIndexerRemovalTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\TokenUsageRebuildService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\ConversationAcceptanceStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs` |
| 26 | where is the project root detected for the code index scope | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexRemovalCorrectnessTe...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\TypeScript\TypeScriptIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Core\GitSnapshotService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__chat__index-async.js` |
| 27 | how is the code index maintenance loop driven by the host | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\GitCommitArgsSerializationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\MessageFabric\MessageFabricSchemaBootstrapper.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\ToolApproval.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationDriverTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__chat__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Extensions\PuddingServiceCollectionExtensions.Runtime.cs` |
| 28 | where does the code query service run a symbol search | `Source/PuddingCodeIntelligence/Services/CodeQueryService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\cf7fc8fac97d43b0a2cb74d5fb195f11-sub-08e...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\cf7fc8fac97d43b0a2cb74d5fb195f11-sub-08e...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\cf7fc8fac97d43b0a2cb74d5fb195f11-sub-08e...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\cf7fc8fac97d43b0a2cb74d5fb195f11-sub-08e...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\cf7fc8fac97d43b0a2cb74d5fb195f11-sub-08e...` |
| 29 | which file extracts plain text content for full text indexing | `Source/PuddingFullTextIndex/Infrastructure/Search/PlainTextExtractor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\Files\FilePatchTool.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationComponentContractsTes...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationEdgeValidationTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCoreTests\Orchestration\AgentOrchestrationGraphCompilerTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformTests\Controllers\AgentOrchestrationRevisionApiControll...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformTests\Services\RemoteImageArtifactImportServiceTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\HttpFetchSkillTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntimeTests\Tools\PuddingToolInfrastructureTests.cs` |
| 30 | RetrievalGlobMatcher | `Source/PuddingCore/Tools/Retrieval/RetrievalGlobMatcher.cs`<br>`Source/PuddingRuntime/Tools/BuiltIns/Search/SearchGrepTool.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Tools\Retrieval\RetrievalGlobMatcher.cs` |
| 31 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 32 | JiebaAnalyzer | `Source/PuddingFullTextIndex/Infrastructure/Text/JiebaAnalyzer.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Text\JiebaAnalyzer.cs` |
| 33 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 34 | IFileContentExtractor | `Source/PuddingFullTextIndex/Contracts/IFileContentExtractor.cs`<br>`Source/PuddingFullTextIndex/Infrastructure/Search/LuceneSearchEngine.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Contracts\IFileContentExtractor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\LuceneSearchEngine.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\Infrastructure\Search\PlainTextExtractor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingFullTextIndex\code_map.md` |
| 35 | ILanguageServerService | `Source/PuddingCodeIntelligence/Contracts/ILanguageServerService.cs`<br>`Source/PuddingCodeIntelligence/Lsp/IndexBasedLanguageServerService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Contracts\ILanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\IndexBasedLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\Lsp\NoOpLanguageServerService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\DependencyInjectionTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\DependencyInjection.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIntelligence\code_map.md`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json` |
| 36 | PuddingDataTable | `Source/PuddingPlatformAdmin/src/components/PuddingDataTable/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingDataTable\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 37 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 38 | TaskBoard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskBoard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskBoard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\index.tsx` |
| 39 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js` |
| 40 | syncEngine | `Source/PuddingPlatformAdmin/src/pages/chat/client/syncEngine.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\syncEngine.ts` |
| 41 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\chatClientStore.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 42 | workspaceNavigation | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\utils\workspaceNavigation.ts` |
| 43 | autoReviewClassifier | `Source/PuddingPlatformAdmin/src/pages/chat/classifier/autoReviewClassifier.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.te...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\classifier\autoReviewClassifier.ts` |
| 44 | where is the chat client store that keeps local cache in sync | `Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\revisionEditor.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__chat__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\AgentExecution\NoOpKeyVaultService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_1-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\localCache.test.ts` |
| 45 | how does the chat client merge canonical server state | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\Goals\GoalContinuationWorker.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Models\AgentReplyVoiceDirective.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Orchestration\AgentOrchestrationAuthoringContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\hooks\useCompaction.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__chat__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Messaging\MessageDeliveryDispatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\.pudding\context-tool-results\206a9b48ec904ebb93e7541131fbb835-sub-fc8...` |
| 46 | where is the checkpoint store used when resuming a chat session | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\services\swagger\store.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\connectionDrag.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRuntime\Services\Messaging\AgentExecutionAdmissionCoordinator.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\orchestration\requestSequenceGuard.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__orchestration__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 47 | which module renders the workspace task board columns | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__workspace__id__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\src_pages_workspace-tasks_index_tsx-asyn...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__workspace__index-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\ChatMain.test.tsx` |
| 48 | where is the scheduler drawer for recurring tasks | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/SchedulerDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\ConversationProjectionWorker.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\src_pages_workspace-tasks_index_tsx-asyn...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCore\Configuration\PuddingConfigModels.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingHost\Hosting\DesktopLifecycleEndpointExtensions.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\reducer\subAgentReducer.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\components\MessageList.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\p__chat__index-async.js` |
| 49 | how is the agent template settings drawer organized into sections | `Source/PuddingPlatformAdmin/src/pages/agent-template-settings/AgentTemplateSettingsDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\src_pages_global-agent-template_index_ts...` |
| 50 | where is the access token secret shown once modal | `Source/PuddingPlatformAdmin/src/pages/access-token-management/components/SecretOnceModal.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\src_pages_access-token-management_index_...`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\vendors_0-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Storage\CoreStorageManagementClient.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexChangeCoalescerTests.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingDesktop\Hosting\DesktopApplicationCoordinator.cs` |
| 51 | which util builds the workspace navigation menu | `Source/PuddingPlatformAdmin/src/utils/workspaceNavigation.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\seed-v1.json`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\bn-BD\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\en-US\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\fa-IR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\id-ID\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\ja-JP\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\pt-BR\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\zh-TW\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\locales\zh-CN\menu.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatform\Services\WorkspaceAgentRosterProvider.cs` |
| 52 | canonicalMerge | `Source/PuddingPlatformAdmin/src/pages/chat/client/canonicalMerge.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/client/chatClientStore.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\chatClientStore.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\canonicalMerge.ts` |
| 53 | checkpointStore | `Source/PuddingPlatformAdmin/src/pages/chat/client/checkpointStore.ts`<br>`Source/PuddingPlatformAdmin/src/pages/chat/hooks/useCheckpointTimeline.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.test.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\chat\client\checkpointStore.ts` |
| 54 | TaskCard | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskColumn.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js` |
| 55 | PuddingStatusBadge | `Source/PuddingPlatformAdmin/src/components/PuddingStatusBadge/index.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskCard.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingStatusBadge\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskCard.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskTable.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
| 56 | TaskEventTimeline | `Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskEventTimeline.tsx`<br>`Source/PuddingPlatformAdmin/src/pages/workspace-tasks/TaskDetailsDrawer.tsx` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\pages\workspace-tasks\TaskEventTimeline.tsx` |
| 57 | PuddingAdminShell | `Source/PuddingPlatformAdmin/src/components/PuddingAdminShell/index.tsx`<br>`Source/PuddingPlatformAdmin/src/components/index.ts` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\styles.ts`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminPrimitives.test.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\PuddingAdminShell\index.tsx`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\dist-dev\common-async.js`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingPlatformAdmin\src\components\index.ts` |
