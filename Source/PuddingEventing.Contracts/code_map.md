# Eventing port contracts

`EventSubscriptionPorts.cs`: PA-07A reliable plugin subscription ports — validated `EventEnvelope` (id/stream/sequence/kind/schema/time/workspace/payload hash/producer/correlation), committed event log read with retention `snapshot_required`, host-bound `SubscriptionGrant`, subscription record with monotonic cursor/revision, and the delivery ports: transactional outbox (`IEventOutboxWriter`/`IEventOutboxTransaction`), at-least-once delivery claiming, atomic `IEventCheckpointStore.AckAndAdvanceAsync`, dead letters and typed error codes. BCL-only.

`DurableJobPorts.cs`: PA-07A durable plugin job ports — bounded `PluginJobRequest`, host-bound `PluginJobBinding` (plugin generation, workspace, owner work, root budget, deadline), idempotent-by-operation submit, generation-fenced cancel/transition and the `PluginJobSnapshot` state owned by the job protocol (never a Review/Goal state). BCL-only.
