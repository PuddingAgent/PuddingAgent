# Plugin Runtime Tests
> 源指纹: *.cs=4-ca0c03067c9d, *.csproj=1-42d206944d5d · 条目数: 5 · 最近整理: 2026-11-10

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `HostToolMountTests.cs` | 服务化插件的吊销、drain 与跨会话拒绝边界。 | `HostToolMountTests` | `Source/Pudding.ToolPlugins.Runtime/HostToolPlugins.cs` | 重载须幂等。 |
| `PluginRuntimeTests.cs` | 校验安装边界与加载竞态，覆盖配额拒绝。 | `PluginRuntimeTests` | `PluginValidation`、`PluginPackageStore` | 建议回传失败不得中断宿主。 |
| `AgentCallbackLifecycleTests.cs` | 回调生命周期中的授权冻结与不确定停止。 | `AgentCallbackLifecycleTests` | `Source/Pudding.ToolPlugins.Runtime/PluginManager.cs` | 不确定停止不得报成已停止。 |
| `OperatorManifestTests.cs` | 算子清单的 Core API 版本门槛与拒绝面。 | `OperatorManifestTests` | `Source/Pudding.ToolPlugins.Runtime/PluginValidation.cs` | 1.2 与 1.3 的能力差异不得放宽。 |
| `Tests/PuddingToolPlugins.IntegrationTests/RealWorkerTests.cs` | 真实 DLL 与 Windows 监督进程的验收入口。 | `RealWorkerTests` | `Source/Pudding.ToolPlugins.Windows/WindowsPluginWorkerFactory.cs` | 覆盖崩溃、超时、输出洪水与子进程树。 |
