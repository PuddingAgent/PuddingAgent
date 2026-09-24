# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-frozen-f32-p0` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:54:14.4797750+00:00 |
| total elapsed (ms) | 4405 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3-frozen\index\f32-p0" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-frozen-f32-p0" --label u4-3-frozen-f32-p0 --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.4643 |
| recall@5 | 0.9286 |
| recall@10 | 1.0000 |
| MRR | 0.6497 |
| precision@5 | 0.1857 |
| precision@10 | 0.1000 |
| noiseRate@10 | 1.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.4643 | 0.9286 | 1.0000 | 0.6497 | 0.1857 | 0.1000 | 1.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 27.075 | 30.419 | 37.816 | 43.583 | 43.583 | 31.79 |
| warm (all later calls) | 112 | 27.231 | 30.402 | 35.764 | 37.673 | 38.683 | 31.25 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 34.0251 | 20 | yes |  |
| 1 | 32.826 | 20 | yes |  |
| 2 | 30.4187 | 20 | yes |  |
| 3 | 29.7305 | 20 | yes |  |
| 4 | 30.4461 | 20 | yes |  |
| 5 | 29.7275 | 20 | yes |  |
| 6 | 30.2612 | 20 | yes |  |
| 7 | 29.6416 | 20 | yes |  |
| 8 | 30.246 | 20 | yes |  |
| 9 | 30.2046 | 20 | yes |  |
| 10 | 29.3381 | 20 | yes |  |
| 11 | 30.5446 | 20 | yes |  |
| 12 | 30.1238 | 20 | yes |  |
| 13 | 28.0357 | 20 | yes |  |
| 14 | 27.0752 | 20 | yes |  |
| 15 | 27.4367 | 20 | yes |  |
| 16 | 31.4018 | 20 | yes |  |
| 17 | 30.2873 | 20 | yes |  |
| 18 | 31.9237 | 20 | yes |  |
| 19 | 27.3399 | 20 | yes |  |
| 20 | 33.6697 | 20 | yes |  |
| 21 | 35.5359 | 20 | yes |  |
| 22 | 32.898 | 20 | yes |  |
| 23 | 35.588 | 20 | yes |  |
| 24 | 34.8356 | 20 | yes |  |
| 25 | 37.816 | 20 | yes |  |
| 26 | 43.5832 | 20 | yes |  |
| 27 | 35.085 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 30.0748 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 31.4565 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 29.1259 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 27.8742 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 29.7646 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 29.0807 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 30.6578 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 28.5854 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 30.1024 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 30.4913 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 29.124 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 30.8776 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 32.7924 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 29.4657 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 30.4016 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 29.3888 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 31.1912 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 28.26 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 29.3847 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 30.1513 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 33.5584 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 35.233 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 33.9095 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 34.1531 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 34.2244 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 35.7714 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 33.2444 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 34.2669 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeIndexScopeContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeRelationContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeReferenceRecord.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeProjectContracts.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexFileUpdater.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexScopeState.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeQueue.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` |
