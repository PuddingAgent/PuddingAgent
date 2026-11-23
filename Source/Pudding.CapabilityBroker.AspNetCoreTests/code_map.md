# Pudding.CapabilityBroker.AspNetCoreTests
> 源指纹: *.cs=5-074255384bdd, *.csproj=1-da557e914a73 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.CapabilityBroker.AspNetCore；用例全部为进程内单测，不需要真实宿主或外部服务。

`CapabilityChannelConfigurationTests.cs` 覆盖 14 条用例：
- `DefaultConfiguration_IsDisabled`
- `EnabledConfiguration_BindsEveryField`
- `DerivedPipeName_IsScopedPerUserAndProductInstance`
- `ExplicitPipeName_Wins`
- `Describe_ProducesAParsablePublishableDescriptionWithoutCredentials`
- `Transport_DeterminesWhatIsListenedToAndPublished`
- （其余 8 条同类用例略）

`CapabilityChannelHostTests.cs` 覆盖 19 条用例：
- `KestrelListenOverridesUseUrls_SoTheAssemblyBindsBothExplicitly`
- `Preflight_ConfirmsRestAndCapabilityEndpointsAreBothBound`
- `Preflight_NamesTheListenOverridesUseUrlsTrapWhenRestDisappears`
- `Preflight_ReportsAMissingCapabilityEndpointAndInstanceId`
- `Preflight_DisabledChannelOnlyRequiresRestToSurvive`
- `Preflight_RejectsAnAddressThatDoesNotIdentifyTheExpectedPipe`
- （其余 13 条同类用例略）

`CapabilityChannelReadySignalTests.cs` 覆盖 8 条用例：
- `Describe_NamedPipe_RoundTripsThroughStrictParser`
- `Describe_Loopback_RoundTripsThroughStrictParser`
- `Describe_ProtocolVersion_SegmentIsInteger_NotDecorated`
- `Describe_VPrefixedDescription_IsRejectedByTheSameStrictParser`
- `Describe_LogLine_ContainsExactlyTheSameDescription`
- `Describe_Description_CarriesNoCredentials`
- （其余 2 条同类用例略）

`TrustedCallerAuthorizerOverTheWireTests.cs` 覆盖 4 条用例：
- `WithoutIdentity_DeniesAndNeverSendsTheCommand`
- `WithTrustedIdentity_SendsTheCommandAndCompletesTheCall`
- `IdentityWithoutExecution_DeniesMutatingCapabilityBeforeSending`
- `NarrowBrowserPort_RoutesEveryOperationThroughTheRealChannel`
