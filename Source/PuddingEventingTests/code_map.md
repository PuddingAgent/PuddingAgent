# Eventing tests

`InMemoryEventingHost.cs`: independent in-memory adapter for every PA-07A port (committed log, subscriptions, transactional outbox, delivery leases, atomic checkpoint, jobs) under one lock, with test hooks for retention windows and concurrent cursor writers. Not a product implementation.

`SubscriptionTests.cs`: authorization filtering (kind + workspace), contiguous cursor advance and gap rejection, crash-before-ack redelivery without duplicating the logical delivery, retention snapshot signal, cancelled-subscription reads/acks, ungranted kind and cross-workspace rejection, stale-generation reads, two-dispatcher no-duplicate delivery, dead-letter query/replay, and transactional outbox visibility, duplicate-id rejection and uncommitted rollback.

`DeliveryRecoveryTests.cs`: non-blocking job submit and ticket, idempotent submit per operation with content conflict, generation-fenced get/cancel, stale-generation result transition rejection, missing job grant, invalid host binding, and atomic checkpoint rejection on a stale revision or a cancelled subscription.
