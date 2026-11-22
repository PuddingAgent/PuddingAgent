# PuddingModels.Contracts
> 源指纹: *.cs=1-c435b5f23ca5, *.csproj=1-3579f92900f9 · 条目数: 0 · 最近整理: 2026-11-22

模型策略端口契约（PA-06）：定义分层配置、能力目录与可信报价，BCL-only，不暴露密钥与 endpoint。

> 源指纹: Source/PuddingModels.Contracts/**=pending · 条目数: 2 · 最近整理: 2026-11-22

| 文件 / 目录 | 用途 | 关键符号 | 关联 | 约束 |
|---|---|---|---|---|
| `ModelPolicyPorts.cs` | 定义分层模型配置、能力目录与可信报价端口 | `IAgentModelPolicyStore`、`IModelCatalogSource`、`IModelQuoteSource`、`ModelErrorCodes` | `Source/PuddingModels/ModelPolicyResolver.cs` | 未知价格不得映射成零，只有 `ExplicitFree` 允许零报价；配置写入必须带期望 revision 的 CAS |
| `PuddingModels.Contracts.csproj` | 构建期锁死端口工程的零依赖边界 | — | — | `EnforceModelsPortsBoundary` 目标会失败构建：出现任何 ProjectReference 或 PackageReference 即报错 |
