# PuddingSchedulingTests
> 源指纹: *.cs=7-9ee7cdaee9a0, *.csproj=1-c3899e597a98 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingScheduling；用例全部为进程内单测，不需要真实宿主或外部服务。

`AutomationScheduleRecordTests.cs` 覆盖 8 条用例：
- `ToState_MapsEachFieldByName_NotByPosition`
- `ToState_DoesNotConfuseNextRunWithLastFire`
- `ToState_CarriesDisabledFlag`
- `FireKey_SameInstantInDifferentOffsets_IsOneKey`
- `FireKey_TruncatesSubMinuteComponents`
- `FireKey_DifferentMinutes_AreDifferentKeys`
- （其余 2 条同类用例略）

`CronExpressionTests.cs` 覆盖 12 条用例：
- `Anchor_IsWednesday`
- `EveryMinute_AdvancesExactlyOneMinute`
- `DailyMidnight_RollsToNextDay`
- `StepOf15_From07_LandsOn15`
- `WeeklyMonday_FromWednesday_LandsOnNextMonday`
- `DayOfMonth31_SkipsApril_AndLandsOnMay31`
- （其余 6 条同类用例略）

`ScheduleStoreSchemaTests.cs` 覆盖 5 条用例：
- `Schema_AppliesCleanlyToAnEmptyDatabase`
- `Schema_RejectsASecondRowForTheSameScheduleAndFireTime`
- `Schema_RejectsDuplicateEvenWhenTheFireTimeIsExpressedWithADifferentOffset`
- `Schema_AllowsTheSameFireTimeForADifferentSchedule`
- `Schema_AllowsConsecutiveFiresForTheSameSchedule`

`ScheduleTickPlannerTests.cs` 覆盖 14 条用例：
- `Disabled_ProducesNoPlan`
- `FirstTick_SeedsCursorWithoutFiring`
- `NotYetDue_ProducesNoPlan_AndKeepsCursor`
- `DueExactly_WhenNowEqualsCursor_Fires`
- `SkipPolicy_MissedDays_FiresNothing_ButAdvancesPastNow`
- `SkipPolicy_StillFiresWithinTheScheduledMinute`
- （其余 8 条同类用例略）

`ScheduleTickServiceTests.cs` 覆盖 4 条用例：
- `InitializeAsync`
- `CleanupAsync`
- `RunDueAsync_FiresOnceWhenTimeArrivesAcrossConnections`
- `RunDueAsync_FailureLeavesTheScheduleForRetryAndContinuesTheScan`

`SqliteScheduleStoreTests.cs` 覆盖 9 条用例：
- `InitializeAsync`
- `CleanupAsync`
- `SyncDefinitionAsync_PreservesCursorOnRestartAndFencesOldPlansAfterAnEdit`
- `ListDueAsync_UsesUtcFiltersAndReturnsOldestFirst`
- `TryApplyTickAsync_ConsumesCanonicalKeyAndLinksOnlyOneTask`
- `TryApplyTickAsync_FailedTaskWriteRollsBackCursorAndFiring`
- （其余 3 条同类用例略）
