# PuddingChat

BCL-only leaf, no host or UI references. Contracts are a read-only subset of canonical Platform projection DTOs.

- `ChatSelection`: role identity, selection epochs, isolated drafts and stable retry IDs; canonical sequence ordering.
- `Contracts`: plain DTOs and IChatClient application port. Accepted means queued, never completed.

Consumers own polling, dispatch, rendering and lifetime. Uncertain command completion retains the same pending identity until acknowledged; a later draft is never overwritten by its receipt. Desktop Composition supplies the in-process adapter; this component has no HTTP, JSON, host, database or UI dependency.

`WorkspaceSetup.cs`: local first-use request normalization and `IWorkspaceSetupClient`; no login or secrets. Workspace IDs are bounded path-safe slugs.
