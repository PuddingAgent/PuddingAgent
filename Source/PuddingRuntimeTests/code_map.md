# PuddingRuntimeTests
> 源指纹: *.cs=1-5419fce94538, *.csproj=1-669b1df173ff · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingRuntime、PuddingPlatform、PuddingRetrieval；用例全部为进程内单测，不需要真实宿主或外部服务。

`SubAgentResultPreviewTests.cs` 覆盖 3 条用例：
- `Condense_ReturnsShortResultUnchanged`
- `Condense_TruncatesLongResultAndMarksTotalLength`
- `Condense_HandlesNullAndEmpty`
