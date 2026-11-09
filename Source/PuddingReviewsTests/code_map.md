# Review tests

`ReviewTestHost.cs`: independent in-memory adapter for every PA-03 port (idempotent submit, revision/owner-epoch-fenced claim/complete/fail/cancel, configurable evidence and executor, completion outbox). Not a production store.

`ReviewJobTests.cs`: key idempotency and operation conflict, deadline bounds, happy-path completion with one published event, terminal jobs never re-executed, stale-owner rejection, executor failure → Unknown without completion, concurrent claims executing once, cancellation with late completion rejected, missing review and cancel revision checks.

`ReviewEvidenceTests.cs`: empty required criteria, incomplete/truncated evidence, unknown criterion/evidence, version and prompt mismatch, caller-supplied evidence never CheckVerified, controlled checks earning CheckVerified, model Accept downgraded to NeedsWork when a criterion fails, uncovered required criterion.

`ReviewRecoveryTests.cs`: snapshot-required signal keeps the job Waiting without calling the model, evidence-reader exceptions typed as Waiting/Unknown, missing evidence never passes, explicit new-owner retry after Waiting completes, completion event carries finding/level/revision.
