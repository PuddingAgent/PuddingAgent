# Agent component tests

`AgentTestHost.cs`: independent fake backends for every PA-02A port (directory store with cursor pages, committed work event source with retention, cancel gateway spy, cancel-command store, child work submitter, atomic lease pool under one lock). Not a product implementation.

`AgentDirectoryTests.cs`: workspace-scoped listing, foreign-workspace Get rejection, cursor/complete pagination, truncated page never treated as absence, page-size/cursor bounds, archived state mapping.

`AgentWorkControlTests.cs`: event mapping with terminal status, retention `snapshot_required`, cancel requested once with replay returning the original receipt, same-operation/different-content conflict, AlreadyTerminal passthrough, foreign-workspace cancel rejected before the gateway.

`DelegationOwnershipTests.cs`: bound parent injection, forged parent rejection, child limit/capability denial, atomic concurrent acquire (exactly one lease), idempotent acquire replay, owner/generation-fenced release, duplicate release rejection, generation advance on reacquire, foreign-workspace acquire rejection.
