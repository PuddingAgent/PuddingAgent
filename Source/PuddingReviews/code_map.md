# Reviews

| File | Responsibility |
|---|---|
| `ReviewInputValidator.cs` | Input fence: snapshot must belong to the job's review id and logical key with matching versions and stay inside the job deadline; incomplete evidence is forwarded to the result validator, never repaired. |
| `ReviewResultValidator.cs` | Advice fence: version/prompt mismatch, unknown criterion or evidence, uncovered required criteria, truncated/incomplete evidence and empty required criteria downgrade to Inconclusive; caller-supplied evidence can never earn CheckVerified; Satisfied requires every criterion Supported, otherwise NeedsWork. |
| `ReviewJobCoordinator.cs` | Persistent acceptance with idempotency, owner-epoch/revision-fenced claim, committed evidence read, bound model attempt, result validation, completion through the outbox; terminal jobs never re-run the model and a cancelled job rejects late completion. No Goal terminal writes. |
