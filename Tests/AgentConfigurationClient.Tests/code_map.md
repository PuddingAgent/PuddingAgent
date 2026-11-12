# Tests/AgentConfigurationClient.Tests

| File | Purpose |
|---|---|
| `AgentConfigurationClient.Tests.csproj` | MSTest.Sdk/4.0.1 runner; only `ProjectReference` is the sample project; no real network, no real Core. |
| `ConfigurationInspectorTests.cs` | Fake `IAgentConfigurationApi` coverage: exact JSON line, null fields, Chinese/CRLF/quote/backslash escaping, `permission.denied` without configuration output, not-found, cancellation (130), zero `UpdateAsync`/`ReadChangesAsync` calls, injected api/writers not disposed; plus the shell's argument, base-URL and credential-file validation (exit 2 paths, no credential echo). |
