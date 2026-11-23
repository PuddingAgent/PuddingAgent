# PuddingVectorIndexTests
> 源指纹: *.cs=8-1f1bd4f3540e, *.csproj=1-78d7cb0a1716 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingVectorIndex；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 7 条用例：
- `Detector_Flags_Forbidden_Names`
- `Source_Token_Detector_Flags_Http_And_Secret_Tokens`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Component_Assembly_Must_Not_Reference_Http_Database_Or_Lucene`
- `Component_Sources_Must_Not_Mention_Http_Or_Credentials`
- `Component_Csproj_Must_Declare_Zero_Project_And_Package_References`
- （其余 1 条同类用例略）

`InMemoryVectorIndexTests.cs` 覆盖 11 条用例：
- `Search_Ranks_By_Cosine_Descending`
- `Search_Breaks_Ties_By_Id_So_Two_Runs_Agree`
- `Search_Respects_TopK_And_Returns_Whole_Index_When_Asked_For_More`
- `Search_On_Empty_Index_Returns_Empty_List`
- `Add_Rejects_Dimension_Mismatch_Instead_Of_Truncating`
- `Add_Rejects_Duplicate_Id_And_Zero_Vector`
- （其余 5 条同类用例略）

`QuantizedInMemoryVectorIndexTests.cs` 覆盖 6 条用例：
- `A1_Shipped_Search_Semantics_Are_Still_Pinned_By_Hand_Computed_Values`
- `A2_In_Place_Scan_Reproduces_The_Shipped_Ranking_Bit_For_Bit`
- `A3_Ties_Are_Broken_By_Id_Ordinal_Not_By_Insertion_Order`
- `A4_Query_Allocation_Is_Bounded_By_K_Not_By_The_Corpus`
- `A5_Fail_Closed_Rules_Are_Identical_On_Both_Paths`
- `Rows_Keep_Provenance_And_Route_Through_The_Scan`

`VectorIndexBuilderTests.cs` 覆盖 12 条用例：
- `Empty_Input_Produces_An_Empty_Index_And_No_Calls`
- `Batch_And_Single_Embedding_Produce_The_Same_Index`
- `Vector_Belongs_To_Its_Own_Document_Regardless_Of_Batching`
- `Unavailable_Provider_Fails_Instead_Of_Building_An_Empty_Index`
- `Undeclared_Dimension_Fails_Closed`
- `Provider_Returning_Wrong_Vector_Length_Fails_Closed`
- （其余 6 条同类用例略）

`VectorLayeredBuildTests.cs` 覆盖 7 条用例：
- `A1_Default_Options_Keep_The_Unfiltered_Unquantised_Build`
- `A5_Quantised_Search_Is_Repeatable_And_Breaks_Ties_By_Id_Ordinal`
- `A6_The_Tier_Filter_Keeps_Exactly_The_Selected_Blocks`
- `A7_Filtering_Only_Removes_Blocks_It_Never_Changes_The_Survivors`
- `A8_Filtered_Empty_Is_Reported_As_Filtered_Empty_Not_As_Nothing_To_Index`
- `A9_Int8_Storage_Is_Smaller_Than_Float32_And_P0_Is_Smaller_Than_Every_Tier`
- （其余 1 条同类用例略）

`VectorMathTests.cs` 覆盖 9 条用例：
- `Dot_Matches_Hand_Computed_Value`
- `Norm_Of_Pythagorean_Triple_Is_Five`
- `Cosine_Of_Identical_Direction_Is_One_And_Opposite_Is_Minus_One`
- `Cosine_Of_Orthogonal_Vectors_Is_Zero`
- `Cosine_Matches_Hand_Computed_Value`
- `Cosine_Is_Scale_Invariant`
- （其余 3 条同类用例略）

`VectorQuantizationTests.cs` 覆盖 5 条用例：
- `A2_Round_Trip_Error_Never_Exceeds_Half_A_Scale_Step`
- `A2_Round_Trip_Scales_With_The_Data_Not_With_A_Constant`
- `A3_Dimension_Mismatch_And_Degenerate_Inputs_Fail_Closed`
- `A4_Scale_Must_Be_Positive_And_Finite`
- `Quantised_Vectors_Are_Copied_And_Scored_Through_The_Float_Cosine`
