# MCP ports

`McpPorts.cs`: PA-05C BCL-only MCP ports — transport kind/connection state, host-bound `McpServerRef` (server/workspace/generation), bounded request with deadline, response with explicit Unknown, sequenced event and accepted batch (cursor/gap), transport port (start/send/read events), cursor store, and typed errors. Process/token storage stays in adapters.
