# Pudding.Api.Contracts
> 源指纹: *.cs=16-3a75e36f00b0, *.csproj=1-b3b8e4f01aab · 条目数: 17 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AgentConfiguration.cs` | 配置快照与补丁的公共契约及绑定 API。 | `AgentConfigurationPatch`、`ConfigurationUpdateRequest`、`IAgentConfigurationApi` | `Source/Pudding.Api.Sdk/HttpAgentConfigurationApi.cs` | — |
| `AgentWork.cs` | 单轮提交、回执与终态证据的公共契约。 | `AgentWorkRequest`、`AgentWorkReceipt`、`AgentWorkSnapshot`、`IAgentWorkApi` | `Source/Pudding.Api.Sdk/HttpAgentWorkApi.cs` | Completed 只是该轮执行终态，不代表 Goal 已通过验证。 |
| `AgentWorkControl.cs` | PA-02A 观测与取消的公共契约。 | `IAgentWorkControlApi`、`ObserveWorkRequest`、`CancelWorkRequest` | `Source/Pudding.Api.Sdk/HttpAgentWorkControlApi.cs` | 取消回执的 Requested 不等于已终止。 |
| `OperatorEvaluation.cs` | 打分绑定 API 与技能观测契约。 | `IOperatorApi`、`OperatorScoreRequest`、`OperatorScoreReceipt`、`SkillValueApi` | `Source/Pudding.Api.Sdk/OperatorsClient.cs` | 评分输入由调用方提供，不得当作受证遥测。 |
| `ModelEvaluation.cs` | 分类与证据评审的绑定 API，含弃答与用量。 | `IModelEvaluationApi`、`ClassificationRequest`、`AcceptanceRequest`、`EvaluationModelUsage` | `Source/PuddingRuntime/Operators/PublicApi/ModelEvaluationApiFactory.cs` | 无结束 Goal 或授予审批的权限。 |
| `PluginEvents.cs` | PA-07A 插件事件订阅与确认契约。 | `IPluginEventsApi`、`PluginEventDelivery`、`PluginEventPage` | — | owner/generation/grant/cursor 由宿主绑定，投递已授权并过滤。 |
| `PluginJobs.cs` | PA-07A 插件作业提交与查询契约。 | `IPluginJobsApi`、`SubmitPluginJob`、`PluginJobView` | — | 回执只表示已受理，完成状态须另行查询。 |
| `AgentDirectory.cs` | PA-02A 借用句柄的目录与工厂契约。 | `IAgentDirectoryApi`、`IAgentFactoryApi`、`AgentDirectoryPage`、`CreateAgentRequest` | `Source/PuddingPlatform/Services/Agents/PlatformAgentDirectoryApi.cs` | Workspace 与 actor 由宿主绑定，创建和归档用 OperationId 幂等。 |
| `AgentDelegation.cs` | PA-02A 委派与租约契约。 | `IAgentDelegationApi`、`DelegateWorkRequest`、`AcquireSubAgentRequest`、`SubAgentLease` | `Source/Pudding.Api.Sdk/AgentDelegationClient.cs` | 父身份与预算取自绑定上下文，子级限额只能收紧。 |
| `ReviewJobs.cs` | PA-03 评审作业契约。 | `IReviewJobsApi`、`SubmitReviewRequest`、`ReviewJobView` | `Source/Pudding.Api.Sdk/ReviewJobsClient.cs` | 提交只返回已受理作业；幂等键冻结目标与验收版本；finding 仅作建议。 |
| `AgentModelPolicy.cs` | PA-06A 生效模型策略契约。 | `IAgentModelPolicyApi`、`EffectiveAgentModelPolicy`、`AgentModelPolicyReceipt` | `Source/PuddingPlatform/Services/Configuration/PlatformAgentModelPolicyApi.cs` | Clear 只移除实例覆盖；回执区分持久化修订与已应用修订。 |
| `ModelCapabilities.cs` | PA-06B 模型目录契约。 | `IModelCatalogApi`、`ModelCatalogEntry`、`ModelQuery` | `Source/PuddingPlatform/Services/Models/ModelCatalogAdapter.cs` | 只暴露授权模型身份与价格来源，不含端点密钥。 |
| `GoalDecision.cs` | PA-04 只读决策快照契约。 | `IGoalDecisionApi`、`GoalDecisionSnapshot` | — | 无任意 GoalPhase 写入口；展示层不得自造结论。 |
| `CapabilityCatalog.cs` | PA-05 只读能力目录契约。 | `ICapabilityCatalogApi`、`CapabilityCatalogEntry`、`CapabilityQuery` | — | 条目不是可执行授权；权限缺口与空结果分开表达。 |
| `ExecutionBudget.cs` | PA-01 01D 只读预算快照契约。 | `IExecutionBudgetApi`、`ExecutionBudgetSnapshot`、`BudgetUnknownView` | `Source/PuddingPlatform/Services/ExecutionBudgets/ExecutionBudgetApiAdapter.cs` | 未知金额与 Token 保持 unknown，不得在视图层映射成零。 |
| `ExecutionReview.cs` | PA-08A 执行评审面板的只读投影契约。 | `ExecutionReviewSnapshot`、`ExecutionReviewBudgetView`、`ExecutionReviewFreshnessView` | — | 组件只渲染不推导控制结论；未知金额须 known=false。 |
| `Pudding.Api.Contracts.csproj` | 公共契约工程，编译期强制只依赖 BCL。 | `EnforcePublicContractBoundary` | — | EnforcePublicContractBoundary 在出现任何 ProjectReference 或 PackageReference 时构建失败。 |
