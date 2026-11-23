# PuddingMemoryGraphTests
> 源指纹: *.cs=4-286452c1b85c, *.csproj=1-079d6642b836 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingMemoryGraph；用例全部为进程内单测，不需要真实宿主或外部服务。

`ComponentBoundaryTests.cs` 覆盖 2 条用例：
- `Component_has_no_project_references`
- `Component_assembly_only_references_the_framework`

`MemoryCueExtractorTests.cs` 覆盖 13 条用例：
- `Absolute_path_is_a_strong_cue`
- `Time_reference_words_are_strong_cues`
- `Correction_is_a_strong_cue`
- `Unfinished_marker_is_a_medium_cue`
- `Quoted_name_is_an_entity_cue`
- `Type_identifier_is_a_weak_entity_cue`
- （其余 7 条同类用例略）

`MemoryGraphTests.cs` 覆盖 19 条用例：
- `Higher_context_match_ranks_first`
- `Fresher_node_wins_when_context_equal`
- `One_hop_neighbor_beats_two_hop_and_unknown`
- `Tombstoned_nodes_are_never_ranked`
- `Ranking_is_stable_for_equal_scores`
- `Score_is_normalized_between_zero_and_one`
- （其余 13 条同类用例略）

`RecallReplayProbeTests.cs` 覆盖 1 条用例：
- `Replay_real_user_turns_to_measure_cue_coverage`
