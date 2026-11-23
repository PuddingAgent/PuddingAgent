# PuddingDesktop.FoundationTests
> 源指纹: *.cs=21-d8a7a23321f5, *.csproj=1-75d086193303 · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingDesktop.Foundation；用例全部为进程内单测，不需要真实宿主或外部服务。

`DesignTokenTests.cs` 覆盖 21 条用例：
- `FontSizes_AreUniqueNamedAndAscending`
- `CornerRadii_AreUniqueNamedAndAscending`
- `MapFontSize_CoversEveryLiteralObservedInMainWindow`
- `MapFontSize_AlwaysLandsOnADefinedTier`
- `MapCornerRadius_CoversEveryLiteralObservedInMainWindow`
- `Lint_FlagsRawFontSize`
- （其余 15 条同类用例略）

`DocumentTabTests.cs` 覆盖 4 条用例：
- `Document_is_ready_because_webview2_is_a_present_capability`
- `Document_identity_folds_separator_and_casing`
- `Different_documents_do_not_collapse_into_one_tab`
- `Reopening_the_same_document_keeps_one_tab_and_it_can_close`

`LayoutAndSettingsTests.cs` 覆盖 4 条用例：
- `ResponsiveLayoutPreservesWorkbench`
- `InvalidDimensionsAreNormalized`
- `CorruptSettingsPreservedAndReported_AtomicSaveRoundTrips`
- `AssemblyHasNoUiOrHostDependency`

`PanelAnnotationSnapshotWriterTests.cs` 覆盖 9 条用例：
- `Writes_a_camel_case_envelope_readable_by_the_core_side`
- `Publish_leaves_no_temp_file_behind`
- `Empty_payload_never_overwrites_the_previous_snapshot`
- `Payload_that_is_not_a_json_object_is_refused`
- `Illegal_panel_id_is_refused_instead_of_being_sanitized`
- `Missing_directory_is_created`
- （其余 3 条同类用例略）

`PanelAnnotationTests.cs` 覆盖 45 条用例：
- `Converts_pixels_to_the_documented_zero_to_999_range`
- `Rounds_half_away_from_zero_so_boundaries_do_not_flicker`
- `Clamps_pixels_outside_the_image_instead_of_going_out_of_range`
- `Rejects_a_non_positive_extent`
- `Rejects_a_non_finite_pixel`
- `Normalizes_a_drag_in_any_direction`
- （其余 39 条同类用例略）

`PanelCanvasDocumentTests.cs` 覆盖 17 条用例：
- `Embedding_document_carries_the_sandbox_policy`
- `Embedding_policy_allows_local_images_but_still_blocks_everything_else`
- `Embedding_document_injects_the_annotation_layer_but_export_does_not`
- `Annotation_layer_offers_a_per_item_remove_button`
- `Annotation_layer_exposes_drawing_tools_and_compositing`
- `Canvas_take_returns_items_verbatim_so_scribbles_keep_their_points`
- （其余 11 条同类用例略）

`PanelCanvasFeedIdentityTests.cs` 覆盖 5 条用例：
- `Accepts_the_snapshot_when_it_is_for_the_requested_panel`
- `Rejects_a_snapshot_belonging_to_another_panel`
- `Stays_permissive_when_no_panel_is_expected`
- `Compares_panel_ids_case_sensitively`
- `Compares_against_the_trimmed_panel_id`

`PanelCanvasFeedTests.cs` 覆盖 14 条用例：
- `BuildUri_points_at_the_canvas_route`
- `BuildUri_escapes_a_hostile_panel_id`
- `BuildUri_rejects_a_relative_address`
- `TryParse_reads_the_snapshot`
- `TryParse_normalizes_a_blank_title_to_null`
- `TryParse_rejects_a_missing_revision_instead_of_defaulting_it`
- （其余 8 条同类用例略）

`PanelCanvasRestoreTests.cs` 覆盖 5 条用例：
- `Empty_items_produce_no_script_at_all`
- `Script_waits_for_the_annotation_layer_then_calls_load`
- `Payload_carries_normalized_coordinates_for_every_kind`
- `Scribble_payload_carries_the_trail_not_just_the_box`
- `Legacy_scribble_without_a_trail_falls_back_to_its_box`

`PanelCanvasStateTests.cs` 覆盖 5 条用例：
- `Malformed_snapshots_are_rejected_instead_of_guessed`
- `Snapshot_reports_the_active_tool_and_the_annotation_count`
- `Optional_fields_default_to_idle_when_the_page_omits_them`
- `Wrongly_typed_optional_fields_degrade_to_idle_not_to_an_error`
- `An_unknown_tool_name_is_still_accepted`

