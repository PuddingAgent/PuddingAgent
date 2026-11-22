# Plugin Runtime
> 源指纹: *.cs=5-5ca060de472b, *.csproj=1-f8c822bbaeb9 · 条目数: 6 · 最近整理: 2026-11-11

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PluginValidation.cs` | 清单与 schema 子集的严格校验，含相对路径与工具名白名单。 | `PluginValidation` | `Source/Pudding.ToolPlugins.Contracts` | Core API 1.3 只放开三项模型评估能力，其余一律拒绝。 |
| `PluginPackageStore.cs` | 只读元数据发现、受限 ZIP 导入与加载前校验。 | `PluginPackageStore`、`PluginCatalogItem`、`PluginCatalogSnapshot` | `Source/Pudding.ToolPlugins.Runtime/PluginValidation.cs` | 内容身份不可变，导入后不得原地改写。 |
| `HostToolPlugins.cs` | 安装态工具清单与按作用域激活。 | `HostToolPlugins`、`HostToolPackage`、`HostToolMounts` | `Source/Pudding.ToolPlugins.Runtime/PluginManager.cs` | 过期句柄必须拒绝，飞行中的调用需 drain。 |
| `PluginManager.cs` | 管理激活授权与写效果，并负责代次吊销和回调 drain。 | `PluginManager`、`PluginActivation`、`PluginAuthorization`、`PluginCallable` | `HostToolPlugins`、`PluginCoreServices` | 只走 worker 端口；按激活隔离故障。 |
| `PluginCoreServices.cs` | 受限的工作区与资源读取，按 Agent 与插件隔离私有状态。 | `PluginCoreServices` | `Source/Pudding.ToolPlugins.Runtime/PluginManager.cs` | 能力租约由 manager 实时校验。 |
| `Pudding.ToolPlugins.Runtime.csproj` | 插件运行时工程，编译期限定只引用插件契约。 | `EnforcePluginRuntimeBoundary` | — | 宿主与 OS 适配只以端口形式接入。 |
