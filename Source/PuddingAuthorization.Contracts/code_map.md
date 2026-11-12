# Authorization ports

`AuthorizationPorts.cs`: PA-05A BCL-only authorization ports — typed decision outcomes (Allow/Deny/NeedsApproval/Unavailable), policy rules with source/version/expiry, host-bound `AuthorizationRequest`, policy source, approval record/state store (CAS by revision), host-authored `ApprovalBinding` (a plugin cannot decide its own request) and typed error codes. UI, identity and persistence adapters stay outside this component.
