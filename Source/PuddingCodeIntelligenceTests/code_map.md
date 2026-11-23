# PuddingCodeIntelligenceTests
> 源指纹: *.cs=1-26ea30afb60b, *.csproj=1-11ae2807fb6d · 条目数: 0 · 最近整理: 2026-11-23

测试工程，引用 PuddingCodeIndex、PuddingCodeIntelligence；用例全部为进程内单测，不需要真实宿主或外部服务。

`Bicep/BicepFileOutlinerTests.cs` 覆盖 8 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsBicep`
- `OutlineAsync_Parameters_Extracted`
- `OutlineAsync_Resource_Extracted`
- `OutlineAsync_VarsAndOutputs_Extracted`
- `OutlineAsync_Module_Extracted`
- `OutlineAsync_Type_Extracted`
- （其余 2 条同类用例略）

`CSharp/ProjectOpenPlannerTests.cs` 覆盖 7 条用例：
- `SamePathListedTwice_OpensOnce`
- `SamePathInDifferentCase_IsTreatedAsOneProject`
- `ProjectAlreadyLoadedAsDependency_IsNotOpenedAgain`
- `OnlyTheMissingProjectIsOpened_OrderPreserved`
- `NullEmptyAndWhitespaceEntries_AreIgnoredWithoutFailingTheBatch`
- `RelativeAndAbsolutePathsForTheSameProject_DedupeToTheFirstOne`
- （其余 1 条同类用例略）

`CSharp/RoslynCSharpIndexerBatchTests.cs` 覆盖 10 条用例：
- `Initialize`
- `OneWorkspaceIsOpenedForTheWholeBatchAndEveryFileGetsAPayload`
- `BatchNeverWritesToTheIndex`
- `NonCSharpAndUnknownFilesAreNotApplicable`
- `MissingProjectRootIsRetryableAndNeverOpensTheWorkspace`
- `AWorkspaceThatCannotBeOpenedMarksEveryCSharpPathRetryable`
- （其余 4 条同类用例略）

`CSharp/RoslynCSharpIndexerRemovalTests.cs` 覆盖 5 条用例：
- `Initialize`
- `Full_Re_Index_Does_Not_Sweep_A_File_Missing_From_The_Compilation_But_Per_File_Removal_Does`
- `IndexFileAsync_MissingFile_Is_Reported_As_Failed`
- `IndexFileAsync_FileUnderANoiseDirectory_Is_Reported_As_Failed`
- `IndexFileAsync_MissingProjectPath_Is_Reported_As_Failed`

`CSharp/RoslynCSharpIndexerTests.cs` 覆盖 17 条用例：
- `Initialize`
- `IndexCompilationAsync_ShouldExtractSymbolsAndContainsRelations`
- `IndexCompilationAsync_ShouldExtractCallsRelations`
- `IndexCompilationAsync_ShouldExtractReferences`
- `IndexCompilationAsync_ShouldPersistFiles`
- `IndexCompilationAsync_EndLineCoversDeclarationSpan_NotJustIdentifierLine`
- （其余 11 条同类用例略）

`ComponentBoundaryTests.cs` 覆盖 4 条用例：
- `Boundary_Detector_Flags_Forbidden_Names`
- `ComponentCsproj_DeclaresOnlyCodeIndex`
- `TestCsproj_ReferencesComponentExplicitly_AndNotRuntime`
- `ComponentAssembly_DoesNotReferenceUpperLayers`

`Contracts/CodeIntelligenceContractTests.cs` 覆盖 4 条用例：
- `CodeProjectRecord_And_Add_Request_Store_Expected_Values`
- `Symbol_Relation_And_Reference_Contracts_Compile_And_Contain_Expected`
- `Index_And_Lsp_Contracts_Expose_Expected_Results`
- `Query_Container_Descriptors_Exist`

`Cpp/CppFileOutlinerTests.cs` 覆盖 3 条用例：
- `SupportedExtensions_Include_C_And_Cpp_Header_Forms`
- `OutlineAsync_ClassStructEnumAndFunctions_AreExtracted`
- `OutlineAsync_Ignores_ControlFlow_Inside_FunctionBodies`

`Extractors/ExtractorAssetResolverTests.cs` 覆盖 8 条用例：
- `Initialize`
- `Resolve_TypeScriptAssetsPresent_ResolvesFromInjectedBaseDirectory`
- `Resolve_PythonScriptPresent_SucceedsWithoutNodeModules`
- `Resolve_ScriptMissing_ReportsAssetMissingWithExpectedPath`
- `Resolve_NodeModulesMissing_ReportsNodeModulesMissing`
- `Resolve_AssetsOnlyInAncestorDirectory_DoesNotWalkUp`
- （其余 2 条同类用例略）

`Extractors/ExtractorSubprocessEnvironmentTests.cs` 覆盖 2 条用例：
- `ApplyNodeModulesPath_SetsChildEnvironmentOnly`
- `ApplyNodeModulesPath_NullPath_LeavesChildEnvironmentUnchanged`

`Json/JsonFileOutlinerTests.cs` 覆盖 7 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsJson`
- `OutlineAsync_SimpleObject_TopLevelKeysExtracted`
- `OutlineAsync_NestedObjects_KeysExtractedWithContainer`
- `OutlineAsync_ComplexJsonFile`
- `OutlineAsync_EmptyObject_ReturnsEmpty`
- `OutlineAsync_InvalidJson_ReturnsFallback`
- （其余 1 条同类用例略）

