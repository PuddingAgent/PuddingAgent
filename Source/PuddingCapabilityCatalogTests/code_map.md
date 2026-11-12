# Capability catalog tests

`CapabilityCatalogTests.cs`: kind/bounds validation before the source, revision/generation/availability preservation, forbidden page is not an empty success, partial truncated page keeps Cursor/Complete=false, legitimate empty page is complete, catalog entry still requires approval (not an execution grant), source failure/invalid generation typed as source-unavailable, caller cancellation not swallowed. Uses an in-file fake source; no Host/platform loading.
