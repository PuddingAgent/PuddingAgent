# Authorization
> 源指纹: *.cs=2-5a0410db5954, *.csproj=1-b7f9dbcb8ed7 · 条目数: 2 · 最近整理: 2026-11-22

授权判定与审批生命周期：纯函数式决策 + 按修订号 CAS 的审批协调。

> 源指纹: Source/PuddingAuthorization/**=pending · 条目数: 3 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AuthorizationEvaluator.cs` | 纯授权判定：系统 Deny 恒胜，插件规则只收紧且在无规则时拒绝 | `AuthorizationEvaluator.EvaluateAsync` | `AuthorizationDecision`、`IAuthorizationPolicySource` | 审批只在动作/资源/参数哈希/策略版本全匹配且未过期时才放行；策略失败必须 Unavailable，不得 fail-open |
| `ApprovalCoordinator.cs` | 审批生命周期：按操作号幂等请求并对决策做修订号 CAS，TTL 有界 | `ApprovalCoordinator.RequestAsync`、`ApprovalCoordinator.DecideAsync`、`ApprovalRequest` | `ApprovalRecord` | 只有宿主或系统身份可决策；已过期或非 Pending 的审批拒绝 |
| `PuddingAuthorization.csproj` | 实现工程，唯一工程引用为授权契约 | — | `PuddingAuthorization.Contracts` | — |
