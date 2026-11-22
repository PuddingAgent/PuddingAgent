# Plugin SDK
> 源指纹: *.cs=1-d1c162be0cbf, *.csproj=1-7d512668d1e5 · 条目数: 2 · 最近整理: 2026-11-09

| File | Responsibility |
|---|---|
| `ToolPlugin.cs` | Author base class; only depends on plugin contracts; virtual hook/workspace-event subscriptions and handlers |
| `../../Samples/ToolPlugins/TextTools/TextTools.csproj` | Author example compiled against published reference DLLs without host project references |
