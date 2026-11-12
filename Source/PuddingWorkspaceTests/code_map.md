# Workspace tests

`WorkspaceTestHost.cs`: fake catalog source and filesystem adapter (content/link/atomic/failure switches).

`WorkspaceTests.cs`: traversal/rooted/unsafe path rejection, Chinese nested path acceptance, workspace-id separators/traversal, list bounds before source, cursor/completeness preservation, source failure typed, logical-only environment snapshot + missing typed, link rejection before IO, missing file typed, content read, CAS write hash/content requirements, non-atomic adapter and link rejection, adapter CAS mismatch not swallowed, forbidden id before source.
