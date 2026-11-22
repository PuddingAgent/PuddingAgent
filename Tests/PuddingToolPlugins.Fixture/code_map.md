# Plugin Worker Fixtures
> 源指纹: *.cs=5-1720d7ca7edb, *.csproj=1-a1b5f450ce51 · 条目数: 5 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `FixturePlugin.cs` | 托管插件夹具：回声、宿主回调与受控故障。 | `FixturePlugin`、`FailedInitializationPlugin` | `Source/Pudding.ToolPlugins.Sdk/ToolPlugin.cs` | 故障注入点不得改变宿主契约。 |
| `AgentConfigurationPlugin.cs` | 配置门面用法与伪造作用域拒绝探针。 | `AgentConfigurationPlugin` | `Source/Pudding.ToolPlugins.AgentSdk` | 在真实 Worker 内运行。 |
| `OperatorPlugin.cs` | 打分门面与目录用法及伪造作用域探针。 | `OperatorPlugin` | `Source/Pudding.ToolPlugins.AgentSdk` | 对原始技能打分器运行。 |
| `ModelEvaluationPlugin.cs` | 分类与评审门面用法及伪造父身份探针。 | `ModelEvaluationPlugin` | `Source/Pudding.ToolPlugins.AgentSdk` | 真实回调只用宿主模型资源。 |
| `WorkspaceObserverPlugin.cs` | 零工具工作区观察者的主动归档与被动消费两态。 | `WorkspaceObserverPlugin`、`WorkspaceObserverPassivePlugin` | `Source/Pudding.ToolPlugins.Sdk/ToolPlugin.cs` | 订阅型插件的 ExecuteAsync 恒抛 plugin.not_found。 |
