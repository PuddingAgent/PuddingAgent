# Plugin Worker Fixtures
> 源指纹: *.cs=5-1720d7ca7edb, *.csproj=1-a1b5f450ce51 · 条目数: 4 · 最近整理: 2026-11-09

| File | Responsibility |
|---|---|
| `FixturePlugin.cs` | Managed plugin fixture for echo, host callback, advice and controlled Worker faults |
| `AgentConfigurationPlugin.cs` | Public configuration facade usage and explicit forged-scope rejection probe; runs in real Worker |
| `OperatorPlugin.cs` | Public scoring facade, catalog and forged-scope probe; runs in real Worker against original skill scorer |
| `ModelEvaluationPlugin.cs` | Public classification/review facade and forged-parent probe; real Worker callbacks use host-owned model resources |
