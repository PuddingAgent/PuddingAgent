# Tests/HarnessAgent.Core.Tests
> 源指纹: *.csproj=1-d2ea9e84ea56 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 HarnessAgent.Core；用例全部为进程内单测，不需要真实宿主或外部服务。

`Email/EmailRecipientPolicyTests.cs` 覆盖 11 条用例：
- `ListedRecipientIsAllowed`
- `UnlistedRecipientIsDenied`
- `MatchingIsCaseInsensitiveAndTrimsWhitespace`
- `LineBreaksAreDeniedBecauseTheyInjectHeaders`
- `DisplayNameFormIsDenied`
- `MultipleRecipientsAreDenied`
- （其余 5 条同类用例略）

`Feishu/FeishuClientFileUploadTests.cs` 覆盖 3 条用例：
- `UploadFileAsync_Sends_Pdf_As_Declared_FileType`
- `UploadAudioAsync_Still_Uses_Opus_FileType`
- `ReplyFileAsync_Uses_File_Message_Type`

`Feishu/FeishuClientReplyTests.cs` 覆盖 7 条用例：
- `SendText_ToOpenId_DoesNotRequireChatOrReplyMessage`
- `ReplyTextAsync_UsesMessageReplyApiAndStableUuid`
- `SendMessageAsync_DefaultsToOpenIdType_AndSupportsChatIdType`
- `DownloadMessageResourceAsync_UsesAuthenticatedBoundedResourceApi`
- `AudioReply_UploadsOpusThenRepliesWithFileKeyAndStableUuid`
- `ImageReply_UploadsMessageImageThenRepliesWithImageKey`
- （其余 1 条同类用例略）

`Feishu/FeishuFileCredentialGuardTests.cs` 覆盖 4 条用例：
- `Setup`
- `Cleanup`
- `CredentialVariantsAreDenied`
- `OrdinaryFilesAreStillAllowed`

`Feishu/FeishuFileCredentialProbeTests.cs` 覆盖 1 条用例：
- `IsCredentialLike_UsesTheSameRulesAsTheSourcePolicy`

`Feishu/FeishuFileRootsTests.cs` 覆盖 5 条用例：
- `Default_Roots_Cover_The_Workspace_And_The_Output_Folder`
- `Workspace_Root_Is_Omitted_When_Workspace_Id_Is_Missing`
- `Override_Replaces_The_Defaults_Instead_Of_Appending`
- `Blank_Override_Falls_Back_To_The_Defaults`
- `Override_Variable_Name_Is_The_documented_One`

`Feishu/FeishuFileSourcePolicyTests.cs` 覆盖 15 条用例：
- `Setup`
- `Cleanup`
- `File_Inside_Allowed_Root_Is_Allowed`
- `Sibling_Root_Sharing_A_Prefix_Is_Not_Allowed`
- `Directory_Is_Rejected`
- `Missing_File_Is_Rejected`
- （其余 9 条同类用例略）

`Feishu/FeishuFileTypesTests.cs` 覆盖 4 条用例：
- `Whitelisted_Extensions_Map_To_Feishu_FileTypes`
- `Unknown_Or_Missing_Extensions_Fall_Back_To_Stream`
- `Directory_Dots_Are_Not_Treated_As_Extension`
- `Path_Prefix_Does_Not_Hide_The_Real_Extension`

`Feishu/FeishuLiveSmokeTests.cs` 覆盖 1 条用例：
- `CredentialsAndWebSocketEndpoint_AreReachable`

`Feishu/FeishuRecipientResolverTests.cs` 覆盖 7 条用例：
- `Explicit_Open_Id_Wins_Over_Chat_Target`
- `Falls_Back_To_Chat_Id_When_Open_Id_Is_Absent`
- `Blank_Open_Id_Does_Not_Shadow_The_Chat_Target`
- `Null_Metadata_Still_Resolves_The_Chat_Target`
- `Missing_Both_Is_Rejected_Rather_Than_Guessed`
- `Blank_Target_Is_Not_A_Recipient`
- （其余 1 条同类用例略）

`Feishu/FeishuWebSocketEndpointRequestTests.cs` 覆盖 1 条用例：
- `CreateEndpointRequest_MatchesOfficialDiscoveryContract`

`Feishu/FeishuWebSocketInitialPingTests.cs` 覆盖 2 条用例：
- `ConnectAsync_SendsOfficialInitialPingImmediately`
- `ConnectAsync_ReceivesEventAndReturnsSuccessfulAck`

`Feishu/ProtobufFrameTests.cs` 覆盖 11 条用例：
- `EncodeParse_RoundTripsOfficialPbbp2Fields`
- `NewPing_UsesOfficialControlMethodAndTypeHeader`
- `FeishuEvent_SnakeCasePayload_MapsMessageIdentityAndText`
- `FeishuImageEvent_SnakeCasePayload_ExtractsImageKey`
- `FeishuPostEvent_DirectContent_ConvertsToMarkdown`
- `FeishuPostEvent_LocalizedContent_UsesReadableTextFallback`
- （其余 5 条同类用例略）
