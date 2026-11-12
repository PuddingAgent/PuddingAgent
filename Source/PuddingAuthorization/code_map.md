# Authorization

| File | Responsibility |
|---|---|
| `AuthorizationEvaluator.cs` | PA-05A pure decision: system Deny always wins over plugin Allow and approvals, plugin rules only tighten, no rule means deny, stale policy version and source failure are Unavailable (never fail open), expired rules are ignored, and an approval only grants when it exactly matches action/resource/argument hash/policy version and is Approved and unexpired. |
| `ApprovalCoordinator.cs` | PA-05A approval lifecycle: idempotent request by OperationId (different content conflicts), bounded TTL, revision-CAS decisions, only host/system identity may decide, expired or non-pending approvals are rejected. |
