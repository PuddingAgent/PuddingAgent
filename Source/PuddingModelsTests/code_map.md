# Model policy tests

`ModelTestHost.cs`: independent fakes for the PA-06 ports (layered configuration store with CAS, quote source, catalog source) plus deterministic quote/capability/usage fixtures. Not a product implementation.

`EffectiveModelPolicyTests.cs`: layer precedence (instance wins when all layers exist), missing purposes, explicit inherit-primary, CAS loser cannot overwrite, clear falls back to workspace, receipt/applied revision, malformed/unknown fields rejected before the store, per-agent isolation, invalid scope.

`ModelQuoteTests.cs`: explicit-free vs undeclared zero, missing price components, currency mismatch, half-open window on the dispatch instant, cache-hit billing without double charge, unknown usage not fabricated, charge above cap rejected, invalid usage typed.

`ModelLeaseGenerationTests.cs`: new generation revokes new dispatch while old leases still release, downgrade never preempts active slots, upgrade/revoke limits, revoke keeps active count, duplicate release rejected, unknown model unavailable, bounded lease lifetime.
