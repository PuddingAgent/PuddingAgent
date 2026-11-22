# Pudding.Api.SdkTests
> 源指纹: *.cs=8-a432d3051fda, *.csproj=1-c798d3ea2735 · 条目数: 8 · 最近整理: 2026-11-12

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `SdkTests.cs` | SDK 基础契约：操作身份、丢失响应与回执校验。 | `SdkTests` | `Source/Pudding.Api.Sdk/PuddingClient.cs` | 丢失响应必须记为 unknown，不得自动重试。 |
| `WorkSdkTests.cs` | 工作门面的提交与查询往返。 | `WorkSdkTests` | `Source/Pudding.Api.Sdk/AgentWorkClient.cs` | 取消观测不得发出工作取消。 |
| `WorkControlSdkTests.cs` | 观测与取消路由，不安全 ID 的前置拒绝。 | `WorkControlSdkTests` | `Source/Pudding.Api.Sdk/HttpAgentWorkControlApi.cs` | 取消响应丢失记为 unknown 而非已停止。 |
| `AgentFacadeClientTests.cs` | 目录与委派门面的借用句柄转发。 | `AgentFacadeClientTests` | `Source/Pudding.Api.Sdk/AgentDirectoryClient.cs`、`Source/Pudding.Api.Sdk/AgentDelegationClient.cs` | 门面不得自造身份；缺席返回 null。 |
| `ExecutionBudgetClientTests.cs` | 预算门面的原样转发与异常不吞。 | `ExecutionBudgetClientTests` | `Source/Pudding.Api.Sdk/ExecutionBudgetClient.cs` | 缺失账户返回 null，不得编造预算。 |
| `ModelCatalogClientTests.cs` | 模型目录查询与选择的转发。 | `ModelCatalogClientTests` | `Source/Pudding.Api.Sdk/ModelCatalogClient.cs` | 不得暴露密钥；类型化失败不得被吞。 |
| `OperatorsClientTests.cs` | 打分门面保留输入与显式选项且不重试。 | `OperatorsClientTests` | `Source/Pudding.Api.Sdk/OperatorsClient.cs` | — |
| `ReviewJobsClientTests.cs` | 评审作业提交、查询与取消的原样转发。 | `ReviewJobsClientTests` | `Source/Pudding.Api.Sdk/ReviewJobsClient.cs` | 门面不得改写 finding。 |
