# Tests/PuddingToolPlugins.IntegrationTests
> 源指纹: *.cs=6-8c39397104a2, *.csproj=1-40483a6f85b2 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.ToolPlugins.Runtime、Pudding.ToolPlugins.Windows、PuddingPlatform、Pudding.Api.Sdk、PuddingRuntime；用例全部为进程内单测，不需要真实宿主或外部服务。

`FoundationPrototypeTests.cs` 覆盖 2 条用例：
- `SdkSubmittedWork_ProducesVersionedArtifact_AndObserverArchive`
- `Signal`

`HostToolAutoMountTests.cs` 覆盖 2 条用例：
- `DeclarativePackageMountsOnFirstUse_UnloadPinsItOff_AndLoadRestoresIt`
- `PolicyDenialKeepsThePackageUnmounted`

`HostToolSearchTests.cs` 覆盖 1 条用例：
- `HostPackagesAreSearchable_AndActiveReflectsAutoMount`

`PluginToolExposureTests.cs` 覆盖 1 条用例：
- `PluginLoad_IsExposed_UnderTemplateGrantPolicy`

`PluginWorkspaceEventDemoTests.cs` 覆盖 3 条用例：
- `ZeroToolObserver_ArchivesOnce_AndUnloadStopsGrowth`
- `PassiveObserver_ArchivesNothing`
- `SyncAsync`

`RealWorkerTests.cs` 覆盖 7 条用例：
- `RequirePluginAcceptanceEnvironment`
- `RealDll_LoadInvokeCoreCallbackHookUnload_WithoutRestartingController`
- `InitializationFailure_DoesNotPublish_AndReapsProcess`
- `FaultyPlugin_IsStopped_AndOtherAgentRemainsUsable`
- `Timeout_ReapsChildProcessTree`
- `ZeroToolSubscriptionPlugin_ReceivesWorkspaceEvent_AndUnloadStopsDelivery`
- （其余 1 条同类用例略）
