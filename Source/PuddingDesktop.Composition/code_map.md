# Desktop composition

`DesktopKernelFactory` implements the Foundation lifecycle factory with PuddingHost DLL. The adapter is the only bridge to Host; no process launch, PID supervision or shutdown HTTP call. It injects `IDesktopServices`, leases the data directory, initializes and starts the host on loopback, then stops/disposes resources. Browser automation stays unavailable until its WinUI adapter is ported.

`InProcessChatClient` implements the BCL `IChatClient` port by invoking Core application services directly. Every operation runs on the thread pool with its own async DI scope and linked cancellation. Native clients use the fixed Core single-user actor (`single-user`) without account lookup, password validation, JWT, cookie, HTTP request or controller invocation. Web authentication remains unchanged. Agent/main-session/Turn/projection services stay in Core. Core stop cancels and drains clients before disposing the host; restarted clients can immediately query their local workspace. Portrait URLs map only to packaged wwwroot assets, with path containment checked.

`InProcessChatClient` also implements `IWorkspaceSetupClient`, projecting only provider/model identifiers and labels and invoking `LocalWorkspaceSetupService` directly; the lifecycle drain applies to setup writes too.

`IConfigurationClient` uses the same in-process scoped/lifetime-managed adapter. Reads expose HasKey only; provider edits call the Core partial-save method, role edits preserve undisplayed manifest settings. No configuration HTTP or controller invocation.

`DesktopLlmResourceSettings` implements `ILlmResourceSettings` for the native settings pages (DS-02): provider CRUD, provider limits, model definition, model context/pricing and provider quota. Every call goes through `IDesktopKernel.RunSettingsAsync`, so the DS-00 gate refuses an unready or stopping host instead of faking a save, and each operation gets its own async DI scope. `ApiKeyChange.Clear` maps to `UpsertLlmProviderRequest.ClearApiKey`; `Keep` submits a null key so stored plaintext and vault references survive. Quota reads and writes go to `LlmProviderQuotaService` (limits in the provider file, usage derived from the token ledger, reset moves the window). Core keeps validation, the write lock and atomic replacement.

`DesktopVoiceResourceSettings` implements `IVoiceResourceSettings` for the TTS/ASR pages (DS-03) on the same gated path: provider CRUD, TTS/ASR model CRUD and the effective defaults. `ApiKeyChange.Clear` maps to `UpsertVoiceProviderRequest.ClearApiKey`; array fields are submitted as displayed so capability switches round-trip. Core keeps validation, the write lock and default-pointer sync.

`DesktopAgentDirectorySettings` implements `IAgentDirectorySettings` (DS-04 directory slice) on the same gated path: templates, shipped presets, the avatar catalog, workspace role instances and freeze. Template and instance basic edits reload the stored record and submit it back with only the displayed fields replaced, so prompt/Markdown documents and undisplayed policy fields survive.

`DesktopAgentDocumentsSettings` (partial of the directory adapter) covers the DS-04 document slice: template prompt/Markdown fields are saved with a fingerprint of what was read, instance Markdown documents use Core's own SHA-256 token, and a document the instance manifest does not reference is reported as repairable instead of failing the page.

`DesktopAgentModelPolicySettings` (partial of the directory adapter) covers the DS-04 model & memory slice: the provider/model catalogue, the three pairs, memory search mode and reasoning effort for template and instance. Saves merge into the stored record, half-filled pairs are refused, and TemplateRequest is the single builder used by the basic, document and policy saves.

`DesktopAgentSmartRouteSettings` (partial of the directory adapter) covers the DS-04 Smart slice and owns `InstanceRequest`, the single instance-profile builder used by the basic, model-policy and Smart saves. Smart routes are written through `UpdateAgentAsync` because `UpdateAgentProfileAsync` deliberately forces the stored routing back; a basic profile edit therefore cannot clobber routing.

`DesktopAgentGuardrailSettings` (partial of the directory adapter) covers the DS-04 guardrail slice through the shared `TemplateRequest`/`InstanceRequest` builders. An instance guardrail save goes through `UpdateAgentProfileAsync`, so Core keeps the Smart routes intact.

`DesktopToolPluginSettings` implements `IToolPluginSettings` (DS-06) on the same gated path: the runtime tool registry (`IPuddingToolCatalogService`) and the plugin catalogue (`PluginManifestCatalog` + `PluginDiagnosticsReader`), both read-only plus an explicit manifest re-read.

`DesktopSkillHubSettings` implements `ISkillHubSettings` (DS-07 overview/events slice) on the same gated path. `ISkillHubService` is registered scoped, so it is resolved from the per-operation Core scope instead of being captured. Write paths (publish/evolve/retire/install) are not exposed yet.

`DesktopSkillHubSettings` also covers the DS-07 library slice: skill detail and version markdown, metadata edit, soft retirement, version publish and install registration. `SkillHubResult` failures are surfaced as real errors instead of ignored.

`DesktopSkillHubSettings` also covers EVO MAP and the install ledger: lineage for one skill or globally, ledger queries and per-agent update checks.

`DesktopSkillPackageSettings` binds the legacy skill-package page to the shared `SkillPackageService` (the same operation the Web controller uses); the file is opened before any Core call so a bad path fails without side effects.

`DesktopAgentGrantSettings` (partial of `DesktopAgentDirectorySettings`) covers DS-04 grants: options come from the runtime tool catalogue plus the skill-package ledger, template grants read/write through `TemplateRequest`, and instance writes use `AgentGrantSelection` so null keeps the stored snapshot while an empty list clears it.

`DesktopWorkspaceSettings` binds the workspace tab to the shared `WorkspaceService`; team and user pickers read `PlatformDbContext` through the same per-operation scope.

`DesktopChannelSettings` binds the channel tabs to `ChannelConfigurationFileService`; a kept App Secret is sent as null so Core reuses the stored value, and the secret never travels back into the shell.

`DesktopWorkspaceResourceSettings` binds the knowledge / skill / workflow cards to `WorkspaceResourceService`; an empty resource id means create, and Core keeps ownership of workspace isolation plus MCP config validation.

`DesktopMemoryLibrarySettings` binds the memory library cards to the scoped `IMemoryLibraryAdminService` (Core registers the interface, not the concrete class) and always passes workspace + agent through.

`DesktopMemoryLibrarySettings` also covers search and inspection: FTS hits as Core returns them, plus source references (ownerType/ownerId) and pointers (sourceType/sourceId) queried with Core's own two key pairs, keeping outgoing and backlink directions separate.

`DesktopStorageSettings` reads Core's cached inventory snapshot, trend history, data-class catalogue and retention policy; a CAS conflict from `StorageAdminException` becomes `SettingsConflictException` so the shell can say the policy changed under it.

`SettingsOperationScope` is the per-operation Core DI scope used by `Session.RunAsync`. `DesktopKernelFactory.CreateLlmSettings(kernel)` binds the LLM adapter to the kernel lifecycle.
`InProcessChatClient.Speech`: IChatSpeechClient direct Core IVoiceSynthesisService adapter. Reads authoritative message/envelope payload, validates workspace/role/local-owner identity, uses configured defaults and the existing tracked-operation cancellation lifetime.

`InProcessChatClient.Transcription`: IChatTranscriptionClient → existing Core IAudioTranscriptionService; validates bounded recording and current enabled/unfrozen role, passes WAV bytes with default provider/model selection, follows tracked host cancellation. Integration tests use real configuration/application services and a fake provider; no Desktop HTTP or actual microphone.
