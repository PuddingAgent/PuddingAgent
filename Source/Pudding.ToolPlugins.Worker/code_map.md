# Plugin Worker

| File | Responsibility |
|---|---|
| `Program.cs` | Loads plugin DLL in private context; shares authoring/public SDK contracts; reports tools/hooks/workspace-event subscriptions; handles `event` delivery; async-context invocation leases reject delayed callbacks borrowing a later request; typed configuration, operator and model evaluation failures preserve unknown tool effects |
| `Pudding.ToolPlugins.Worker.csproj` | Host-independent executable referencing authoring SDK, Agent SDK, public SDK/contracts and protocol only |
