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

`NativeSpeechAudioPlayer`: WinRT MediaPlayer over bounded in-memory audio; task completes on media end/error/cancel and releases stream/player. `SpeechPlaybackButton`: message-scoped action using one shared SpeechPlaybackSession, state labels and retry feedback, unload/dispose cancellation without disposing the shared session. MessageCard offers playback for completed agent/assistant replies. ChatWorkspace owns the shared playback session and stops it on role change, leaving chat and disposal; Composition supplies direct Core synthesis. Microphone/ASR and continuous voice remain pending.

`VoiceInputControl`: independent recording/finish/cancel actions, transcription status and selectable result preview. Explicit guarded draft acceptance never sends; changed drafts keep the result for copying. Unload/dispose cancels the operation without owning the workspace session. Tested with fake capture/ASR in a real WinUI window; native microphone/Core/composer wiring remains pending.

`NativeVoiceCapture`: explicit UI/STA MediaCapture audio initialization, PCM 16k/mono/16-bit WAV, fixed 8 MiB random-access memory stream, two-minute limit/failure watcher, shared async stop/dispose. VoiceCaptureChecks verifies the real encoding/WinRT stream boundary and pre-cancelled opening without accessing the microphone. Hardware behavior and workspace wiring remain pending.

`ChatComposer.SetVoiceInput` mounts a native recording flyout; voice-enabled toolbars wrap below 560 DIP. `ChatWorkspace` binds capture/transcription to the selected role and guarded draft acceptance, cancels on role/page/lifetime changes, and exposes async disposal to await microphone release. MainWindow waits for release before remounting/exiting. WorkspaceVoiceChecks uses fake devices/ASR in real WinUI; hardware and supplier acceptance remain pending.

`RemoteImageSource` / `RemoteImageView`: external HTTP(S) image previews loaded only on expansion, bounded bytes/timeout/redirects, credential-free shared client, WIC dimensions plus bounded native decoding, cancellation/recycling. MarkdownImageContext routes canonical artifacts to Core and external images to this independent source; unchanged Markdown appends retain previews.
`ChatWorkspace.SendAsync` captures voice provenance before session creation awaits, alongside text/images/files. WorkspaceVoiceChecks verifies accepted ASR source reaches PendingSend; current native checks: 219.
`ChatComposer` observes TextCompositionStarted/Ended, preserves plain Enter, guards Ctrl+Enter during composition, consumes disabled/repeated send shortcuts, and excludes Shift/Alt combinations. ComposerKeyboardChecks exercises native control policy; actual system IME event ordering remains an acceptance gate (224 native checks total).
`RoleAvatarCard.AccessibleLabel` includes current status/unread count; ChatWorkspace synchronizes realized ListView item names/help text and clears recycled labels. RoleAccessibilityChecks verifies actual list automation data peers retain SelectionItem while labels update. Frozen/disabled roles retain unread badges (228 native checks total).
`PagedTextView`: native previous/next page and lossless full-copy data; ActivityContentView routes tool payloads over 32,768 UTF-16 units to bounded plain text, retaining normal Markdown below that size. Streaming preserves current page; long-to-short replacement restores Markdown. 236 native checks; source memory remains unbounded by this presentation change.
`MessageCard`: copy/speech share a constrained Grid row; empty attachments and closed outcome reserve no spacing. Copy uses current content, provides retry after clipboard failure and is disabled after recycling. MessageActionChecks covers layout/copy failures/latest text/speech error wrapping/disposal (243 native checks).
`MessageCard.LoadProcessDetailsAsync`: loading indicator, loaded item count/merge explanation, honest partial-window label and explicit retry button. Run changes reset disclosure heading; canonical identity/cancellation/cache guards remain. MessageDetailsChecks invokes native retry; 246 window checks total.
`MarkdownView` skips identical snapshots and uses reference-identity membership sets when retaining controls; TurnContentView uses the same retention strategy. Full Markdown parsing remains necessary for late reference definitions. MarkdownStreamingChecks covers 801 completed blocks, repeated appends, stable code preferences and reference invalidation (250 native checks).

`ChatWorkspace.CancelAsync` tracks in-flight stops by role/session/turn, suppresses duplicate calls and rejects late feedback after selection changes. ChatComposer disables the pending stop action; failure allows retry and success does not mark execution terminal. WorkspaceCancellationChecks covers seven window scenarios (257 native checks total).

`PagedTextView` bounds each text page inside a native 360-DIP ScrollViewer while keeping pagination/copy outside it. Explicit page changes reset to the top; streaming updates retain the current page offset. PagedTextChecks verifies actual viewport and navigation geometry (260 native checks).
