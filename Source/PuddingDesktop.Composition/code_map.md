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

`SettingsOperationScope` is the per-operation Core DI scope used by `Session.RunAsync`. `DesktopKernelFactory.CreateLlmSettings(kernel)` binds the LLM adapter to the kernel lifecycle.
