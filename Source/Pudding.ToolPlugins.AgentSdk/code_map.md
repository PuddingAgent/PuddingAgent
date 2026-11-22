# Plugin Agent SDK
> 源指纹: *.cs=6-487b1dc1bfee, *.csproj=1-90fd38993944 · 条目数: 6 · 最近整理: 2026-11-22

插件侧 Agent SDK：把当前调用的传输层包装成类型化公开契约，插件代码不接触宿主机密与目标选择。

> 源指纹: Source/Pudding.ToolPlugins.AgentSdk/**=pending · 条目数: 7 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PluginAgentConfiguration.cs` | `Context.AgentConfiguration()` 门面：在当前调用范围内读写 Agent 配置 | `PluginAgentConfiguration.AgentConfiguration`、`IAgentConfigurationApi` | `IPluginContext` | 句柄绑定单一 Agent，换目标即失败；写结果不可确认时报 unknown 且不重试 |
| `PluginOperators.cs` | `Context.Operators()` 门面：只读算子目录与技能价值评分，校验作用域与回执 | `PluginOperators.Operators`、`OperatorsClient`、`OperatorScoreReceipt` | `IOperatorApi`、`OperatorApiException` | 评分不可得时不许折成占位 0；不重试 |
| `PluginModelEvaluations.cs` | `Context.ModelEvaluations()` 门面：文本分类与交付验收两类模型评估调用 | `PluginModelEvaluations.ModelEvaluations`、`ClassificationReceipt`、`AcceptanceReceipt` | `IModelEvaluationApi`、`ModelEvaluationException` | 证据不足的验收不得被判为接受；自报置信度必须落在 0..1 |
| `PluginEvents.cs` | 冻结的插件事件观察契约：事件信封、宿主绑定上下文与幂等审计回执 | `PluginEventEnvelope`、`IPluginEventObserver`、`IPluginAuditApi` | `PluginAuditReceipt` | 审计 `OperationId` 必须等于 `EventId`；重放返回已存在回执而非新增 |
| `PluginJobs.cs` | 冻结的插件作业契约：作业信封与三态结果（含 Unknown） | `PluginJobEnvelope`、`PluginJobResult`、`IPluginJobHandler` | `PluginJobsException` | Unknown 是第三态，不得当成成功或失败；Failed 必须带失败码 |
| `PluginExtensions.cs` | 冻结的扩展声明与处理器（观察/建议/变换/守卫）契约 | `PluginExtensionDeclaration`、`IPluginObserver`、`IPluginTransformer`、`PluginGuardDecision` | `IPluginAdvisor` | 声明必须有界；受保护字段与授权由宿主强制执行 |
| `Pudding.ToolPlugins.AgentSdk.csproj` | 插件 SDK 工程，只引用插件契约与公开 SDK | — | `Pudding.ToolPlugins.Contracts`、`Pudding.Api.Sdk` | 引用清单即白名单 |
