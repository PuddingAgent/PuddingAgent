# Retrieval evaluation — lucene-fulltext

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `lucene-fulltext` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `small-puddingcodeindex-outline` |
| scope root | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex` |
| started (UTC) | 2026-09-24T03:13:49.4810738+00:00 |
| total elapsed (ms) | 1116 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project temp/U4-0-probe/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode measure --scope "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex" --index-root "E:\github\AgentNetworkPlan\PuddingAgent\temp\U4-1a\index\outline" --set "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingRetrievalEval\eval\reports\compare-puddingcodeindex-outline" --label small-puddingcodeindex-outline --warmup 1 --measured 3 --max-results 20 --expected-under Source/PuddingCodeIndex
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.7500 |
| recall@5 | 0.8214 |
| recall@10 | 0.9286 |
| MRR | 0.7941 |
| precision@5 | 0.1643 |
| precision@10 | 0.0929 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.7500 | 0.8214 | 0.9286 | 0.7941 | 0.1643 | 0.0929 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 2.083 | 4.058 | 6.556 | 550.015 | 550.015 | 23.42 |
| warm (all later calls) | 112 | 2.019 | 3.871 | 6.13 | 7.751 | 11.943 | 4 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 550.0146 | 15 | yes |  |
| 1 | 5.049 | 20 | yes |  |
| 2 | 4.0576 | 15 | yes |  |
| 3 | 2.0834 | 10 | yes |  |
| 4 | 3.4109 | 19 | yes |  |
| 5 | 2.394 | 10 | yes |  |
| 6 | 3.2677 | 19 | yes |  |
| 7 | 2.9075 | 13 | yes |  |
| 8 | 2.9931 | 16 | yes |  |
| 9 | 4.1052 | 20 | yes |  |
| 10 | 2.6462 | 20 | yes |  |
| 11 | 3.0689 | 20 | yes |  |
| 12 | 4.6788 | 20 | yes |  |
| 13 | 2.8013 | 20 | yes |  |
| 14 | 4.2543 | 20 | yes |  |
| 15 | 3.7995 | 20 | yes |  |
| 16 | 3.5914 | 20 | yes |  |
| 17 | 3.1151 | 20 | yes |  |
| 18 | 2.9363 | 20 | yes |  |
| 19 | 4.2957 | 20 | yes |  |
| 20 | 6.5564 | 20 | yes |  |
| 21 | 4.865 | 20 | yes |  |
| 22 | 4.469 | 20 | yes |  |
| 23 | 5.9028 | 20 | yes |  |
| 24 | 4.7376 | 20 | yes |  |
| 25 | 4.7887 | 20 | yes |  |
| 26 | 4.307 | 20 | yes |  |
| 27 | 4.5545 | 20 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 3.2513 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 4.7789 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.5031 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 2.3657 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 3.0187 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 2.6957 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 2.9754 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 2.4167 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.5000 | 0.2000 | 0.1000 | 0.0000 | 3 | 0 | 3.1882 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.9969 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 3.2985 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.1311 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 4.8086 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.9727 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 4.5837 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.9091 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 3.0979 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 3.0179 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 2.8573 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 5.0086 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 4.9177 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 4.8481 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1111 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 6.1298 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1250 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 4.656 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 4.5972 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 10 | 0 | 5.7334 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 1/1 | 0.0000 | 0.0000 | 1.0000 | 0.1667 | 0.0000 | 0.1000 | 0.0000 | 10 | 0 | 3.8712 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 1/1 | 0.0000 | 1.0000 | 1.0000 | 0.3333 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 4.3286 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeWorkspaceDescriptor.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeFileRecord.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeSymbolContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeSymbolContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodePathIdentity.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs` |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeQueue.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs` |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeQueue.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs` |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeRegistry.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs` |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeWatchers.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeBatch.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs` |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexScopeContracts.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeResolver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs` |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexScopeState.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexWatcher.cs`<br>`E:\github\AgentNetworkPlan\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\IndexChange.cs` |
