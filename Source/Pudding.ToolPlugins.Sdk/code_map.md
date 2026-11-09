# Plugin SDK

| File | Responsibility |
|---|---|
| `ToolPlugin.cs` | Author base class; only depends on plugin contracts; virtual hook/workspace-event subscriptions and handlers |
| `../../Samples/ToolPlugins/TextTools/TextTools.csproj` | Author example compiled against published reference DLLs without host project references |
