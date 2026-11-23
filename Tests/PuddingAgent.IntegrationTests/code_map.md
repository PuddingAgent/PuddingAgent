# Tests/PuddingAgent.IntegrationTests
> 源指纹: *.csproj=1-9d908cd2da78 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingAgent；用例全部为进程内单测，不需要真实宿主或外部服务。

`Feishu/FakeFeishuRoundTripTests.cs` 覆盖 12 条用例：
- `ConfiguredRecipient_SendsThroughDurableQueue_WithoutAnyIngressHistory`
- `FakeFeishuMessage_RunsDurableIngressAndReplyDeliveryChain`
- `FakeFeishuStreamingCard_ProjectsDeltasAndFinalizesThroughDurableDelivery`
- `StartAsync`
- `StopAsync`
- `SendAsync`
- （其余 6 条同类用例略）

`Feishu/FeishuCommandInterceptionTests.cs` 覆盖 7 条用例：
- `Yolo_RequiresPrivateVerifiedSenderBindingAndFeishuCommandGrant`
- `CommandResult_ReachesAgentOnlyWhenHandlerExplicitlyRequestsForwarding`
- `Compact_IsInterceptedAndRepliesToFeishuWithoutAgentDelivery`
- `WhoAmI_PassesSenderOpenIdAndRepliesWithoutPrivilegeOrAgentDelivery`
- `Status_IsInterceptedForNonWhitelistedUser_AndRepliesWithoutAgentDelivery`
- `SetChannelBindingAsync`
- （其余 1 条同类用例略）

`Feishu/FeishuImageUploadPreparationServiceTests.cs` 覆盖 1 条用例：
- `PrepareAsync_LargeImageCreatesBoundedCopyAndKeepsOriginal`

`Feishu/FeishuInboundAudioTests.cs` 覆盖 1 条用例：
- `AudioEvent_DownloadsOnceAndMaterializesProviderSafeWavArtifact`

`Feishu/FeishuInboundImageTests.cs` 覆盖 1 条用例：
- `ImageEvent_DownloadsOnceAndMapsToCanonicalVisionArtifactMetadata`

`Feishu/FeishuInboundPostTests.cs` 覆盖 3 条用例：
- `PostEvent_MapsMarkdownAndPreservesGatewayMetadata`
- `PostEvent_WithImages_MaterializesArtifactsInOrder`
- `PostEvent_WithImage_DownloadFailure_KeepsTextAndSkipsImage`

`Feishu/SendImageToolTests.cs` 覆盖 2 条用例：
- `GenerateImage_IsTemplateGrantedControlledNetworkOperation`
- `ExecuteAsync_QueuesArtifactToCurrentTrustedFeishuRoute`

`Feishu/SendVoiceToolTests.cs` 覆盖 2 条用例：
- `ExecuteAsync_QueuesCurrentFeishuVoiceAndSuppressesFinalText`
- `ExecuteAsync_RejectsWhenChannelVoiceCapabilityIsDisabled`

`Slack/SlackGatewayTests.cs` 覆盖 11 条用例：
- `DirectoryBeforeConnectorStartDoesNotClaimCredentialRejection`
- `MemberLookupUsesOnlyTheTrustedCurrentRouteAndNeverSends`
- `RotatingCredentialFilePreservesAclAndUnknownFieldsWithExclusiveOwnership`
- `ConfigurationRegistersProviderPersistsOnlyReferenceAndRejectsDuplicateApp`
- `ConfigurationRejectsWorkspaceNamesAndGitCredentialPaths`
- `IngressRoutesEverySlackMessageIntoTheAgentMainSession`
- （其余 5 条同类用例略）
