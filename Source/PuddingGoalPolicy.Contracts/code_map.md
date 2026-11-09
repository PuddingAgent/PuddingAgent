# Goal policy ports

`GoalPolicyContracts.cs`: PA-04 BCL-only pure-policy contracts — interaction modes (Chat/Task/Goal), frozen continuation policy snapshot (policy version, review requirement, repair/no-progress limits, root budget, capabilities, resume policy), authoritative fact record (cancel/pause/revocation, version/stale flags, review fact, budget/round limits, evidence completeness, repair usage, no-progress count), and the Continue/Wait/Complete/Stop advice with typed reason codes. No GoalPhase writes and no state-machine copy.
