# Review job ports

`ReviewJobPorts.cs`: PA-03 persistent review ports (BCL-only) — review job states owned by the review job (never a GoalPhase), logical unique key (workspace/parent/goal, activation epoch, iteration, objective/acceptance versions, artifact-set hash, policy version), frozen input snapshot (criteria + evidence with retention/completeness/caller-supplied class), model result, advice-only `ReviewOutcome` with evidence level, revision/owner-epoch store ports, committed-evidence reader, bound executor, durable completion outbox and typed error codes.
