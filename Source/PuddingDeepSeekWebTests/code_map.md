# PuddingDeepSeekWebTests
> 源指纹: *.cs=4-82ebf20c6603, *.csproj=1-880c472f995b · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingDeepSeekWeb；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 7 条用例：
- `Detector_Flags_Forbidden_Assembly_Names`
- `Source_Token_Detector_Flags_Http_Client_But_Not_Protocol_Knowledge`
- `Test_Process_Must_Not_Load_Forbidden_Assemblies`
- `Component_Assembly_Must_Not_Reference_Upper_Layers_Or_Http_Stack`
- `Component_Sources_Must_Not_Use_Http_Client`
- `Component_Csproj_Must_Declare_Zero_Project_And_Package_References`
- （其余 1 条同类用例略）

`DeepSeekWebLiveTests.cs` 覆盖 2 条用例：
- `Live_Search_Returns_DeepSeek_Docs_Results`
- `Live_Fetch_Returns_Markdown_Content_For_Search_Result`

`DeepSeekWebServiceTests.cs` 覆盖 39 条用例：
- `SearchAsync_Builds_Official_Request_Shape`
- `SearchAsync_Omits_Absent_Optional_Fields`
- `SearchAsync_Whitespace_Optional_Fields_Are_Omitted`
- `SearchAsync_Trims_Query_And_Optional_Fields`
- `SearchAsync_Custom_Base_Url_With_Trailing_Slash_Is_Normalized`
- `FetchAsync_Builds_Official_Request_Shape`
- （其余 33 条同类用例略）
