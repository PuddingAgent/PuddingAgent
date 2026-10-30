# Retrieval evaluation — production-auto

> Raw measurements only. **No达标阈值 / pass-fail verdict is applied by this tool.**
> A threshold (e.g. a hot-cache p95 target) is a product decision and must be stated separately.

## Run

| field | value |
|---|---|
| probe | `production-auto` |
| set | `small-puddingcodeindex-v1` v1 |
| scope label | `workspace:default` |
| scope root | `D:\CodeProject\PuddingAgent\PuddingAgent` |
| started (UTC) | 2026-10-30T09:40:29.7738323+00:00 |
| total elapsed (ms) | 33978 |
| cases | 28 |
| failed calls | 0 |
| repetition-stable | yes |

## Reproduce

```
dotnet run --project Source/PuddingRetrievalEvalProbe/PuddingRetrievalEvalProbe.csproj -c Release -- --mode production --db "D:\CodeProject\PuddingAgent\PuddingAgent\temp\r0-02-snapshot\20261030-093937\code_index.db" --workspace default --set "Source\PuddingRetrievalEval\eval\sets\small-puddingcodeindex.json" --out "Source\PuddingRetrievalEval\eval\reports\r0-02-production-small-puddingcodeindex" --max-results 20 --warmup 1 --measured 3 --anchor-symbol ClassifyForEmpty --pagination-query "code" --pagination-page-size 3
```

## Accuracy (mean over cases)

| metric | value |
|---|---|
| recall@1 | 0.7143 |
| recall@5 | 0.7143 |
| recall@10 | 0.7143 |
| MRR | 0.7143 |
| precision@5 | 0.1429 |
| precision@10 | 0.0714 |
| noiseRate@10 | 0.0000 |

## By language stratum

| language | cases | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 |
|---|---|---|---|---|---|---|---|---|
| CSharp | 28 | 0.7143 | 0.7143 | 0.7143 | 0.7143 | 0.1429 | 0.0714 | 0.0000 |

## Latency (raw ms)

| bucket | samples | min | p50 | p95 | p99 | max | mean |
|---|---|---|---|---|---|---|---|
| cold (1st call per query) | 28 | 65.408 | 233.474 | 1068.648 | 1825.696 | 1825.696 | 308.68 |
| warm (all later calls) | 112 | 64.434 | 234.742 | 373.094 | 395.606 | 413.503 | 226.12 |

Cold = first call for that query inside this process; warm = every later call.

## Cold-call raw samples

| # | ms | hits | ok | error |
|---|---|---|---|---|
| 0 | 1825.6955 | 20 | yes |  |
| 1 | 121.766 | 20 | yes |  |
| 2 | 178.245 | 20 | yes |  |
| 3 | 239.1125 | 20 | yes |  |
| 4 | 233.4735 | 20 | yes |  |
| 5 | 1068.6484 | 8 | yes |  |
| 6 | 116.0237 | 20 | yes |  |
| 7 | 188.4788 | 20 | yes |  |
| 8 | 114.4696 | 20 | yes |  |
| 9 | 376.0757 | 16 | yes |  |
| 10 | 373.1333 | 11 | yes |  |
| 11 | 368.0896 | 15 | yes |  |
| 12 | 136.7459 | 20 | yes |  |
| 13 | 97.9574 | 20 | yes |  |
| 14 | 110.0462 | 20 | yes |  |
| 15 | 65.4082 | 20 | yes |  |
| 16 | 65.9742 | 20 | yes |  |
| 17 | 120.447 | 20 | yes |  |
| 18 | 67.5663 | 20 | yes |  |
| 19 | 74.7445 | 20 | yes |  |
| 20 | 340.7506 | 0 | yes |  |
| 21 | 334.6465 | 0 | yes |  |
| 22 | 325.5156 | 0 | yes |  |
| 23 | 322.3703 | 0 | yes |  |
| 24 | 369.2928 | 0 | yes |  |
| 25 | 341.8291 | 0 | yes |  |
| 26 | 340.2143 | 0 | yes |  |
| 27 | 326.2086 | 0 | yes |  |

## Per-case results

