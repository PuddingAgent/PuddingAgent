# Authorization tests

`AuthorizationTestHost.cs`: in-memory policy source and approval store fakes plus frozen request/approval fixtures.

`AuthorizationTests.cs`: system Deny wins over plugin Allow + approval, plugin Deny tightens, default deny, allow rule, needs-approval never allows execution, approved matching call allows, pending/expired approval does not, argument-hash/policy-version mismatch denies, stale policy version unavailable, expired rules ignored, source failure unavailable (never fail open).

`ApprovalTests.cs`: idempotent request and content conflict, bounds/binding validation, plugin cannot decide, revision CAS and pending-only decisions, expiry rejection, missing/invalid approval lookups.
