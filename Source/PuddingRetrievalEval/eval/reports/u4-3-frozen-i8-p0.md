# Retrieval evaluation — vector-cosine

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `vector-cosine` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `u4-3-frozen-i8-p0` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T04:54:35.6332689+00:00 |
| total elapsed (ms) | 4407 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\temp\u4-1b-scope\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-3-frozen\index\i8-p0" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\u4-3-frozen-i8-p0" --label u4-3-frozen-i8-p0 --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex --retriever vector
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
| cold (1st call per query) | 28 | 26.77 | 29.888 | 36.714 | 37.141 | 37.141 | 31.11 |
| warm (all later calls) | 112 | 26.679 | 30.648 | 36.553 | 38.679 | 43.57 | 31.44 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 34.3032 | 20 | yes |  |
| 1 | 29.1792 | 20 | yes |  |
| 2 | 29.6543 | 20 | yes |  |
| 3 | 28.6108 | 20 | yes |  |
| 4 | 29.8881 | 20 | yes |  |
| 5 | 28.3185 | 20 | yes |  |
| 6 | 26.7704 | 20 | yes |  |
| 7 | 28.972 | 20 | yes |  |
| 8 | 29.2635 | 20 | yes |  |
| 9 | 27.0277 | 20 | yes |  |
| 10 | 30.2977 | 20 | yes |  |
| 11 | 30.5235 | 20 | yes |  |
| 12 | 28.6649 | 20 | yes |  |
| 13 | 28.7422 | 20 | yes |  |
| 14 | 28.144 | 20 | yes |  |
| 15 | 29.4339 | 20 | yes |  |
| 16 | 31.1491 | 20 | yes |  |
| 17 | 31.3296 | 20 | yes |  |
| 18 | 29.3008 | 20 | yes |  |
| 19 | 30.2309 | 20 | yes |  |
| 20 | 36.5276 | 20 | yes |  |
| 21 | 33.803 | 20 | yes |  |
| 22 | 33.9246 | 20 | yes |  |
| 23 | 36.7142 | 20 | yes |  |
| 24 | 37.1407 | 20 | yes |  |
| 25 | 34.8536 | 20 | yes |  |
| 26 | 34.6739 | 20 | yes |  |
| 27 | 33.5224 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 32.5725 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 1.0000 | 10 | 10 | 30.6806 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 28.5971 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 30.1697 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2000 | 0.2000 | 0.1000 | 1.0000 | 8 | 8 | 28.3005 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 31.3957 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 31.4645 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 26.6791 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 32.577 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 28.9796 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 30.9121 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 30.2755 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 6 | 6 | 30.639 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 28.9844 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 30.1173 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 30.9798 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 29.1441 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 30.114 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 29.6502 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 31.0826 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 38.145 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 10 | 10 | 34.1199 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 1.0000 | 5 | 5 | 34.5126 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 35.052 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 3 | 3 | 36.0353 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 36.5526 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 1.0000 | 7 | 7 | 33.0125 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.2500 | 0.2000 | 0.1000 | 1.0000 | 9 | 9 | 33.6302 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeBatch.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeWatchers.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexSchedulerDriver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeResolver.cs` |
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
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodePathIdentity.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/IndexChange.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndexScopeResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexWatcher.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Contracts/ICodeIndexScopeRegistry.cs`<br>`temp/u4-1b-scope/Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` |
