# MCP

`McpConnectionCoordinator.cs`: PA-05C lifecycle coordination — bounded start with typed `start_timeout`/`start_failed` (never Ready on failure), idempotent same-generation connect, requests require Ready + matching generation, expired deadlines return Unknown without touching the transport, transport timeouts/uncertain failures are Unknown and never auto-retried, drain disposes the transport and stops accepting requests, events are accepted in sequence order with duplicates/out-of-order ignored, gaps reported, and the cursor persisted for reconnect.
