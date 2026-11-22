# Authorization ports

授权端口的 BCL-only 契约：决策结果、策略规则、宿主绑定请求与审批记录存储。

> 源指纹: Source/PuddingAuthorization.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `AuthorizationPorts.cs` | 定义授权决策与审批端口：四态结果与带版本的有效期规则、审批 CAS 存储 | `AuthorizationDecision`、`AuthorizationRule`、`IAuthorizationPolicySource`、`ApprovalRecord` | `AuthorizationRequest`、`ApprovalState` | 审批绑定由宿主出具，插件不能决定自己的请求；UI 与持久化适配器留在组件外 |
| `PuddingAuthorization.Contracts.csproj` | 纯契约工程，无工程引用与包依赖 | — | `PuddingAuthorization.Contracts` | 保持 BCL-only |