`Lsp/NoOpLanguageServerServiceTests.cs` 覆盖 1 条用例：
- `ExecuteAsync_Returns_Unsupported_Response_With_Correlation_Id`

`Markdown/MarkdownFileOutlinerTests.cs` 覆盖 8 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsMdMdx`
- `OutlineAsync_ATXHeadings_Extracted`
- `OutlineAsync_FrontMatter_Extracted`
- `OutlineAsync_CodeBlocksIgnored`
- `OutlineAsync_Hierarchy_NestedCorrectly`
- `OutlineAsync_SetextHeadings_Extracted`
- （其余 2 条同类用例略）

`PowerShell/PowerShellFileOutlinerTests.cs` 覆盖 7 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsPs`
- `OutlineAsync_FunctionDeclaration_Extracted`
- `OutlineAsync_MultipleFunctions_AllExtracted`
- `OutlineAsync_Class_Extracted`
- `OutlineAsync_Enum_Extracted`
- `OutlineAsync_Variables_Extracted`
- （其余 1 条同类用例略）

`Python/PythonFileOutlinerTests.cs` 覆盖 3 条用例：
- `SupportedExtensions_Include_Python_File_Forms`
- `OutlineAsync_ClassMethodsFunctionsAndVariables_AreExtracted`
- `OutlineAsync_Ignores_Nested_Functions_Inside_FunctionBodies`

`Python/PythonIndexerBatchTests.cs` 覆盖 10 条用例：
- `Initialize`
- `OneProjectExtractionCoversTheWholeBatchAndNothingIsWritten`
- `PathsOutsideTheLanguageAreNotApplicableAndDoNotRunTheExtractor`
- `MissingProjectRootIsRetryableWithoutRunningTheExtractor`
- `MissingExtractorAssetsIsRetryableNotAFailure`
- `PathsNotCoveredByProjectModeFallBackPerFileAndClearTheSessionKey`
- （其余 4 条同类用例略）

`Services/CodeIntelligenceFixture.cs` 覆盖 1 条用例：
- `Dispose`

`Services/CodeProjectRegistryTests.cs` 覆盖 4 条用例：
- `AddProjectAsync_Generates_Stable_Project_Id_For_Directory_Outside_Workspace`
- `RemoveProjectAsync_Removes_Index_Data_Without_Deleting_Source_Directory`
- `AddProjectAsync_Returns_Failure_For_Missing_Project_Directory`
- `AddProjectAsync_Returns_Failure_For_Invalid_Project_Path`

`Services/CodeQueryServiceTests.cs` 覆盖 5 条用例：
- `Query_Service_Delegates_Read_Only_Symbol_And_Graph_Queries_To_Store`
- `Query_Service_Returns_Unknown_Status_For_Unregistered_Project`
- `SearchSymbols_Deduplicates_The_Same_Symbol_Indexed_Under_Multiple_Projects`
- `SearchSymbols_Keeps_Same_Named_Symbols_That_Have_Distinct_SymbolIds`
- `SearchSymbols_Keeps_Same_FullyQualified_Name_From_Distinct_Files`