| # | kind | lang | query | matched/expected | recall@1 | recall@5 | recall@10 | MRR | precision@5 | precision@10 | noiseRate@10 | hits@10 | noiseHits@10 | ms | ok |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 0 | Symbol | CSharp | ICodeIndexer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 251.703 | yes |
| 1 | Symbol | CSharp | ICodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 138.4683 | yes |
| 2 | Symbol | CSharp | ICodeIndexMaintenance | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 194.2698 | yes |
| 3 | Symbol | CSharp | ICodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 237.1893 | yes |
| 4 | Symbol | CSharp | ICodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 240.2218 | yes |
| 5 | Symbol | CSharp | ICodeProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 376.0203 | yes |
| 6 | Symbol | CSharp | CodeWorkspaceDescriptor | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 116.5918 | yes |
| 7 | Symbol | CSharp | CodeFileRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 188.2735 | yes |
| 8 | Symbol | CSharp | CodeSymbolRecord | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 5 | 0 | 127.2186 | yes |
| 9 | Symbol | CSharp | IndexExcludePatterns | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 370.3012 | yes |
| 10 | Symbol | CSharp | CodePathIdentity | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 370.8783 | yes |
| 11 | Symbol | CSharp | DefaultProjectRootDetector | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 1 | 0 | 372.5485 | yes |
| 12 | Symbol | CSharp | DefaultCodeWorkspaceResolver | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 102.377 | yes |
| 13 | Symbol | CSharp | CodeProjectRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 7 | 0 | 101.3036 | yes |
| 14 | Symbol | CSharp | CodeIndexScopeRegistry | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 6 | 0 | 113.4328 | yes |
| 15 | Symbol | CSharp | CodeIndexScheduler | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 9 | 0 | 66.1544 | yes |
| 16 | Symbol | CSharp | SqliteCodeIndexStore | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 10 | 0 | 77.0817 | yes |
| 17 | Symbol | CSharp | CodeIndexCalibrationService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 4 | 0 | 98.6586 | yes |
| 18 | Symbol | CSharp | CodeIndexChangeCoalescer | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 65.9734 | yes |
| 19 | Symbol | CSharp | CodeIndexMaintenanceService | 1/1 | 1.0000 | 1.0000 | 1.0000 | 1.0000 | 0.2000 | 0.1000 | 0.0000 | 2 | 0 | 65.0618 | yes |
| 20 | Intent | CSharp | where is the index maintenance switch that the host lifecycle drives | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 329.5071 | yes |
| 21 | Intent | CSharp | which file turns gitignore style patterns into the directories the index skips | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 348.8273 | yes |
| 22 | Intent | CSharp | how does the scheduler decide when a maintenance cycle may run | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 327.1673 | yes |
| 23 | Intent | CSharp | which sqlite store removes index rows for files that disappeared from disk | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 331.8113 | yes |
| 24 | Intent | CSharp | where are file system change batches coalesced before the index is updated | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 347.364 | yes |
| 25 | Intent | CSharp | how is a code file path normalised so two spellings map to one identity | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 355.5577 | yes |
| 26 | Intent | CSharp | where is the project root detected when no workspace descriptor exists | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 342.011 | yes |
| 27 | Intent | CSharp | which service marks and sweeps index rows whose file no longer exists | 0/1 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0.0000 | 0 | 0 | 319.6847 | yes |

### Expected hits per case

