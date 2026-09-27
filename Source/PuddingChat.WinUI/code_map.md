# PuddingChat.WinUI

Programmatic native WinUI controls, no WebView/HTML or host dependency.

- `RoleAvatarCard`: portrait fallback, description, canonical status and unread count.
- `ChatComposer`: multiline per-role draft, Ctrl+Enter send, explicit retry and cancellation.
- `MessageCard`: selectable text, native code/heading blocks, copy, lazy canonical process disclosure, unknown event fallback.
- `ChatWorkspace`: local initialization, role navigation, committed-event subscriptions, history paging, drafts/reading state, native subagent inspection and operation lifetime.
- `MarkdownView` / `CodeBlockView`: native GFM and selectable code. ColorCode.WinUI 2.0.15 supplies syntax-colored Inlines; unknown/long/high-contrast or non-lossless formatting falls back to complete plain source. Copy always uses the original source.
- `TurnContentView` / `ActivityContentView`: ordered thinking/tool/delegation disclosures, stable content slots and progressive expansion.
- `VirtualTranscript` / `MessageViewState`: viewport realization and persistent nonvisual disclosure state.

The host supplies `IChatClient`, owns this control's disposal, and receives settings/runtime navigation events. Image attachments and paged history are connected through independent contracts. Formula rendering, general file context, voice and remaining product acceptance are tracked in the native chat completion audit.

`WorkspaceSetupForm`: native workspace/first-role creation with existing model selection, validation and retry feedback. ChatWorkspace hosts the dialog and selects the returned role. Advanced Web management remains explicitly separate.

`ProviderConfigurationForm`: native provider/chat-model create/edit and explicit key retention/replacement/clear, password input cleared on save/unload. `RoleConfigurationForm`: name, description, enabled, main model, role type and system prompt. ChatWorkspace opens modal editors, refreshes role cards after save; advanced settings remain separate.
