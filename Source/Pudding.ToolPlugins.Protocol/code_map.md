# Plugin Protocol
> 源指纹: *.cs=1-9c4549ce59a9, *.csproj=1-06236e08a0f4 · 条目数: 2 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PluginWire.cs` | 长度前缀 JSON 帧的编解码与请求关联。 | `PluginFrame`、`InitializePayload`、`PluginWire` | `Source/Pudding.ToolPlugins.Contracts` | 版本化帧；单帧上限 1 MiB。 |
| `Pudding.ToolPlugins.Protocol.csproj` | 插件协议工程，编译期限定只引用插件契约。 | `EnforcePluginProtocolBoundary` | — | 只允许引用 Pudding.ToolPlugins.Contracts。 |
