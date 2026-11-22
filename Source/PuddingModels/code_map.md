# Models
> 源指纹: *.cs=5-3c254e7dbc07, *.csproj=1-d0a5aa0dc09e · 条目数: 6 · 最近整理: 2026-11-09

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelPolicyResolver.cs` | 模型策略的分层解析与 CAS 写入。 | `ModelPolicyResolver` | `Source/PuddingModels/ModelPolicyPatchApplier.cs` | 未设置用途报 model_policy_missing；不支持字段拒绝。 |
| `ModelPolicyPatchApplier.cs` | 策略补丁的 CAS 与显式 Set/Clear 应用。 | `ModelPolicyPatchApplier` | `Source/PuddingModels/ModelPolicyResolver.cs` | 重复或矛盾的补丁必须拒绝，只改实例层。 |
| `ModelQuoteService.cs` | 派发时刻的报价冻结与结算计量。 | `ModelQuoteService`、`ModelSettlement`、`ModelUsage` | `Source/PuddingModels/QuoteWindowAdjuster.cs` | 未知价格不映射为零；窗口在派发瞬间半开；命中按缓存价计一次。 |
| `QuoteWindowAdjuster.cs` | 跨窗口派发的报价保留与原子调价判定。 | `QuoteWindowAdjuster`、`QuoteAdjustment`、`QuoteDispatchDecision` | `Source/PuddingModels/ModelQuoteService.cs` | 差额超余量或价格不公即 Block，不复用旧价。 |
| `ModelLeaseRegistry.cs` | 模型代次注册与在飞额度约束。 | `ModelLeaseRegistry`、`ModelLease`、`ModelGeneration` | `Source/PuddingModels/ModelQuoteService.cs` | 新代次阻断被吊销代次的新派发；释放只一次。 |
| `PuddingModels.csproj` | Models 工程，编译期限定只引用端口与公共契约。 | `EnforceModelsBoundary` | — | 只允许 PuddingModels.Contracts 与 Pudding.Api.Contracts，无包引用。 |
