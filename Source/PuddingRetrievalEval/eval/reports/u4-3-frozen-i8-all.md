# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-frozen-i8-all` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:53:51.9872118+00:00 |
| total elapsed (ms) | 5948 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3-frozen\index\i8-all" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-frozen-i8-all" --label u4-3-frozen-i8-all --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.3571 |
| recall@5 | 0.8929 |
| recall@10 | 1.0000 |
| MRR | 0.5810 |
| precision@5 | 0.1786 |
| precision@10 | 0.1000 |
| noiseRate@10 | 1.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.3571 | 0.8929 | 1.0000 | 0.5810 | 0.1786 | 0.1000 | 1.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 37.095 | 41.35 | 48.529 | 49.689 | 49.689 | 42.4 |
| warm (all later calls) | 112 | 37.36 | 41.15 | 49.257 | 50.261 | 50.516 | 42.38 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 44.0109 | 20 | yes |  |
| 1 | 39.2641 | 20 | yes |  |
| 2 | 38.79 | 20 | yes |  |
| 3 | 43.4355 | 20 | yes |  |
| 4 | 39.8882 | 20 | yes |  |
| 5 | 39.3037 | 20 | yes |  |
| 6 | 37.0947 | 20 | yes |  |
| 7 | 41.3495 | 20 | yes |  |
| 8 | 43.2173 | 20 | yes |  |
| 9 | 37.7877 | 20 | yes |  |
| 10 | 40.1748 | 20 | yes |  |
| 11 | 39.0702 | 20 | yes |  |
| 12 | 42.5665 | 20 | yes |  |
| 13 | 39.5389 | 20 | yes |  |
| 14 | 42.4289 | 20 | yes |  |
| 15 | 37.2843 | 20 | yes |  |
| 16 | 43.8813 | 20 | yes |  |
| 17 | 39.511 | 20 | yes |  |
| 18 | 41.0072 | 20 | yes |  |
| 19 | 39.3116 | 20 | yes |  |
| 20 | 43.9231 | 20 | yes |  |
| 21 | 48.5289 | 20 | yes |  |
| 22 | 47.4844 | 20 | yes |  |
| 23 | 47.1712 | 20 | yes |  |
| 24 | 47.7179 | 20 | yes |  |
| 25 | 49.6893 | 20 | yes |  |
| 26 | 46.4788 | 20 | yes |  |
| 27 | 47.3948 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 38.0601 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 41.16 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 41.5993 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 44.1184 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 39.3565 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 41.1832 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 8 | 8 | 40.6742 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 37.3599 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 40.0558 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 4 | 4 | 39.2931 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 37.6123 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 4 | 4 | 38.5946 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 37.9745 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 39.7113 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 42.9123 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 40.6709 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 44.3789 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 2 | 2 | 39.9878 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 39.6912 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 39.7495 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 47.054 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 46.6248 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 46.7817 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 46.421 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 50.5155 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 48.2802 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 46.0404 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 46.5773 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
