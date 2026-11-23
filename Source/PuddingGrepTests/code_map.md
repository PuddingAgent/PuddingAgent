# PuddingGrepTests
> 源指纹: *.cs=4-5e323e04ff43, *.csproj=1-c33c8cd1d200 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingGrep；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `GrepComponent_ReferencesOnlyItselfAndBcl`
- `TestProject_ReferencesOnlyTheComponent`
- `GrepCsproj_DeclaresNoForbiddenProjectReferences`

`GrepMatchOutcomeTests.cs` 覆盖 5 条用例：
- `Match_LiteralHit_ReturnsMatched_WithSpans`
- `Match_LiteralMiss_ReturnsNoMatch_WithoutSpans`
- `Match_RegexCatastrophicBacktracking_ReturnsTimeout_NotException`
- `Match_InvalidPattern_StillThrows`
- `Matched_RejectsEmptySpans`

`GrepMatcherOracleTests.cs` 覆盖 6 条用例：
- `Literal_Matches_SameAs_BCL_IndexOf`
- `Literal_CaseSensitive_Matches_SameAs_Ordinal_IndexOf`
- `Regex_Matches_SameAs_BCL_Regex`
- `Literal_IsNotTreatedAsRegex`
- `FindAll_Literal_SpansMatchAndDoNotOverlap`
- `FindAll_Regex_MatchesBclIndexes`

`GrepPatternValidationTests.cs` 覆盖 5 条用例：
- `EmptyLiteral_IsRejected`
- `EmptyRegex_IsRejected`
- `InvalidRegex_IsRejectedAtConstruction`
- `ValidPatterns_HaveNoValidationError`
- `CatastrophicBacktracking_TimesOutInsteadOfHanging`
