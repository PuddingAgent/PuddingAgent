# Capability catalog ports

`CapabilityCatalogPorts.cs`: PA-05 BCL-only read-only catalog ports — capability record (id/kind/display/revision/generation/availability/missing reason/requires-approval/required capabilities) and authorized page with `Cursor`/`Complete`/`Partial`/`Generation`/`MissingReason`. Permission gaps are expressed as Partial/Forbidden rather than an empty catalog, and a listed capability is never an executable authorization.
