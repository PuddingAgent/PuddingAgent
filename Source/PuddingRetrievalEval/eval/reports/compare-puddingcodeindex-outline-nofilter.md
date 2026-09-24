# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `small-puddingcodeindex-outline-nofilter` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T03:13:53.3083765+00:00 |
| total elapsed (ms) | 1054 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-1a\index\outline-nofilter" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\compare-puddingcodeindex-outline-nofilter" --label small-puddingcodeindex-outline-nofilter --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.7500 |
| recall@5 | 0.8214 |
| recall@10 | 0.8929 |
| MRR | 0.7949 |
| precision@5 | 0.1643 |
| precision@10 | 0.0893 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.7500 | 0.8214 | 0.8929 | 0.7949 | 0.1643 | 0.0893 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 2.148 | 3.589 | 6.558 | 510.048 | 510.048 | 21.88 |
| warm (all later calls) | 112 | 1.917 | 3.506 | 5.845 | 9.983 | 10.306 | 3.84 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 510.0479 | 15 | yes |  |
| 1 | 3.5893 | 20 | yes |  |
| 2 | 2.9402 | 15 | yes |  |
| 3 | 2.1541 | 10 | yes |  |
| 4 | 3.5341 | 19 | yes |  |
| 5 | 2.6627 | 10 | yes |  |
| 6 | 3.5626 | 19 | yes |  |
| 7 | 2.9386 | 13 | yes |  |
| 8 | 2.1479 | 16 | yes |  |
| 9 | 3.0424 | 20 | yes |  |
| 10 | 3.0602 | 20 | yes |  |
| 11 | 4.2408 | 20 | yes |  |
| 12 | 2.3999 | 20 | yes |  |
| 13 | 4.032 | 20 | yes |  |
| 14 | 2.409 | 20 | yes |  |
| 15 | 2.5993 | 20 | yes |  |
| 16 | 3.9829 | 20 | yes |  |
| 17 | 4.4125 | 20 | yes |  |
| 18 | 3.1263 | 20 | yes |  |
| 19 | 3.8034 | 20 | yes |  |
| 20 | 6.5581 | 20 | yes |  |
| 21 | 4.8867 | 20 | yes |  |
| 22 | 4.7464 | 20 | yes |  |
| 23 | 6.0703 | 20 | yes |  |
| 24 | 5.1842 | 20 | yes |  |
| 25 | 4.6997 | 20 | yes |  |
| 26 | 5.0764 | 20 | yes |  |
| 27 | 4.6179 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 3.7634 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.5026 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 2.4362 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 1.9168 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 3.105 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 2.4721 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 3.2149 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 2.2115 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 2.1081 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.0008 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 4.0337 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 4.3939 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.1971 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.8486 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.4061 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.211 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 9.9828 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 4.4448 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.2505 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.6615 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 5.5352 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 5.0018 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 5.845 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 5.4446 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 5.1801 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 9 | 0 | 5.0964 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 0.0000 | 0.0000 | 0.0909 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 4.3107 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 4.6461 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeWorkspaceDescriptor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeFileRecord.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeSymbolContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeSymbolContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodePathIdentity.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeQueue.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeQueue.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeRegistry.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeBatch.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeQueue.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs` |
