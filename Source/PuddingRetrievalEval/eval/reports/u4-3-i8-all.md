# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-i8-all` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:49:47.1045331+00:00 |
| total elapsed (ms) | 7847 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3\index\i8-all" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-i8-all" --label u4-3-i8-all --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
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
| cold (1st call per query) | 28 | 49.717 | 53.616 | 63.069 | 64.359 | 64.359 | 55.42 |
| warm (all later calls) | 112 | 49.686 | 54.72 | 63.667 | 75.926 | 77.795 | 56.07 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 64.3592 | 20 | yes |  |
| 1 | 55.4625 | 20 | yes |  |
| 2 | 53.6163 | 20 | yes |  |
| 3 | 51.398 | 20 | yes |  |
| 4 | 53.7389 | 20 | yes |  |
| 5 | 59.6312 | 20 | yes |  |
| 6 | 49.7166 | 20 | yes |  |
| 7 | 50.0998 | 20 | yes |  |
| 8 | 53.0023 | 20 | yes |  |
| 9 | 52.2618 | 20 | yes |  |
| 10 | 51.0392 | 20 | yes |  |
| 11 | 51.1395 | 20 | yes |  |
| 12 | 50.2528 | 20 | yes |  |
| 13 | 52.6039 | 20 | yes |  |
| 14 | 53.3518 | 20 | yes |  |
| 15 | 50.248 | 20 | yes |  |
| 16 | 60.6113 | 20 | yes |  |
| 17 | 52.9777 | 20 | yes |  |
| 18 | 54.0828 | 20 | yes |  |
| 19 | 52.6304 | 20 | yes |  |
| 20 | 59.5318 | 20 | yes |  |
| 21 | 59.7095 | 20 | yes |  |
| 22 | 60.3595 | 20 | yes |  |
| 23 | 57.683 | 20 | yes |  |
| 24 | 62.0279 | 20 | yes |  |
| 25 | 63.0687 | 20 | yes |  |
| 26 | 55.3592 | 20 | yes |  |
| 27 | 61.7949 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 55.4339 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 65.0449 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 51.757 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 54.9595 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 54.8962 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 58.8372 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 8 | 0 | 52.1086 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 50.5924 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 53.7422 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 55.3083 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 9 | 0 | 56.2764 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 50.297 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 53.5334 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 49.6856 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 52.0893 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 50.1111 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 58.3967 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 53.8603 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 53.8499 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 53.0109 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 62.6357 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 60.9024 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 58.5086 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 59.697 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 59.9711 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 63.3713 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 59.5691 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 61.9821 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` |
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
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
