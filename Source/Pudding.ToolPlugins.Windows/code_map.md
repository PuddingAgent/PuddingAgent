# Plugin Windows Supervisor
> 源指纹: *.cs=2-ed2ecf4514d7, *.csproj=1-6c229f6b1bae · 条目数: 3 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `WindowsPluginWorkerFactory.cs` | 隐藏 worker 的命名管道与经 PID 校验的受限 RPC。 | `WindowsPluginWorkerFactory` | `Source/Pudding.ToolPlugins.Protocol/PluginWire.cs` | 连接须校验对端 PID，同用户命名管道。 |
| `WorkerJob.cs` | Job Object 的子进程树归属与 kill-on-close 限额。 | `WorkerJob` | `Source/Pudding.ToolPlugins.Windows/WindowsPluginWorkerFactory.cs` | 512 MiB 内存上限；关句柄即杀整棵子树。 |
| `Pudding.ToolPlugins.Windows.csproj` | OS 监督进程工程，编译期限定只引用协议与契约。 | `EnforcePluginWindowsBoundary` | — | 只允许 Pudding.ToolPlugins.Protocol 与 Contracts。 |
