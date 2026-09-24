# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-f32-all` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:48:29.5151463+00:00 |
| total elapsed (ms) | 7804 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3\index\f32-all" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-f32-all" --label u4-3-f32-all --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.3571 |
| recall@5 | 0.8571 |
| recall@10 | 1.0000 |
| MRR | 0.5765 |
| precision@5 | 0.1714 |
| precision@10 | 0.1000 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.3571 | 0.8571 | 1.0000 | 0.5765 | 0.1714 | 0.1000 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 49.95 | 53.819 | 63.092 | 63.667 | 63.667 | 55.64 |
| warm (all later calls) | 112 | 50.2 | 55.033 | 61.964 | 63.287 | 66.839 | 55.64 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 56.6084 | 20 | yes |  |
| 1 | 52.8414 | 20 | yes |  |
| 2 | 51.4087 | 20 | yes |  |
| 3 | 51.0318 | 20 | yes |  |
| 4 | 53.5265 | 20 | yes |  |
| 5 | 59.4046 | 20 | yes |  |
| 6 | 53.671 | 20 | yes |  |
| 7 | 52.1521 | 20 | yes |  |
| 8 | 51.0907 | 20 | yes |  |
| 9 | 49.9502 | 20 | yes |  |
| 10 | 51.3871 | 20 | yes |  |
| 11 | 54.3995 | 20 | yes |  |
| 12 | 53.819 | 20 | yes |  |
| 13 | 53.6468 | 20 | yes |  |
| 14 | 53.0043 | 20 | yes |  |
| 15 | 57.6355 | 20 | yes |  |
| 16 | 51.3843 | 20 | yes |  |
| 17 | 54.304 | 20 | yes |  |
| 18 | 52.98 | 20 | yes |  |
| 19 | 58.6104 | 20 | yes |  |
| 20 | 58.4244 | 20 | yes |  |
| 21 | 58.8056 | 20 | yes |  |
| 22 | 62.1955 | 20 | yes |  |
| 23 | 63.667 | 20 | yes |  |
| 24 | 59.4626 | 20 | yes |  |
| 25 | 63.0921 | 20 | yes |  |
| 26 | 58.2399 | 20 | yes |  |
| 27 | 61.2314 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 66.8388 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 55.3667 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 50.5011 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 53.5402 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 55.0332 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 55.6206 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 8 | 0 | 54.0522 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 52.7574 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 51.4445 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 50.6919 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 9 | 0 | 50.1995 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 51.1433 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 54.4054 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 53.3264 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 59.4569 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 56.4796 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 55.5089 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 52.2197 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 53.1194 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 51.1948 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 60.6366 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 58.0111 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 62.2938 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 59.5125 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 60.5844 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 61.964 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 57.2989 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 61.3146 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalHit.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalIntentPolicy.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalHitViews.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalHit.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalFilter.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalTaxonomy.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalResult.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalDistribution.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalEmptyReason.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalNextStep.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `Source/PuddingCodeIndex/Contracts/Retrieval/SymbolIdentity.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalFilter.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalPathFacts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/Retrieval/RetrievalOverflow.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` |
