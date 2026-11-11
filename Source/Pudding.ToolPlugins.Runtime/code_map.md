# Plugin Runtime

| File | Responsibility |
|---|---|
| `PluginValidation.cs` | Strict manifest/schema subset, relative paths, activation-specific tool names and workspace-event allowlist; Core API 1.3 permits only three explicit model evaluation capabilities |
| `PluginPackageStore.cs` | Metadata-only discovery, bounded ZIP import, immutable content identities and pre-load verification |
| `HostToolPlugins.cs` | Installation-owned tool manifests and per-workspace/Agent tool activation, stale handle rejection and in-flight draining |
| `PluginManager.cs` | Scoped/host activation identity, immutable grants, per-tool write effects, generation revocation, callback drain and workspace-event delivery with per-activation failure isolation; worker port only |
| `PluginCoreServices.cs` | Bounded workspace/asset reads and per-agent/plugin private state; live capability lease checked by manager |
