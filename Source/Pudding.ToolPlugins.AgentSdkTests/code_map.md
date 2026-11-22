# Plugin Agent SDK Tests
> 源指纹: *.cs=6-8cec9396659f, *.csproj=1-186857b10672 · 条目数: 5 · 最近整理: 2026-11-22

插件 Agent SDK 的契约测试：验证门面只经一次传输调用，且非法回执不会被当成成功。

> 源指纹: Source/Pudding.ToolPlugins.AgentSdkTests/**=pending · 条目数: 7 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentSdkTests.cs` | 配置门面经作用域传输调用，覆盖异域回执、拒绝与写结果未知 | `AgentSdkTests.ConfigurationFacade_UsesScopedTransport_AndPublicContracts` | `PluginAgentConfiguration` | — |
| `PluginOperatorsTests.cs` | 算子评分：作用域限定、畸形目录与占位 0 不被接受 | `PluginOperatorsTests.ScopeFreeWire_AndTypedInputReachOneTransportCall` | `PluginOperators` | — |
| `PluginModelEvaluationTests.cs` | 模型评估：拒绝被篡改的作用域与用量，接受必须有证据 | `PluginModelEvaluationTests.AcceptanceWithoutSupportingChecksCannotBecomeValid` | `PluginModelEvaluations` | — |
| `PluginEventObserverTests.cs` | 冻结事件信封与审计契约：校验负载并把重放收敛为同一逻辑审计 | `PluginEventObserverTests.AuditReceipt_ReportsReplayWithoutChangingTheLogicalAudit` | `PluginEvents` | — |
| `PluginJobContractTests.cs` | 作业信封边界与结果三态：Failed 需失败码，Completed 禁止 | `PluginJobContractTests.Result_RequiresFailureCodeForFailedAndForbidsItForCompleted` | `PluginJobs` | — |
| `PluginExtensionContractTests.cs` | 扩展声明边界与变换/守卫的显式成功失败形态 | `PluginExtensionContractTests.Declarations_RequireBoundedIdentityPointPriorityAndVersion` | `PluginExtensions` | — |
| `Pudding.ToolPlugins.AgentSdkTests.csproj` | 测试可执行工程，唯一工程引用为插件 Agent SDK | — | `Pudding.ToolPlugins.AgentSdk` | — |
