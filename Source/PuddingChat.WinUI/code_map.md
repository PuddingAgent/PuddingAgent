# PuddingChat.WinUI

Programmatic native WinUI controls, no WebView/HTML or host dependency.

- `RoleAvatarCard`: portrait fallback, description, canonical status and unread count.
- `ChatComposer`: multiline per-role draft, Ctrl+Enter send, explicit retry and cancellation.
- `MessageCard`: selectable text, native code/heading blocks, copy, lazy canonical process disclosure, unknown event fallback.
- `ChatWorkspace`: automatic local initialization (shared across Loaded calls, retryable after failure), workspace/role navigation, bounded recent conversation snapshot polling (1s active/4s idle), dispatch and lifetime.

The host supplies `IChatClient`, owns this control's disposal, and receives settings/runtime navigation events. Current native formatting retains unsupported Markdown syntax verbatim. Attachments, voice, Live2D, complete historical pagination and admin editing are separate migration items.

`WorkspaceSetupForm`: native workspace/first-role creation with existing model selection, validation and retry feedback. ChatWorkspace hosts the dialog and selects the returned role. Advanced Web management remains explicitly separate.
