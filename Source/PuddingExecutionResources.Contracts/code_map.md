# Execution resource ports

`ExecutionResources.cs`: host-owned execution scopes, hierarchical budget reservations, frozen model descriptors, single-dispatch transport and resource pool contracts. No project or package dependencies.

`DurableBudgetContracts.cs`: cross-round durable budget ports (PA-01): opaque account ids, host-bound identity, ancestor path, reserve/dispatch/settle/unknown/release/cancel commands, monotonic snapshot (revision + cancellation revision), the single authoritative `IExecutionBudgetLedger` seam and the host-only `IExecutionBudgetRecoveryLedger` listing used by restart reconciliation. BCL-only; the in-memory implementation is for independent tests and the product must wire exactly one persistent ledger.