| # | query | expected | returned (ranked, top 10) |
|---|---|---|---|
| 0 | ICodeIndexer | `Source/PuddingCodeIndex/Contracts/ICodeIndexer.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\MaintenanceHarness.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\EndToEndSourceMaintenanceTests.cs` |
| 1 | ICodeIndexStore | `Source/PuddingCodeIndex/Contracts/ICodeIndexStore.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeSourceMaintenanceCoordinator.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\Retrieval\RetrievalPortV1.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationTestDoub...` |
| 2 | ICodeIndexMaintenance | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexMaintenance.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Services\CodeIndexStatusProbe.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeQueryTools.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Tests\PuddingHost.Tests\Hosting\SA2CodeIndexStatusTests.cs` |
| 3 | ICodeIndexScheduler | `Source/PuddingCodeIndex/Contracts/ICodeIndexScheduler.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeProjectManagementT...` |
| 4 | ICodeWorkspaceResolver | `Source/PuddingCodeIndex/Contracts/ICodeWorkspaceResolver.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeProjectManagementT...` |
| 5 | ICodeProjectRootDetector | `Source/PuddingCodeIndex/Contracts/ICodeProjectRootDetector.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRootDetector.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs` |
| 6 | CodeWorkspaceDescriptor | `Source/PuddingCodeIndex/Contracts/CodeWorkspaceDescriptor.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeWorkspaceDescriptor.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeIndexBatchContracts.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexer.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexFileUpdater.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeWorkspaceResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CompositeCodeIndexer.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexMaintenanceTestDoub...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeSourceMaintenanceCoordin...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeSourceMaintenanceDriverS...` |
| 7 | CodeFileRecord | `Source/PuddingCodeIndex/Contracts/CodeFileRecord.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeFileRecord.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\Retrieval\RetrievalPortV1.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.FileReplacement.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationTestDoub...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\Retrieval\RetrievalPortV1Tests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIntelligence\Python\PythonIndexer.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIntelligence\Services\CodeQueryService.cs` |
| 8 | CodeSymbolRecord | `Source/PuddingCodeIndex/Contracts/CodeSymbolContracts.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\CodeSymbolContracts.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexStore.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeFileSemanticDiff.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\Retrieval\RetrievalPortV1.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs` |
| 9 | IndexExcludePatterns | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\IndexExcludePatterns.cs` |
| 10 | CodePathIdentity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodePathIdentity.cs` |
| 11 | DefaultProjectRootDetector | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultProjectRootDetector.cs` |
| 12 | DefaultCodeWorkspaceResolver | `Source/PuddingCodeIndex/Services/DefaultCodeWorkspaceResolver.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\DefaultCodeWorkspaceResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\DefaultCodeWorkspaceResolverTes...` |
| 13 | CodeProjectRegistry | `Source/PuddingCodeIndex/Services/CodeProjectRegistry.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeProjectRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeProjectRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIntelligenceTests\Services\CodeProjectRegistryTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\LanguageCodeSourceConsumerInputPr...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Services\CodeIndexStatusProbe.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeProjectManagementT...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingRuntime\Tools\BuiltIns\CodeIntelligence\CodeQueryTools.cs` |
| 14 | CodeIndexScopeRegistry | `Source/PuddingCodeIndex/Services/CodeIndexScopeRegistry.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScopeRegistry.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexScopeRegistryTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScopeResolver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Hosting\CodeIndexMaintenanceHostedService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Services\CodeIndexStatusProbe.cs` |
| 15 | CodeIndexScheduler | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Storage\StorageDerivedTargetHandlers.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingHost\Storage\StorageMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexScheduler.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Contracts\ICodeIndexSchedulerDriver.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexSchedulerTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Tests\PuddingHost.Tests\Storage\StorageMaintenanceServiceTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Tests\PuddingHost.Tests\Storage\StorageManagementAdministrationTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs` |
| 16 | SqliteCodeIndexStore | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreFullMaintenanceTest...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveFilesTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreRemoveGraphWorkTest...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreReplaceFilesTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreSourceMaintenanceTe...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreStorageMaintenanceT...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Storage\SqliteCodeIndexStoreTests.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\CodeIndexMaintenanceRunner.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Storage\SqliteCodeIndexStore.FileReplacement.cs` |
| 17 | CodeIndexCalibrationService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexCalibrationService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexCalibrationServiceT...`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeSourceMaintenanceDriverS...` |
| 18 | CodeIndexChangeCoalescer | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexChangeCoalescer.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexChangeCoalescerTest...` |
| 19 | CodeIndexMaintenanceService | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexMaintenanceService.cs` | `D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndex\Services\CodeIndex\CodeIndexMaintenanceService.cs`<br>`D:\CodeProject\PuddingAgent\PuddingAgent\Source\PuddingCodeIndexTests\Services\CodeIndex\CodeIndexMaintenanceServiceT...` |
| 20 | where is the index maintenance switch that the host lifecycle drives | `Source/PuddingCodeIndex/Contracts/ICodeIndexMaintenance.cs` | _(none)_ |
| 21 | which file turns gitignore style patterns into the directories the index skips | `Source/PuddingCodeIndex/Services/IndexExcludePatterns.cs` | _(none)_ |
| 22 | how does the scheduler decide when a maintenance cycle may run | `Source/PuddingCodeIndex/Services/CodeIndexScheduler.cs` | _(none)_ |
| 23 | which sqlite store removes index rows for files that disappeared from disk | `Source/PuddingCodeIndex/Storage/SqliteCodeIndexStore.cs` | _(none)_ |
| 24 | where are file system change batches coalesced before the index is updated | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexChangeCoalescer.cs` | _(none)_ |
| 25 | how is a code file path normalised so two spellings map to one identity | `Source/PuddingCodeIndex/Services/CodePathIdentity.cs` | _(none)_ |
| 26 | where is the project root detected when no workspace descriptor exists | `Source/PuddingCodeIndex/Services/DefaultProjectRootDetector.cs` | _(none)_ |
| 27 | which service marks and sweeps index rows whose file no longer exists | `Source/PuddingCodeIndex/Services/CodeIndex/CodeIndexCalibrationService.cs` | _(none)_ |
