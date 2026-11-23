# Pudding.Rpc.ProtocolTests
> 源指纹: *.cs=4-a2d55ecf2fae, *.csproj=1-a67021235148 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 Pudding.Rpc.Protocol；用例全部为进程内单测，不需要真实宿主或外部服务。

`ProtocolBoundaryTests.cs` 覆盖 5 条用例：
- `ProtocolAssembly_ReferencesOnlyProtobufRuntimeAndGrpcApi`
- `ProtocolCsproj_HasNoProjectReferenceAndOnlyAllowedPackages`
- `ProtocolProject_ContainsProtoSourcesAndNoHandWrittenCSharp`
- `ProtoSource_DeclaresVersionedPackage`
- `ProtocolTestsCsproj_ReferencesExactlyOneProject`

`SerializationRoundTripTests.cs` 覆盖 8 条用例：
- `EmptyPayload_ParsesAsNoOneofCase`
- `Command_WithNavigatePayload_RoundTrips`
- `Result_OutcomeVariants_AreMutuallyExclusive`
- `EventsAndHeartbeats_RoundTrip`
- `PageStateCommandAndOutcome_RoundTrip`
- `ShellStatusCommandAndOutcome_RoundTrip`
- （其余 2 条同类用例略）

`WireContractSnapshotTests.cs` 覆盖 7 条用例：
- `Service_HasSingleBidirectionalConnectMethod`
- `GeneratedStubs_AreBothClientAndServer`
- `FrameFieldNumbers_AreFrozen`
- `HandshakeFieldNumbers_AreFrozen`
- `CommandAndResultFieldNumbers_AreFrozen`
- `PayloadOneof_IsClosedWhitelist`
- （其余 1 条同类用例略）
