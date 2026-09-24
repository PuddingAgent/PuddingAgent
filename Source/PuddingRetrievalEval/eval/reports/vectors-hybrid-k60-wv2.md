# Retrieval evaluation — hybrid-rrf-k60-wf1-wv2

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `hybrid-rrf-k60-wf1-wv2` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `small-puddingcodeindex-hybrid-k60-wv2` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T03:37:00.3502920+00:00 |
| total elapsed (ms) | 7476 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-1b\index\hybrid" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\vectors-hybrid-k60-wv2" --label small-puddingcodeindex-hybrid-k60-wv2 --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever hybrid --rrf-k 60 --rrf-weight-fulltext 1 --rrf-weight-vector 2 --fusion-depth 100
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
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.3571 | 0.8929 | 1.0000 | 0.5810 | 0.1786 | 0.1000 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 41.728 | 46.961 | 64.646 | 522.941 | 522.941 | 65.71 |
| warm (all later calls) | 112 | 40.744 | 46.988 | 66.124 | 70.565 | 71.058 | 50.04 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 522.9407 | 20 | yes |  |
| 1 | 64.6461 | 20 | yes |  |
| 2 | 43.7974 | 20 | yes |  |
| 3 | 43.9548 | 20 | yes |  |
| 4 | 41.7283 | 20 | yes |  |
| 5 | 44.068 | 20 | yes |  |
| 6 | 43.1199 | 20 | yes |  |
| 7 | 42.6784 | 20 | yes |  |
| 8 | 43.7927 | 20 | yes |  |
| 9 | 46.56 | 19 | yes |  |
| 10 | 42.4313 | 20 | yes |  |
| 11 | 52.3528 | 20 | yes |  |
| 12 | 45.3087 | 20 | yes |  |
| 13 | 42.6804 | 20 | yes |  |
| 14 | 61.0536 | 19 | yes |  |
| 15 | 43.0867 | 20 | yes |  |
| 16 | 47.0541 | 20 | yes |  |
| 17 | 46.6349 | 17 | yes |  |
| 18 | 46.9613 | 15 | yes |  |
| 19 | 47.5202 | 18 | yes |  |
| 20 | 52.7194 | 20 | yes |  |
| 21 | 54.435 | 20 | yes |  |
| 22 | 50.3977 | 20 | yes |  |
| 23 | 49.7653 | 20 | yes |  |
| 24 | 62.5772 | 20 | yes |  |
| 25 | 51.9989 | 20 | yes |  |
| 26 | 51.9114 | 20 | yes |  |
| 27 | 53.6752 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 48.6796 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 53.9515 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.988 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.1347 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 44.2503 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 44.201 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 43.8604 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 43.7424 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 43.9735 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 45.6218 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 59.9815 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 42.2977 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 44.2383 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 41.9394 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 44.6382 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 43.8203 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.5705 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.7017 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.7557 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 46.0044 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 71.0584 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 66.8875 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 57.0782 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 51.364 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 51.5633 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 53.2214 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 50.9271 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 50.0362 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
