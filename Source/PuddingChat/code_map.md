# PuddingChat

BCL-only leaf, no host or UI references. Contracts are a read-only subset of canonical Platform projection DTOs.

- `HttpChatClient`: ephemeral JWT authentication; Core loopback projection / main-session / Turn admission and cancellation APIs.
- `ChatSelection`: role identity, selection epochs, isolated drafts and stable retry IDs; canonical sequence ordering.
- `Contracts`: transport DTOs and IChatClient port. Accepted means queued, never completed.

Consumers own polling, dispatch, rendering and lifetime. Uncertain POST responses retain the same pending command until acknowledged; a later draft is never overwritten by its receipt.
