# PuddingHtmlTests
> 源指纹: *.cs=3-585d271d84b8, *.csproj=1-1e4d7fac58c0 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingHtml；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 5 条用例：
- `PuddingHtml_Assembly_References_No_Pudding_Assembly`
- `PuddingHtml_Csproj_Declares_No_ProjectReference`
- `PuddingHtml_Csproj_Keeps_Package_Surface_Narrow`
- `PuddingHtmlTests_Csproj_References_Exactly_One_Project`
- `TestProcess_Does_Not_Load_Host_Or_Browser_Assemblies`

`HtmlContentExtractorTests.cs` 覆盖 8 条用例：
- `ExtractReadableHtml_Removes_Scripts_Styles_And_Chrome`
- `ExtractReadableHtml_Prefers_Main_Over_Article_Over_Body`
- `ExtractReadableHtml_Falls_Back_To_Body_When_No_Landmark_Exists`
- `ExtractReadableHtml_Returns_Empty_For_Blank_Input`
- `ToPlainText_Decodes_Entities_And_Collapses_Whitespace`
- `IsHtml_Trusts_A_ContentType_That_Mentions_Html`
- （其余 2 条同类用例略）

`HtmlToMarkdownConverterTests.cs` 覆盖 6 条用例：
- `Converts_Headings_To_Atx_Form`
- `Converts_Links_And_Emphasis`
- `Converts_Lists_To_Dash_Bullets`
- `Drops_Html_Comments`
- `Preserves_Fenced_Code_Blocks`
- `Returns_Empty_For_Empty_Input`