`Services/CodeSourceRealExtractorBatchTests.cs` 覆盖 4 条用例：
- `Initialize`
- `RealTypeScriptExtractionReturnsOnePayloadPerFile`
- `RealPythonExtractionReturnsOnePayloadPerFile`
- `Dispose`

`Services/CompositeCodeIndexerLanguageDispatchTests.cs` 覆盖 3 条用例：
- `IndexFileAsync_For_A_CSharp_File_Equals_A_Direct_RoslynCSharpIndexer_Call`
- `Production_Language_Indexers_Own_Their_Extensions_And_Do_Not_Overlap`
- `AddPuddingCodeIntelligence_Registers_The_Aggregate_Over_The_Three_Languages`

`Services/DefaultCodeWorkspaceResolverTests.cs` 覆盖 3 条用例：
- `ResolveWorkspaceAsync_Discovers_CSharp_Workspace_Files_And_Ignores_Noise_Directories`
- `ResolveWorkspaceAsync_Returns_Unloaded_Descriptor_When_Source_Directory_Is_Missing`
- `ResolveWorkspaceAsync_Does_Not_Recurse_Into_Directory_Symbolic_Links`

`Services/DependencyInjectionTests.cs` 覆盖 2 条用例：
- `AddPuddingCodeIntelligence_Registers_Core_Services_Without_Registering_Store`
- `AddPuddingCodeIntelligence_Registers_All_File_Outliners`

`Services/EndToEndSourceMaintenanceTests.cs` 覆盖 11 条用例：
- `Initialize`
- `ARealChangeRunsTheWholeEnabledChainOnceAndLandsTheRealSymbols`
- `ARepeatedHintOnUnchangedRealFilesSkipsExtraction`
- `Dispose`
- `StartAsync`
- `PublishChange`
- （其余 5 条同类用例略）

`Services/FileOutlinerRegistryTests.cs` 覆盖 9 条用例：
- `IsSupported_TypeScriptFile_ReturnsTrue`
- `IsSupported_MarkdownFile_ReturnsTrue`
- `IsSupported_UnsupportedFile_ReturnsFalse`
- `IsSupported_CppFile_ReturnsTrue`
- `IsSupported_PythonFile_ReturnsTrue`
- `GetOutliner_TypeScriptFile_ReturnsTypeScriptOutliner`
- （其余 3 条同类用例略）

`TypeScript/TypeScriptFileOutlinerTests.cs` 覆盖 10 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsTsTsxJsJsx`
- `OutlineAsync_ClassDeclaration_Extracted`
- `OutlineAsync_InterfaceDeclaration_Extracted`
- `OutlineAsync_TypeAlias_Extracted`
- `OutlineAsync_Enum_Extracted`
- `OutlineAsync_TopLevelFunction_Extracted`
- （其余 4 条同类用例略）

`TypeScript/TypeScriptIndexerAssetTests.cs` 覆盖 5 条用例：
- `Initialize`
- `IndexWorkspaceAsync_ProjectPathShipsDecoyScript_UsesComponentOwnedScript`
- `IndexWorkspaceAsync_ExtractionRun_KeepsNodePathOutOfTheHostProcess`
- `IndexWorkspaceAsync_ComponentAssetsMissing_FailsWithExpectedPath`
- `Dispose`

`TypeScript/TypeScriptIndexerBatchTests.cs` 覆盖 9 条用例：
- `Initialize`
- `OneProjectExtractionCoversTheWholeBatchAndNothingIsWritten`
- `PathsOutsideTheLanguageAreNotApplicableAndDoNotRunTheExtractor`
- `MissingProjectRootIsRetryableWithoutRunningTheExtractor`
- `MissingExtractorAssetsIsRetryableNotAFailure`
- `PathsNotCoveredByProjectModeFallBackPerFileAndClearTheSessionKey`
- （其余 3 条同类用例略）

`Yaml/YamlFileOutlinerTests.cs` 覆盖 7 条用例：
- `OutlineAsync_SupportedExtensions_ReturnsYamlYml`
- `OutlineAsync_SimpleDocument_TopLevelKeysExtracted`
- `OutlineAsync_NestedKeys_ExtractedWithContainer`
- `OutlineAsync_DockerCompose`
- `OutlineAsync_MultipleDocuments_SeparatorsDetected`
- `OutlineAsync_CommentsIgnored`
- （其余 1 条同类用例略）
