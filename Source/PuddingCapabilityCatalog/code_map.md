# Capability catalog

| File | Responsibility |
|---|---|
| `CapabilityCatalogService.cs` | PA-05 read-only catalog facade: validates kind (tool/mcp/context, case-sensitive) and bounds before the source, forwards cursor/size unchanged, and preserves Revision/Generation/Availability/Completeness exactly — forbidden or partial pages are never folded into "no capabilities", and source failures are typed instead of empty. Catalog entries stay non-executable (RequiresApproval preserved). |
