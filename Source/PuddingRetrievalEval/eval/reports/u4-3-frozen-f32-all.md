# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-frozen-f32-all` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:53:04.0457571+00:00 |
| total elapsed (ms) | 6119 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3-frozen\index\f32-all" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-frozen-f32-all" --label u4-3-frozen-f32-all --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
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
| cold (1st call per query) | 28 | 36.813 | 42.28 | 50.296 | 83.231 | 83.231 | 43.79 |
| warm (all later calls) | 112 | 36.547 | 40.882 | 48.997 | 65.716 | 166.125 | 43.54 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 44.9077 | 20 | yes |  |
| 1 | 42.1481 | 20 | yes |  |
| 2 | 37.8071 | 20 | yes |  |
| 3 | 50.2956 | 20 | yes |  |
| 4 | 43.0405 | 20 | yes |  |
| 5 | 40.8432 | 20 | yes |  |
| 6 | 40.0678 | 20 | yes |  |
| 7 | 38.2416 | 20 | yes |  |
| 8 | 41.4267 | 20 | yes |  |
| 9 | 37.8772 | 20 | yes |  |
| 10 | 42.2803 | 20 | yes |  |
| 11 | 40.1782 | 20 | yes |  |
| 12 | 40.1742 | 20 | yes |  |
| 13 | 36.8132 | 20 | yes |  |
| 14 | 42.571 | 20 | yes |  |
| 15 | 38.4944 | 20 | yes |  |
| 16 | 42.7461 | 20 | yes |  |
| 17 | 38.928 | 20 | yes |  |
| 18 | 45.0168 | 20 | yes |  |
| 19 | 38.7125 | 20 | yes |  |
| 20 | 44.5404 | 20 | yes |  |
| 21 | 83.2307 | 20 | yes |  |
| 22 | 45.0393 | 20 | yes |  |
| 23 | 44.0589 | 20 | yes |  |
| 24 | 47.8849 | 20 | yes |  |
| 25 | 46.7087 | 20 | yes |  |
| 26 | 47.4551 | 20 | yes |  |
| 27 | 44.5586 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 42.2717 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 40.3448 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 40.2828 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 43.0196 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 39.1704 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 39.086 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 8 | 8 | 39.5884 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 40.0673 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 40.2782 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 4 | 4 | 39.8436 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 40.0702 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 4 | 4 | 39.9724 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 44.1312 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 40.2053 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 37.7863 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 39.8276 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 38.8862 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 2 | 2 | 39.2031 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 42.4022 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 40.0195 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 65.7155 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 46.6613 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 47.7167 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 46.3712 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 47.0336 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 50.088 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 46.2601 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 45.8299 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
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
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs` |
