# Execution resource tests

`ResourceTests.cs`: sibling contention, zero limits, pre-dispatch refunds, unknown usage, settlement, overrun, cancellation and retained slots for non-cooperative providers. References the standalone component only.

`DurableBudgetTests.cs`: PA-01 01A durable budget invariants on the in-memory ledger — ancestor-atomic reservation, forged-path rejection, operation idempotency vs conflict, cancel-before-dispatch linearization, once-only settlement with receipt idempotency, overrun recording and closed admission, unknown retention with late reconciliation, owner fencing, cross-round continuity, per-request input capacity, mixed model prices, subtree cancellation and unknown-not-zero snapshots.

`DurableBudgetCoordinatorTests.cs`: PA-01 01B/01C attempt seam and restart reconciliation — provider spy never called when the budget is exhausted, reservation observable before the provider call, exact-once settlement, provider failure keeps the ceiling unknown, cancellation before dispatch rejects, missing usage is not free, retry after release reuses the operation id without double charge, unknown is not retried automatically, abandoned dispatched handles stay unknown, recovery releases pending and keeps dispatched unknown without resetting the root, late settlement after unknown settles once, and buffered/streaming channels share the same seam.
