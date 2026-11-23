# Tests/PuddingDesktop.Diagnostics.Tests
> 源指纹: *.cs=1-de57523fa949, *.csproj=1-dd4ca4c481c6 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingDesktop.Diagnostics；用例全部为进程内单测，不需要真实宿主或外部服务。

`DesktopRunJournalTests.cs` 覆盖 8 条用例：
- `ClassifiesEvidenceWithoutGuessingInitiator`
- `FatalEvidenceSurvivesLaterHeartbeatAndExitCompletion`
- `RecoveryPreservesLastSceneAndIsIdempotent`
- `RecoveryNeverMarksActiveOrInaccessibleProcessAsDead`
- `RedactsSecretsBeforePersistingAndBoundsPayload`
- `JournalRotatesAndRetentionKeepsActiveRuns`
- （其余 2 条同类用例略）
