# PuddingChat.WinUI

Programmatic native WinUI controls, no WebView/HTML or host dependency.

- `RoleAvatarCard`: portrait fallback, description, canonical status and unread count.
- `ChatComposer`: multiline per-role draft, Ctrl+Enter send, explicit retry and cancellation.
- `MessageCard`: selectable text, native code/heading blocks, copy, lazy canonical process disclosure, unknown event fallback.
- `ChatWorkspace`: local initialization, role navigation, committed-event subscriptions, history paging, drafts/reading state, native subagent inspection and operation lifetime.
- `MarkdownView` / `CodeBlockView`: native GFM and selectable code. ColorCode.WinUI 2.0.15 supplies syntax-colored Inlines; unknown/long/high-contrast or non-lossless formatting falls back to complete plain source. Copy always uses the original source.
- `MathFormulaView`: CSharpMath.SkiaSharp 1.0.0-pre.1 + SkiaSharp 3.119.2, bounded off-thread formula rasterization into native Image. Markdown mathematics use RichTextBlock/InlineUIContainer or a display block; exact LaTeX copy/source fallback, theme rerender and unloaded cancellation. No browser or TeX process.
- `MarkdownImageContext`: workspace-scoped IImageAttachmentClient access for generated image fences and Markdown image references. MessageCard, active TurnContentView and expanded activity content supply the same context. ImageAttachmentView loads only when mounted/expanded and restores previews after unloading/reloading; unchanged Markdown destinations preserve decoded controls.
- `TurnContentView` / `ActivityContentView`: ordered thinking/tool/delegation disclosures, stable content slots and progressive expansion.
- `VirtualTranscript` / `MessageViewState`: viewport realization and persistent nonvisual disclosure state.

The host supplies `IChatClient`, owns this control's disposal, and receives settings/runtime navigation events. Image attachments and paged history are connected through independent contracts. ChatComposer/ChatWorkspace import text files as per-role snapshots with native preview/removal, then send them through the existing client port. Binary document extraction, external Markdown images, voice and remaining product acceptance are tracked in the native chat completion audit.

`ChatWorkspace.UpdateNavigationLayout`: preserves the host's preferred sidebar width; when chat would have less than 520 DIP, or the host collapses the sidebar, moves the existing role navigation into a native Flyout reached from the heading. Wide mode reuses the same controls. Role selection, leaving the workbench and disposal close the flyout; draft/role state is not recreated by resizing.

`ChatWorkspace.FileTransfer.cs`: mixed image/text StorageItems transfer, capturing the role before deferred provider reads. Validates text snapshots first and commits draft attachments only after the entire batch succeeds. ChatComposer routes drop and Ctrl+V storage items here; explicit paste-image and bitmap clipboard retain NativeImageTransfer, ordinary text stays with TextBox.

`WorkspaceSetupForm`: native workspace/first-role creation with existing model selection, validation and retry feedback. ChatWorkspace hosts the dialog and selects the returned role. Advanced Web management remains explicitly separate.

`ProviderConfigurationForm`: native provider/chat-model create/edit and explicit key retention/replacement/clear, password input cleared on save/unload. `RoleConfigurationForm`: name, description, enabled, main model, role type and system prompt. ChatWorkspace opens modal editors, refreshes role cards after save; advanced settings remain separate.

`MathFormulaView` lifecycle: stable Grid/visibility switching, same-theme render deduplication, and DispatcherQueue-delayed unload release guarded by IsLoaded. Prevents InlineUIContainer reflow from cancelling completed images repeatedly. VisualPreview asserts actual image dimensions, collapsed source and stable rendering tasks across frames in wide/compact/dark full-message scenes; genuine unmount/remount remains covered.

`NativeSpeechAudioPlayer`: WinRT MediaPlayer over bounded in-memory audio; task completes on media end/error/cancel and releases stream/player. `SpeechPlaybackButton`: message-scoped action using one shared SpeechPlaybackSession, state labels and retry feedback, unload/dispose cancellation without disposing the shared session. These are independently tested components; product MessageCard/ChatWorkspace and Core speech adapter wiring remain pending.
