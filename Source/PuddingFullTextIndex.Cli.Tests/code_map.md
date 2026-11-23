# PuddingFullTextIndex.Cli.Tests
> 源指纹: *.cs=8-8598e4f9ec9b, *.csproj=1-87f2f2b5cca2 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingFullTextIndex.Cli；用例全部为进程内单测，不需要真实宿主或外部服务。

`A19SingleSourceTests.cs` 覆盖 4 条用例：
- `A1_Frozen_Hash_Literals_Match_Golden_Helper_And_Engine`
- `A1_Golden_Helper_Engine_Resolve_And_Probe_Agree_On_Six_Boundary_Classes`
- `A2_Cli_Printed_ScopeKey_And_IndexDirectory_Are_Byte_For_Byte_The_Frozen_Golden`
- `A4_Cli_Production_Project_Has_No_Replica_Of_The_Naming_Hash`

`SupplyCliBuildTests.cs` 覆盖 13 条用例：
- `Build_Wait_With_Real_Lucene_Succeeds_And_Status_Sees_The_Index`
- `Build_Json_Scope_Key_Matches_The_Component_Normalizer`
- `Build_On_A_Missing_Scope_Is_Rejected_With_Exit_Code_2`
- `Build_When_The_Lease_Is_Held_Elsewhere_Is_Busy_With_Exit_Code_3`
- `Build_Without_Wait_Only_Reports_Acceptance`
- `Build_With_A_Failing_Builder_Exits_4_When_Waiting`
- （其余 7 条同类用例略）

`SupplyCliFixture.cs` 覆盖 1 条用例：
- `Dispose`

`SupplyCliParsingTests.cs` 覆盖 12 条用例：
- `NoArguments_Is_A_Usage_Error`
- `Unknown_Command_Is_A_Usage_Error`
- `Plan_Without_Scope_Is_A_Usage_Error`
- `Plan_Rejects_Options_It_Does_Not_Support`
- `Build_Without_Scope_Is_A_Usage_Error`
- `Build_Without_Index_Root_Is_A_Usage_Error_And_Never_Builds`
- （其余 6 条同类用例略）

`SupplyCliPlanTests.cs` 覆盖 8 条用例：
- `Plan_Is_A_Dry_Run_That_Writes_Nothing_And_Matches_The_Disk`
- `Plan_Accepts_Two_Non_Overlapping_Scopes`
- `Plan_Rejects_Nested_Scopes_With_The_Right_Reason_And_Exit_Code`
- `Plan_Rejects_A_Missing_Scope_Directory_With_Exit_Code_2`
- `Plan_Reports_A_Budget_Overrun_Without_Rejecting`
- `Plan_Json_Has_The_Expected_Shape`
- （其余 2 条同类用例略）

`SupplyCliStatusTests.cs` 覆盖 8 条用例：
- `Status_Job_Lookup_Miss_Reports_Not_In_This_Process`
- `Status_Json_Job_Lookup_Miss_Reports_Not_In_This_Process`
- `Status_Job_Lookup_Hit_Reports_The_Job_From_This_Process`
- `Status_Reports_Disk_Observable_State_Without_An_Index`
- `Status_Reports_The_Lease_Holder_When_One_Exists`
- `Status_Prints_The_Default_Index_Root_When_Omitted`
- （其余 2 条同类用例略）