`PanelCanvasThemeTests.cs` 覆盖 8 条用例：
- `Light_and_dark_differ_on_every_slot`
- `For_maps_each_scheme_to_its_own_palette`
- `PageArgb_matches_the_palette_page`
- `Declare_declares_color_scheme`
- `Declare_emits_every_documented_variable`
- `Declare_is_a_single_well_formed_root_block`
- （其余 2 条同类用例略）

`PanelCraftStoreTests.cs` 覆盖 41 条用例：
- `Dispose`
- `Save_writes_both_the_image_and_the_record`
- `Save_records_the_real_size_and_hash_of_the_source_bytes`
- `Save_rejects_a_non_image_extension`
- `Save_rejects_a_missing_source_file`
- `Save_falls_back_to_the_id_when_the_title_is_blank`
- （其余 35 条同类用例略）

`PanelImageSourceTests.cs` 覆盖 16 条用例：
- `Builds_a_readable_uri_for_a_plain_path`
- `Escapes_spaces_and_non_ascii_instead_of_emitting_them_raw`
- `Round_trips_a_path_that_needs_escaping`
- `Rejects_a_relative_path`
- `Accepts_a_file_under_an_allowed_root`
- `Rejects_a_file_outside_every_allowed_root`
- （其余 10 条同类用例略）

`PanelMarkRetentionTests.cs` 覆盖 11 条用例：
- `Dispose`
- `KeepsTheNewestFilesAndDeletesTheRest`
- `DoesNothingWhenUnderTheLimit`
- `NeverDeletes_a_mark_that_was_inserted_into_the_conversation`
- `UsedMarks_do_not_consume_the_keep_budget`
- `Prune_removes_a_used_marker_whose_image_is_gone`
- （其余 5 条同类用例略）

`PanelTabTests.cs` 覆盖 3 条用例：
- `Panel_is_ready_because_agent_can_push_content`
- `Panel_is_one_canvas_and_reopening_activates_it`
- `Panel_can_close_but_home_cannot`

`StartupPanelPresenterTests.cs` 覆盖 50 条用例：
- `Phase_table_maps_title_ring_steps_action_visibility_and_status_tone`
- `Phase_table_maps_tone_and_error_icon`
- `Phase_visibility_follows_whether_phase_text_has_content`
- `Panel_is_visible_for_every_phase_except_ready`
- `Every_phase_exposes_non_null_text_fields_and_steps`
- `Status_bar_text_without_core_address_is_phase_specific`
- （其余 44 条同类用例略）

`ThemeResourceAuditTests.cs` 覆盖 9 条用例：
- `FindBrushKeySetDrift_ReportsKeysMissingOnOneSideOnly`
- `FindBrushKeySetDrift_AllowsDifferentValuesForTheSameKey`
- `FindBrushKeySetDrift_RequiresDefaultAndLight`
- `FindUnresolvedReferences_AcceptsFileKeysPlatformKeysAndExternalKeys`
- `FindUnresolvedReferences_AcceptsRegisteredPlatformKey`
- `ReadReferences_IgnoresMixedExpressions`
- （其余 3 条同类用例略）

`ToolAvailabilityCatalogTests.cs` 覆盖 5 条用例：
- `For_matches_the_real_capability`
- `CardLabel_is_derived_from_availability`
- `CardSubtitle_never_contradicts_availability`
- `IsEntryEnabled_follows_availability`
- `Every_enum_member_is_decided`

`ToolWorkspaceInitialRatioTests.cs` 覆盖 9 条用例：
- `Launcher_only_ratio_sits_inside_the_designed_range`
- `Initial_ratio_depends_on_whether_only_the_launcher_is_shown`
- `Context_reset_target_follows_the_same_rule`
- `Saved_preference_always_wins_over_the_initial_default`
- `Unconfigured_falls_back_to_the_initial_default`
- `Non_finite_saved_value_is_treated_as_unconfigured`
- （其余 3 条同类用例略）

`ToolWorkspaceTests.cs` 覆盖 25 条用例：
- `DefaultPreferenceIsCollapsedAtFortyFivePercent`
- `SplitKeepsChatAndToolWidthsAdditive`
- `DraggedWidthBelowMinimumIsClamped`
- `DraggedRatioIsReclampedAgainstTheCurrentChatMinimum`
- `NarrowWindowOverlaysTheChatInsteadOfSqueezingIt`
- `ZoomedWorkspaceTakesEverything`
- （其余 19 条同类用例略）

`WorkbenchAppearanceTests.cs` 覆盖 10 条用例：
- `ParsePreference_maps_saved_theme_with_system_fallback`
- `Combo_index_round_trips`
- `Out_of_range_combo_index_falls_back_to_system`
- `Resolve_prefers_explicit_choice_then_falls_back_to_system`
- `Background_uses_the_web_layout_truth_source_colors`
- `Argb_parts_are_fully_opaque_and_decompose`
- （其余 4 条同类用例略）
