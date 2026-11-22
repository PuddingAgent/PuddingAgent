# Authorization tests

授权组件的测试：判定优先级、审批生命周期与失败即拒的边界。

> 源指纹: Source/PuddingAuthorizationTests/**=pending · 条目数: 4 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AuthorizationTestHost.cs` | 内存策略源与审批存储假件，加冻结的请求/审批夹具 | `FakePolicySource`、`FakeApprovalStore`、`AuthorizationFixtures` | `IAuthorizationPolicySource` | 测试替身，不是产品实现 |
| `AuthorizationTests.cs` | 判定优先级：系统 Deny 恒胜，其余不匹配一律拒绝 | `AuthorizationTests.SystemDeny_WinsOverPluginAllowAndAnApproval` | `AuthorizationEvaluator` | — |
| `ApprovalTests.cs` | 审批：请求幂等且冲突可检，插件不能自决，过期即拒 | `ApprovalTests.Plugin_CannotDecideItsOwnApproval` | `ApprovalCoordinator` | — |
| `PuddingAuthorizationTests.csproj` | 测试可执行工程，唯一工程引用为 `PuddingAuthorization` | — | `PuddingAuthorization` | — |
