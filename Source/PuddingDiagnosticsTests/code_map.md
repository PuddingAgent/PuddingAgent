# PuddingDiagnosticsTests
> 源指纹: *.cs=6-39becdb0e415, *.csproj=1-ab24516aa3cd · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingDiagnostics；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 7 条用例：
- `Detector_Flags_Forbidden_Names`
- `Source_Token_Detector_Flags_Forbidden_Means`
- `Component_Sources_UseOnlyBclAndNeverSpeakToAStoreOrLogger`
- `Component_Csproj_DeclaresZeroProjectAndPackageReferences`
- `Test_Csproj_ReferencesOnlyTheComponent`
- `Test_Process_MustNotLoadForbiddenAssemblies`
- （其余 1 条同类用例略）

`DiagnosticEvidenceTests.cs` 覆盖 7 条用例：
- `SensitiveKeys_AreRedacted_BySubstringNotExactMatch`
- `SecretShapedValues_AreRedacted_EvenUnderInnocentKeys`
- `UrlQueryStrings_AreStripped`
- `Value_IsTruncatedAtTheDocumentedLimit`
- `Builder_StopsAtTheKeyBudget_AndSaysSoInsteadOfSilentlyDropping`
- `Builder_FirstValueWins_AndOutputOrderIsDeterministic`
- （其余 1 条同类用例略）

`ErrorPresentationTests.cs` 覆盖 8 条用例：
- `CompactionReportPreservesCapacityReason_InsteadOfInventingAnUploadOverflow`
- `Presentation_IsFriendly_AndNeverEchoesTheTransportMessage`
- `Presentation_ShowsTheRawCodeForUnknownCauses`
- `CopiedReport_CarriesEveryFieldNeededToLocateTheIncident`
- `CopiedReport_JsonIsCompleteAndStable`
- `CopiedReport_NeverContainsSecretShapedValues`
- （其余 2 条同类用例略）

`FaultScenarioTests.cs` 覆盖 7 条用例：
- `EveryScenario_ProducesItsExpectedCause`
- `IncidentReplica_ClassifiesAsRequestUploadReset_WithFullEvidence`
- `DefaultUploadFrameFingerprints_MatchTheObservedProductionStack`
- `ResetWithoutPhaseSignal_StaysUnclassified`
- `ResetWithUploadPhaseHint_ClassifiesAsRequestUploadReset`
- `Retryable_IsForcedFalse_AfterAnyDeltaHasBeenYielded`
- （其余 1 条同类用例略）

`IncidentProjectorTests.cs` 覆盖 4 条用例：
- `Incident_20261007_IsReducedToAttemptsPhaseCauseAndSize`
- `Projector_IsScopedToTheRequestedTurn`
- `Projector_WithoutFacts_ReportsUnknownInsteadOfHealthy`
- `FactQuery_ClampsLimit_AndReportsTruncationExplicitly`
