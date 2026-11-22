# Plugin Worker
> 源指纹: *.cs=1-e3b4e661f5ff, *.csproj=1-4498ba8f4e2d · 条目数: 2 · 最近整理: 2026-11-22

插件进程宿主：在私有加载上下文中装载插件 DLL，并向宿主上报工具、钩子与工作区事件订阅。

> 源指纹: Source/Pudding.ToolPlugins.Worker/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `Program.cs` | 插件工作进程入口：私有上下文装载插件，处理事件投递与工具回调 | `Program`、`InvokeAgentApiAsync`、`RequestArchiveAsync` | `Pudding.ToolPlugins.Protocol`、`Pudding.ToolPlugins.Sdk` | 回调只使用发起调用的会话；异步上下文的调用租约拒绝延迟回调借用后续请求；未知工具副作用必须原样保留 |
| `Pudding.ToolPlugins.Worker.csproj` | 与宿主无关的可执行工程，引用协议、插件 SDK 与 Agent SDK | — | `Pudding.ToolPlugins.Protocol`、`Pudding.ToolPlugins.Sdk`、`Pudding.ToolPlugins.AgentSdk` | — |
