# PuddingRetrievalEvalProbeTests
> 源指纹: *.cs=2-b679cac5695e, *.csproj=1-dc8905f100ef · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingRetrievalEvalProbe；用例全部为进程内单测，不需要真实宿主或外部服务。

`ProductionCapabilityPolicyTests.cs` 覆盖 4 条用例：
- `VectorFaceThatDidNotRunIsNeverReportedExecuted`
- `VectorFaceStaysNotExecutedEvenWhenTheBuildCouldDriveIt`
- `RelationsFaceIsOnlyExecutedWhenAnAnchoredRequestReallyRan`
- `LexicalFaceNeedsProofOfExecution`

`ProductionProbeClaimTests.cs` 覆盖 3 条用例：
- `ZeroHitOutcomeCarriesThePortsClaimVerbatim`
- `ObservedClaimIsRetainedForTheReport`
- `EngineFailureIsNotTurnedIntoAnEmptyAnswer`
