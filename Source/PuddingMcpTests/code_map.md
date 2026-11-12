# MCP tests

`McpTestHost.cs`: controllable transport (start delay/failure, send failure/handler, event list) and in-memory cursor store.

`McpConnectionTests.cs`: start timeout never Ready + dispose, idempotent same-generation connect and typed start failure, request state/generation fencing, expired deadline Unknown without transport call, transport failure Unknown and not retried, real timeout Unknown via deadline race, drain dispose/request rejection, ordered dedupe + gap + cursor persistence, reconnect cursor preservation, invalid refs and caller cancellation.
