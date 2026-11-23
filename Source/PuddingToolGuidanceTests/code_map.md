# PuddingToolGuidanceTests
> 源指纹: *.cs=7-02547603626f, *.csproj=1-69c2a1b28fe3 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingToolGuidance；用例全部为进程内单测，不需要真实宿主或外部服务。

`BuildEfficiencyRulesTests.cs` 覆盖 8 条用例：
- `First_dotnet_test_run_does_not_advise`
- `Second_dotnet_test_run_advises`
- `Command_already_using_no_build_never_advises`
- `Non_dotnet_command_never_advises`
- `Single_timeout_does_not_advise`
- `Repeated_timeout_advises_blame_hang`
- （其余 2 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 3 条用例：
- `Component_csproj_declares_no_project_reference`
- `Component_assembly_references_only_bcl`
- `Test_project_references_only_the_component`

`GitAndToolingRulesTests.cs` 覆盖 6 条用例：
- `Add_all_should_advise`
- `Explicit_paths_should_not_advise_for_add_all`
- `Stash_on_a_chain_should_advise`
- `Standalone_stash_should_not_advise`
- `Python_alias_should_advise`
- `Non_invocation_should_not_advise`

`GuidanceEngineTests.cs` 覆盖 6 条用例：
- `Default_max_advices_is_three`
- `Duplicate_rule_ids_are_removed`
- `Throwing_rule_does_not_break_evaluation`
- `No_advice_returns_empty_and_keeps_output_untouched`
- `Append_adds_note_after_output_without_touching_it`
- `Append_on_empty_output_returns_only_notes`

`RepeatedFailureTests.cs` 覆盖 7 条用例：
- `Advance`
- `Below_threshold_does_not_advise`
- `At_or_above_threshold_advises`
- `Counter_accumulates_until_success_resets`
- `Counter_is_per_key`
- `Counter_resets_after_window_expires`
- （其余 1 条同类用例略）

`ResultQualityRulesTests.cs` 覆盖 5 条用例：
- `Incomplete_search_output_should_advise`
- `Clean_search_output_should_not_advise`
- `Null_output_should_not_advise`
- `Filter_with_no_match_should_advise`
- `Normal_test_output_should_not_advise`

`ShellDialectRulesTests.cs` 覆盖 13 条用例：
- `Unix_command_in_cmd_should_advise`
- `Unix_command_elsewhere_should_not_advise`
- `Pwsh_cmdlet_in_cmd_should_advise`
- `Pwsh_cmdlet_in_pwsh_should_not_advise`
- `Bash_variable_outside_bash_should_advise`
- `Bash_variable_in_bash_or_pwsh_env_should_not_advise`
- （其余 7 条同类用例略）
