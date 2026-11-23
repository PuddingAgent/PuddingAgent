# PuddingIndexChunkingTests
> 源指纹: *.cs=4-a3dd9aef649a, *.csproj=1-3bc40ce903cc · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingIndexChunking；用例全部为进程内单测，不需要真实宿主或外部服务。

`ChunkFilterTests.cs` 覆盖 10 条用例：
- `Rules_Are_Language_Specific`
- `Stop_Word_Matching_Is_Case_Insensitive_By_Default_And_Literal_When_Disabled`
- `Minimum_Token_Length_Uses_Language_Default_And_Honours_Override`
- `Before_And_After_Token_Sets_Differ_Exactly_By_Removed_Tokens`
- `Rule_Attribution_Moves_When_A_Rule_Is_Switched_Off`
- `Prose_Language_Keeps_Keywords_And_Only_Drops_Short_Tokens`
- （其余 4 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `Detector_Flags_Forbidden_Names`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Component_Manifest_Must_Stay_Leaf`

`FileChunkAssemblerTests.cs` 覆盖 19 条用例：
- `Pump`
- `Pump`
- `Outline_Chunks_Are_Cut_Per_Symbol_And_Never_Contain_Body_Text`
- `Symbol_Documentation_Is_Carried_By_The_Outline_Chunk_And_Its_Comment_Block_Is_Not_Indexed_Twice`
- `Comment_Block_Is_Indexed_As_DocComment_When_The_Symbol_Has_No_Documentation`
- `Documentation_Can_Be_Left_Out_Of_The_Outline_Chunk`
- （其余 13 条同类用例略）
