# Tests/ReviewAuditPlugin.Tests

| File | Purpose |
|---|---|
| `ReviewAuditPlugin.Tests.csproj` | MSTest.Sdk/4.0.1 runner (`OutputType=Exe`, `UseVSTest=false`); only `ProjectReference` is the sample project; every host dependency is an in-memory fake `IPluginEventContext`/`IPluginAuditApi`, no real plugin process, no host, no network. |
| `ReviewAuditPluginTests.cs` | Offline coverage for `ReviewAuditPlugin`: review.completed maps exactly six `PluginAuditRecord` fields verbatim (OperationId=EventId, Chinese Finding/ReasonCode); the frozen audit text is asserted verbatim via `FormatForTest` (blank/missing ReasonCode → `无`, record value stays verbatim); non-review events and a missing `ReviewCompleted` payload write nothing; a replayed event keeps the same OperationId (the plugin does not dedupe or mint a GUID); a pre-cancelled call writes nothing; `RecordAsync` cancellation and typed `PluginEventsException` are not swallowed or converted to success; only the scoped audit sink is used (other context members are never read); the sample assembly references only the AgentSdk surface; helper rejects non-review envelopes; null envelope/context → `ArgumentNullException`. |
| `code_map.md` | This index. |
