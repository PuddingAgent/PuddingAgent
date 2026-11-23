# PuddingPathFilteringTests
> 源指纹: *.cs=10-505c7e2550b4, *.csproj=1-ce9b3e1c7310 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingPathFiltering；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Leaf_project_has_no_project_or_package_references`
- `Decision_chain_does_not_touch_the_file_system`
- `Leaf_does_not_reference_any_other_pudding_component`
- `Test_project_references_only_the_leaf`

`GitIgnoreFileLoaderTests.cs` 覆盖 6 条用例：
- `SetUp`
- `TearDown`
- `LoadsRootAndNestedGitIgnoreWithTheNestedOneWinning`
- `NoiseDirectoriesAreNotDescendedInto`
- `MissingRootYieldsEmptyStack`
- `RealRepositorySnapshotLoadsItsTwoGitIgnoreFiles`

`GitIgnoreOracleTests.cs` 覆盖 6 条用例：
- `Real_workspace_gitignore_agrees_with_git_check_ignore`
- `Synthetic_workspace_gitignore_agrees_with_git_check_ignore`
- `Oracle_corpus_size_is_at_least_one_hundred_paths`
- `Oracle_corpus_contains_negation_decided_rows`
- `Oracle_corpus_contains_parent_directory_pruning_rows`
- `Windows_case_folding_is_required_to_match_git`

`GitWildcardTests.cs` 覆盖 7 条用例：
- `SingleStarAndQuestionDoNotCrossSeparator`
- `DoubleStarSemantics`
- `CharacterClasses`
- `UnterminatedClass_never_matches`
- `EscapesAndEscapedMetacharacters`
- `BracesAreLiteral`
- （其余 1 条同类用例略）

`IgnoreFileParserTests.cs` 覆盖 10 条用例：
- `CommentsAndBlankLinesAreNotRules`
- `HashAndBangCanBeEscaped`
- `NegationIsRecognised`
- `TrailingSpacesAreTrimmedButEscapedOnesSurvive`
- `LeadingSpacesAreSignificant`
- `TrailingSlashMeansDirectoryOnly`
- （其余 4 条同类用例略）

`IgnoreStackTests.cs` 覆盖 10 条用例：
- `LastMatchingRuleWins`
- `OrderMatters_LaterRuleOverridesEarlierNegation`
- `DeeperGitIgnoreFileOverridesRoot`
- `ParentDirectoryExclusionCannotBeNegated`
- `DirectoryOnlyRulesNeedDirectoryKind`
- `NonAnchoredRulesMatchTheBasenameAtAnyDepth`
- （其余 4 条同类用例略）

`PathNoiseRulesTests.cs` 覆盖 10 条用例：
- `Required_noise_directories_are_present`
- `Atomic_write_temp_directories_are_noise_on_every_entry_point`
- `Suffix_rule_does_not_widen_to_ordinary_files`
- `Union_of_the_three_documented_rule_sets_is_covered`
- `Canonical_set_is_exactly_the_union_plus_the_declared_additions`
- `SegmentsAreMatchedExactlyNotByPrefix`
- （其余 4 条同类用例略）

`RepositoryRootReductionTests.cs` 覆盖 3 条用例：
- `Canonical_set_covers_at_least_eighty_percent_of_the_indexable_corpus`
- `Every_dominant_noise_directory_is_excluded_at_the_repository_root`
- `Dominant_noise_directories_account_for_the_recorded_share_of_the_baseline`
