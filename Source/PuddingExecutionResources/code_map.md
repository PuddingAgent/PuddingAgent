# Execution resources

| File | Responsibility |
|---|---|
| `ExecutionResourceScope.cs` | Atomic hierarchical reservations, observed/unknown accounting, cancellation and closed admission; external main-loop usage is explicitly post-dispatch accounting |
| `ModelResourcePool.cs` | Frozen model slots, exactly one dispatch, queue cancellation and late-provider draining without early resource release |
| `InMemoryExecutionBudgetLedger.cs` | Independent-test ledger for the durable budget ports: ancestor-atomic reservations, operation/attempt idempotency, cancellation revision linearization, owner-epoch fencing, once-only settlement, unknown retention and subtree cancellation. Not a second production ledger. |
| `DurableBudgetCoordinator.cs` | PA-01 attempt protocol: reserve → dispatch (cancel linearization) → provider → settle/unknown, plus a pure `BudgetRecoveryPlanner` (undispatched refund, dispatched/unknown stays unknown) and `RecoverAsync` host reconciler entry. |
