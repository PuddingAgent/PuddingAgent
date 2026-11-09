# Models

| File | Responsibility |
|---|---|
| `ModelPolicyResolver.cs` | PA-06A effective configuration: instance → workspace → system layering with source/revision per field, unset purposes report `model_policy_missing`, explicit inherit-primary only, typed CAS writes with unsupported fields rejected. |
| `ModelQuoteService.cs` | PA-06C dispatch-time quote freeze: unknown price is never zero, only ExplicitFree may be free, currency must match, the window is half-open on the dispatch instant, cache-hit tokens are billed once at the cache rate, unknown usage stays unknown. |
| `ModelLeaseRegistry.cs` | PA-06C generation registry: new generations block new dispatch of revoked generations, downgrades only restrict new admission (active slots are never preempted), release is once-only and slots are freed only after the real attempt completes. |
`ModelPolicyPatchApplier.cs`: PA-06 06A patch CAS/校验（Set/Clear/InheritPrimary 显式形态；重复/矛盾 patch 拒绝；仅改 instance 层）。
`QuoteWindowAdjuster.cs`: PA-06C 跨窗口派发：冻结报价仍可用则保留；否则用新报价原子调整预留；差额超出余量或不公平价格则 Block（不复用旧价）；缺失价格不做零映射；提供按 BaseUnit 的 cost 计算（含 cache-hit 单价）。