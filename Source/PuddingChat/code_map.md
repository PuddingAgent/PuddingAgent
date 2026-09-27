# PuddingChat

BCL-only leaf, no host or UI references. Contracts are a read-only subset of canonical Platform projection DTOs.

- `ChatSelection`: role identity, selection epochs, isolated drafts and stable retry IDs; canonical sequence ordering.
- `Contracts`: plain DTOs and IChatClient application port. Accepted means queued, never completed.
- `MarkdownImageReference`: canonical vision artifact IDs from Web image fences, artifact filenames and current-workspace relative resource URLs. Remote URLs, mismatched workspace routes and arbitrary files are not resolved; callers still use Core workspace-owned storage, never the supplied path.
- `TextFileContext`: bounded UTF-8/UTF-16 text snapshots and deterministic text-context composition. ChatSelection owns per-role file drafts; PendingSend freezes snapshots for retry, independently of source-file changes.

Consumers own polling, dispatch, rendering and lifetime. Uncertain command completion retains the same pending identity until acknowledged; a later draft is never overwritten by its receipt. Desktop Composition supplies the in-process adapter; this component has no HTTP, JSON, host, database or UI dependency.

`WorkspaceSetup.cs`: local first-use request normalization and `IWorkspaceSetupClient`; no login or secrets. Workspace IDs are bounded path-safe slugs.

`Configuration.cs`: secret-free provider/model and role snapshots, explicit Keep/Replace/Clear secret commands, input validation, and `IConfigurationClient`. Secret-bearing edits redact their ToString.

`SpeechPlayback.cs`: workspace/role/message-bound speech requests, bounded WAV/MP3 audio, synthesis/device ports and one UI-owned playback lane. Cancels replaced/stopped/disposed work and rejects stale completion; no platform audio, HTTP or Core dependency. Native device/Core adapters remain pending; see Desktop-Native-Voice-2026-09-27.md.

`VoiceInput.cs`: UI-owned capture/transcription lifecycle and device/service ports. VoiceDraftAnchor binds role, selection epoch and original text; VoiceDraftResult only appends to unchanged drafts. Cancellation drains microphone ownership before reuse, ignores late ASR, and async disposal waits for cleanup. No WinRT, HTTP or Core dependency; native capture/UI and ASR adapter remain pending. Independent logic suite: 77 tests.
