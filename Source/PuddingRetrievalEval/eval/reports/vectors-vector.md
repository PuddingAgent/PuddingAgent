# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `small-puddingcodeindex-vector` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T03:36:37.7511548+00:00 |
| total elapsed (ms) | 6125 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-1b\index\vector" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\vectors-vector" --label small-puddingcodeindex-vector --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
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
| cold (1st call per query) | 28 | 36.963 | 43.216 | 55.369 | 56.533 | 56.533 | 44.19 |
| warm (all later calls) | 112 | 35.96 | 41.236 | 56.813 | 61.826 | 66.762 | 43.53 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 43.2156 | 20 | yes |  |
| 1 | 39.3268 | 20 | yes |  |
| 2 | 37.695 | 20 | yes |  |
| 3 | 40.8258 | 20 | yes |  |
| 4 | 40.6252 | 20 | yes |  |
| 5 | 40.4252 | 20 | yes |  |
| 6 | 44.6753 | 20 | yes |  |
| 7 | 56.5329 | 20 | yes |  |
| 8 | 55.3692 | 20 | yes |  |
| 9 | 52.0213 | 20 | yes |  |
| 10 | 36.9629 | 20 | yes |  |
| 11 | 38.7928 | 20 | yes |  |
| 12 | 40.678 | 20 | yes |  |
| 13 | 53.1066 | 20 | yes |  |
| 14 | 39.5236 | 20 | yes |  |
| 15 | 52.1769 | 20 | yes |  |
| 16 | 41.4021 | 20 | yes |  |
| 17 | 40.4711 | 20 | yes |  |
| 18 | 41.5968 | 20 | yes |  |
| 19 | 37.7796 | 20 | yes |  |
| 20 | 45.1218 | 20 | yes |  |
| 21 | 46.6061 | 20 | yes |  |
| 22 | 46.1095 | 20 | yes |  |
| 23 | 46.1946 | 20 | yes |  |
| 24 | 43.987 | 20 | yes |  |
| 25 | 45.9999 | 20 | yes |  |
| 26 | 44.2485 | 20 | yes |  |
| 27 | 45.8852 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 39.7886 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 45.892 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 42.5211 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 38.9622 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 42.3388 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 40.8671 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 8 | 0 | 36.4542 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 38.7069 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 38.5841 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 38.934 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 9 | 0 | 38.1698 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 41.0217 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 40.1848 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 39.2481 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 55.0809 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 38.7908 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 41.7867 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 38.2981 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 44.9496 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 39.9748 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 44.6979 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 43.902 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 42.5375 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 61.8255 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 47.0005 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 0.0000 | 8 | 0 | 47.4111 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 43.9718 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 45.2587 | yes |

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
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs` |
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
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` |
