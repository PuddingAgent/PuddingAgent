# Agent backend ports

`AgentPorts.cs`: PA-02A host-bound agent ports (BCL-only) — `AgentBinding` (workspace/actor/owner work/root budget/parent limits/allowed capabilities), agent directory store with cursor/complete semantics, committed work event source with `snapshot_required`, canonical cancel gateway, idempotent cancel-command store, child-work submitter, atomic sub-agent lease pool (acquire/release fenced by owner and generation) and typed error codes. Callers never supply their own root/actor/parent identity.

`AgentAdapterPorts.cs`: host-resolved workspace, Agent and plugin installation binding, plus the current connection lease used to fence external adapter operations.
