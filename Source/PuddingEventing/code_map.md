# Eventing

| File | Responsibility |
|---|---|
| `SubscriptionDispatcher.cs` | Server-side delivery driver: authorization filter (bound workspace + granted kinds), bounded batch claiming, at-least-once re-lease, contiguous-ack validation, single-transaction checkpoint via `IEventCheckpointStore`, retention `snapshot_required`, dead-letter query/replay. Contains no Goal/Review business. |
| `DeliveryCoordinator.cs` | Host adapter for the plugin-facing API: binds plugin id/generation/workspace/owner work/root budget, denies ungranted kinds and capabilities, enforces generation fences on read/ack/get/cancel, maps logical event acknowledgements to leased delivery ids, and returns job tickets without waiting for execution. |
