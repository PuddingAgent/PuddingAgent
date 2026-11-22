# Pudding.Api.Sdk
> 源指纹: *.cs=12-7bde181e843e, *.csproj=1-10bb005a15ca · 条目数: 13 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `PuddingClient.cs` | 工作区与 Agent 句柄的 SDK 入口。 | `PuddingClient`、`Workspace`、`Agent`、`AgentConfiguration` | `Source/Pudding.Api.Contracts/AgentConfiguration.cs` | 不持有宿主所有权，不做影子状态。 |
| `HttpAgentConfigurationApi.cs` | 配置契约的鉴权 HTTP 适配器。 | `HttpAgentConfigurationApi` | `Source/Pudding.Api.Contracts/AgentConfiguration.cs` | 无自动重试；变更响应丢失一律返回 unknown。 |
| `AgentWorkClient.cs` | 既有 Agent 执行门面的绑定入口。 | `AgentWorkClient`、`AgentWork`、`AgentWorkExtensions` | `Source/Pudding.Api.Sdk/HttpAgentWorkApi.cs` | 必须经 AgentRef 绑定，不隐式创建宿主。 |
| `HttpAgentWorkApi.cs` | 经鉴权 HTTP 提交与查询工作。 | `HttpAgentWorkApi` | `Source/Pudding.Api.Sdk/AgentWorkClient.cs` | 取消观测不等于取消执行。 |
| `HttpAgentWorkControlApi.cs` | 工作观测与取消的鉴权 HTTP 适配器。 | `HttpAgentWorkControlApi`、`AgentWorkControlClient` | `Source/Pudding.Api.Sdk/HttpAgentWorkApi.cs` | 取消响应丢失记为 unknown，不得当成已停止。 |
| `OperatorsClient.cs` | 受权算子目录与打分门面。 | `OperatorsClient`、`SkillValueScoring` | `Source/Pudding.Api.Contracts/OperatorEvaluation.cs` | 宿主提供绑定 API；本层无 HTTP 与重试。 |
| `ModelEvaluationsClient.cs` | 宿主绑定的分类与评审门面。 | `ModelEvaluationsClient` | `Source/Pudding.Api.Contracts/ModelEvaluation.cs` | 调用方不管理父预算、模型凭据与重试。 |
| `ExecutionBudgetClient.cs` | PA-01 只读预算门面。 | `ExecutionBudgetClient` | `Source/Pudding.Api.Contracts/ExecutionBudget.cs` | 只读授权账户，不补款、不撤销取消。 |
| `ReviewJobsClient.cs` | PA-03 评审作业门面。 | `ReviewJobsClient` | `Source/Pudding.Api.Contracts/ReviewJobs.cs` | 提交只受理作业；finding 仅建议，不生成 ID 也不重试。 |
| `ModelCatalogClient.cs` | PA-06 模型目录门面。 | `ModelCatalogClient` | `Source/Pudding.Api.Contracts/ModelCapabilities.cs` | 不暴露 provider 密钥。 |
| `AgentDirectoryClient.cs` | PA-02 借用句柄目录门面。 | `AgentDirectoryClient` | `Source/Pudding.Api.Contracts/AgentDirectory.cs` | list 与 get 不创建也不启动 Agent。 |
| `AgentDelegationClient.cs` | PA-02 委派门面。 | `AgentDelegationClient` | `Source/Pudding.Api.Contracts/AgentDelegation.cs` | 父身份与预算留在宿主侧，租约只做转发。 |
| `Pudding.Api.Sdk.csproj` | 公共 SDK 工程，编译期限定只引用公共契约。 | `EnforceSdkBoundary` | — | 只允许引用 Pudding.Api.Contracts，且不得有任何 PackageReference。 |
