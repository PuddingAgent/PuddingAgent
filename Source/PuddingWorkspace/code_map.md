# Workspace

| File | Responsibility |
|---|---|
| `WorkspaceFilePolicy.cs` | PA-05F canonical path policy: rejects rooted paths, drive letters, backslashes, `.`/`..`/empty segments, trailing dots/spaces, control characters, Windows reserved names and unbounded lengths; workspace ids reject separators/traversal. Never repairs a suspicious path into an accepted one. |
| `WorkspaceCatalog.cs` | PA-05F authorized surface: bounded listing with cursor/completeness, logical environment snapshot only, path policy before IO, link/reparse rejection before read/write, CAS write with required expected hash, and atomic-adapter enforcement (non-atomic adapter is `unsupported`). Source failures are typed, never fabricated success. |
